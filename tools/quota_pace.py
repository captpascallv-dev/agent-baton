"""Read the HUD quota snapshot and list each quota's remaining share, reset time and even-burn pace.

读 hud/ 写出的额度快照，列出各额度剩多少、什么时候重置、按均匀消耗现在应剩多少，
并标出“落后”（剩得比均匀消耗应剩多 10 个点以上）和“48 小时内重置仍剩很多”（剩 20% 以上）。
Lead 派活前看一眼，就知道哪家额度快作废了该先用。

usage: python tools/quota_pace.py [snapshot.json] [--utc-offset 8]
default snapshot: %LOCALAPPDATA%\\CodexUsageHUD\\quota-snapshot.json
"""
import argparse
import datetime as dt
import json
import os
import sys

DEFAULT_SNAPSHOT = os.path.expandvars(r'%LOCALAPPDATA%\CodexUsageHUD\quota-snapshot.json')


def parse_utc(text):
    value = dt.datetime.fromisoformat(text.replace('Z', '+00:00'))
    return value if value.tzinfo else value.replace(tzinfo=dt.timezone.utc)


def main():
    ap = argparse.ArgumentParser(description='Quota pace from the HUD snapshot.')
    ap.add_argument('snapshot', nargs='?', default=DEFAULT_SNAPSHOT)
    ap.add_argument('--utc-offset', type=float, default=8, help='hours added to UTC for display (default 8)')
    args = ap.parse_args()
    local = dt.timezone(dt.timedelta(hours=args.utc_offset))
    with open(args.snapshot, encoding='utf-8-sig') as fh:
        snapshot = json.load(fh)
    now = dt.datetime.now(dt.timezone.utc)
    out = []
    for slot in snapshot['slots']:
        for w in slot.get('windows', []):
            rem = float(w['remaining_percent'])
            if not w.get('resets_at_utc'):
                out.append(f"{slot['slot']:15} {w['pool']:22} left {rem:5.1f}%  no reset time")
                continue
            reset = parse_utc(w['resets_at_utc'])
            left_h = (reset - now).total_seconds() / 3600
            win = w.get('window_minutes')
            even = 100 * left_h * 60 / win if win else None
            flag = ''
            if even is not None and win >= 1440 and rem - even >= 10:
                flag = f'  behind by {rem - even:.0f} pts'
            if (win is None or win >= 1440) and 0 < left_h <= 48 and rem >= 20:
                flag += '  resets within 48h with plenty left'
            even_s = f'{even:5.1f}%' if even is not None else '  -  '
            out.append(f"{slot['slot']:15} {w['pool']:22} left {rem:5.1f}%  resets {reset.astimezone(local):%m-%d %H:%M}"
                       f"  in {left_h:6.1f}h  even-burn {even_s}{flag}")
    sys.stdout.buffer.write(('\n'.join(out) + '\n').encode('utf-8'))


if __name__ == '__main__':
    main()
