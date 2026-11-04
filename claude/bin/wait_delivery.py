"""Wait for one dispatched job to reach a terminal state, then exit so the Lead session is woken.
Python on purpose: PowerShell hangs when Defender's engine hangs, and a watcher that cannot exit wakes nobody.
Local only, no model calls. Exit codes: 0 delivered (READY.json), 3 executor ended without READY,
4 executor process gone without READY, 2 timed out."""
import argparse
import json
import os
import sys
import time
from datetime import datetime
from pathlib import Path


def process_alive(pid):
    if os.name != 'nt':
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        return True
    import ctypes
    kernel = ctypes.windll.kernel32
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x00100000, False, pid)  # SYNCHRONIZE
    if not handle:
        return False
    running = kernel.WaitForSingleObject(handle, 0) == 0x102  # WAIT_TIMEOUT
    kernel.CloseHandle(handle)
    return running


def finish(job_dir, code, state, detail):
    with (job_dir / 'watch.log').open('a', encoding='utf-8') as log:
        log.write(f'{datetime.now():%Y-%m-%dT%H:%M:%S} {state} {detail}\n')
    print(f'DELIVERY-WATCH job={job_dir} state={state} {detail}', flush=True)
    sys.exit(code)


def ready_status(ready):
    try:
        return json.loads(ready.read_text(encoding='utf-8-sig')).get('status', 'unknown')
    except (OSError, ValueError):
        return 'unparseable'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--job-dir', required=True, type=Path)
    parser.add_argument('--process-id', type=int, default=0)
    parser.add_argument('--terminal-file', action='append', default=[], type=Path)
    parser.add_argument('--timeout-minutes', type=float, default=720)
    parser.add_argument('--interval-seconds', type=int, default=15)
    args = parser.parse_args()

    ready = args.job_dir / 'READY.json'
    deadline = time.monotonic() + args.timeout_minutes * 60
    with (args.job_dir / 'watch.log').open('a', encoding='utf-8') as log:
        log.write(f'{datetime.now():%Y-%m-%dT%H:%M:%S} watching pid={args.process_id} timeout_min={args.timeout_minutes}\n')
    while True:
        if ready.exists():
            finish(args.job_dir, 0, 'ready', f'ready_status={ready_status(ready)}')
        for marker in args.terminal_file:
            if marker.exists():
                time.sleep(5)
                if not ready.exists():
                    finish(args.job_dir, 3, 'ended-without-ready', f'marker={marker}')
        if args.process_id and not process_alive(args.process_id):
            time.sleep(5)
            if not ready.exists():
                finish(args.job_dir, 4, 'process-gone-without-ready', f'pid={args.process_id}')
        if time.monotonic() >= deadline:
            finish(args.job_dir, 2, 'timeout', f'after_min={args.timeout_minutes}')
        time.sleep(args.interval_seconds)


if __name__ == '__main__':
    main()
