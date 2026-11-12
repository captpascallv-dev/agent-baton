"""POSIX-only detached process launcher; prints the actual child's PID.

No shell interpolation, inherited terminal handles, or persistent daemon.
The caller must separately bound/monitor the detached job.
"""
import argparse
import os
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cwd', required=True)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if os.name == 'nt':
        parser.error('POSIX only; Windows uses its existing launcher')
    if not command:
        parser.error('missing command')
    child = subprocess.Popen(command, cwd=args.cwd, stdin=subprocess.DEVNULL,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             close_fds=True, start_new_session=True)
    print(child.pid, flush=True)


if __name__ == '__main__':
    main()
