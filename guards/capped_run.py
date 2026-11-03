"""Run any command tree inside a Windows Job with a hard memory cap and a wall timeout.

所有 agent 跑测试/构建的内存上限入口。
The process starts suspended, joins the Job, the limit is read back, then it resumes;
every child it spawns lives in the same Job. When committed memory reaches the cap,
allocations fail and the watchdog ends the whole tree, so the machine stays usable.

usage: python capped_run.py [--mem-gb 2] [--timeout 900] [--cwd DIR] [--log FILE] -- cmd args...
exit: the command's own code; 124 timeout; 137 memory cap hit; 125 runner error.
Windows only; needs pywin32. CAPPED_RUN_MAX_GB sets the per-run ceiling (default 6).
"""
from __future__ import annotations

import argparse
import msvcrt
import os
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

import win32api
import win32con
import win32event
import win32job
import win32process

MAX_MEM_GB = float(os.environ.get("CAPPED_RUN_MAX_GB", "6"))  # one run never takes more than this


def resolve(argv: list[str]) -> list[str]:
    exe = shutil.which(argv[0]) or argv[0]
    if exe.lower().endswith((".cmd", ".bat")):
        return [os.environ.get("COMSPEC", r"C:\Windows\System32\cmd.exe"), "/d", "/c", exe, *argv[1:]]
    return [exe, *argv[1:]]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--mem-gb", type=float, default=2.0)
    ap.add_argument("--timeout", type=int, default=900)
    ap.add_argument("--cwd", default=os.getcwd())
    ap.add_argument("--log", default="")
    ap.add_argument("--tail", type=int, default=40)
    ap.add_argument("cmd", nargs=argparse.REMAINDER)
    args = ap.parse_args()
    cmd = args.cmd[1:] if args.cmd[:1] == ["--"] else args.cmd
    if not cmd:
        ap.error("missing command after --")
    if not 0 < args.mem_gb <= MAX_MEM_GB:
        ap.error(f"--mem-gb must be in (0, {MAX_MEM_GB}]")
    limit = int(args.mem_gb * 1024 ** 3)
    log = args.log or os.path.join(tempfile.gettempdir(), f"capped-run-{uuid.uuid4().hex[:8]}.log")
    argv = resolve(cmd)

    job = win32job.CreateJobObject(None, "")
    info = win32job.QueryInformationJobObject(job, win32job.JobObjectExtendedLimitInformation)
    info["BasicLimitInformation"]["LimitFlags"] |= (
        win32job.JOB_OBJECT_LIMIT_JOB_MEMORY | win32job.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)
    info["JobMemoryLimit"] = limit
    win32job.SetInformationJobObject(job, win32job.JobObjectExtendedLimitInformation, info)

    process = thread = None
    code, verdict = 125, "runner_error"
    started = time.monotonic()
    peak = 0
    try:
        with open(log, "wb") as out, open(os.devnull, "rb") as null_in:
            oh = msvcrt.get_osfhandle(out.fileno())
            ih = msvcrt.get_osfhandle(null_in.fileno())
            os.set_handle_inheritable(oh, True)
            os.set_handle_inheritable(ih, True)
            si = win32process.STARTUPINFO()
            si.dwFlags |= win32con.STARTF_USESTDHANDLES | win32con.STARTF_USESHOWWINDOW
            si.wShowWindow = win32con.SW_HIDE
            si.hStdOutput = oh
            si.hStdError = oh
            si.hStdInput = ih
            process, thread, pid, _ = win32process.CreateProcess(
                None, subprocess.list2cmdline(argv), None, None, True,
                win32con.CREATE_SUSPENDED | win32con.CREATE_NO_WINDOW, None, args.cwd, si)
            win32job.AssignProcessToJobObject(job, process)
            readback = win32job.QueryInformationJobObject(job, win32job.JobObjectExtendedLimitInformation)
            if readback["JobMemoryLimit"] != limit:
                raise RuntimeError("memory limit readback failed; target never resumed")
            win32process.ResumeThread(thread)
            deadline = started + args.timeout
            while True:
                if win32event.WaitForSingleObject(process, 500) == win32con.WAIT_OBJECT_0:
                    code, verdict = win32process.GetExitCodeProcess(process), "exited"
                    break
                peak = win32job.QueryInformationJobObject(
                    job, win32job.JobObjectExtendedLimitInformation)["PeakJobMemoryUsed"]
                if peak >= limit * 0.97:
                    code, verdict = 137, "memory_cap_hit"
                    win32job.TerminateJobObject(job, 137)
                    break
                if time.monotonic() >= deadline:
                    code, verdict = 124, "timeout"
                    win32job.TerminateJobObject(job, 124)
                    break
            peak = win32job.QueryInformationJobObject(
                job, win32job.JobObjectExtendedLimitInformation)["PeakJobMemoryUsed"]
            # Leftover children (servers, workers) die with the run, never linger.
            win32job.TerminateJobObject(job, code if code else 0)
            for _ in range(100):
                if not win32job.QueryInformationJobObject(
                        job, win32job.JobObjectBasicAccountingInformation)["ActiveProcesses"]:
                    break
                time.sleep(0.05)
    except Exception as exc:  # noqa: BLE001 - report and make sure nothing keeps running
        verdict = f"runner_error: {exc!r}"
        if process is not None:
            try:
                win32api.TerminateProcess(process, 125)
            except Exception:
                pass
    finally:
        job.Close()
        if thread is not None:
            thread.Close()
        if process is not None:
            process.Close()

    try:
        with open(log, "rb") as fh:
            lines = fh.read().decode("utf-8", "replace").splitlines()
    except OSError:
        lines = []
    # A GBK console must not turn the real exit code into 1 on one odd character.
    sys.stdout.reconfigure(errors="replace")
    for line in lines[-args.tail:]:
        print(line)
    print(f"[capped_run] {verdict} code={code} peak={peak / 1024 ** 3:.2f}GB "
          f"cap={args.mem_gb}GB elapsed={time.monotonic() - started:.1f}s log={log}")
    return code


if __name__ == "__main__":
    sys.exit(main())
