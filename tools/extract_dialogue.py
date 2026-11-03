"""Extract a Claude Code session log (.jsonl) into a Markdown file holding only the conversation.

把 Claude Code 会话记录提取成只含对话正文的 Markdown，换新会话交接时给新 Lead 读。
保留：用户的消息（含 Lead 干活时的中途插话）、Lead 每一回合最后对用户说的那段话。
去掉：工具调用与输出、思考过程、系统提醒、后台任务通知（只留一个小标记）、
      回合中间的过程话（"先看一下……"之类）、上下文压缩的续接摘要（只留标记）。图片只留"[截图]"占位。

usage: python tools/extract_dialogue.py <session.jsonl> [out.md] [--user 用户] [--lead Lead] [--utc-offset 8]
Session logs live under ~/.claude/projects/<project-dir>/<session-id>.jsonl.
"""
import argparse
import json
import re
from datetime import datetime, timedelta, timezone
from pathlib import Path

REMINDER = re.compile(r"<system-reminder>.*?</system-reminder>", re.S)
IMAGE = re.compile(r"\[Image: source: [^\]]*\]")
CONTINUED = "This session is being continued from a previous conversation"


def text_blocks(content):
    if isinstance(content, str):
        return [content]
    return [block.get("text", "") for block in content or [] if isinstance(block, dict) and block.get("type") == "text"]


def clean(text):
    return IMAGE.sub("[截图]", REMINDER.sub("", text)).strip()


def is_notification(text):
    return text.startswith("[SYSTEM NOTIFICATION") or "<task-notification>" in text


def main():
    ap = argparse.ArgumentParser(description="Extract the dialogue from a Claude Code session log.")
    ap.add_argument("src")
    ap.add_argument("dst", nargs="?")
    ap.add_argument("--user", default="用户")
    ap.add_argument("--lead", default="Lead")
    ap.add_argument("--utc-offset", type=float, default=8)
    args = ap.parse_args()
    src = Path(args.src)
    dst = Path(args.dst) if args.dst else src.with_suffix(".dialogue.md")
    local = timezone(timedelta(hours=args.utc_offset))

    def stamp_of(ts):
        try:
            return datetime.fromisoformat(ts.replace("Z", "+00:00")).astimezone(local).strftime("%Y-%m-%d %H:%M")
        except ValueError:
            return ts[:16].replace("T", " ")

    lines = [f"# 对话正文：{src.stem}\n\n（只含对话文字：{args.lead}每回合只保留最后对{args.user}说的话；"
             f"后台任务通知、上下文压缩续接摘要只留标记；时间为 UTC{args.utc_offset:+g}。）\n"]
    turns = 0
    pending = None  # (stamp, text) of the lead's latest words in the current turn

    def flush():
        nonlocal pending, turns
        if pending:
            lines.append(f"\n## {args.lead}  {pending[0]}\n")
            lines.append(pending[1] + "\n")
            turns += 1
            pending = None

    with src.open(encoding="utf-8") as f:
        for raw in f:
            try:
                rec = json.loads(raw)
            except json.JSONDecodeError:
                continue
            kind = rec.get("type")
            stamp = stamp_of(rec.get("timestamp") or "")
            att = rec.get("attachment") or {}
            if kind == "attachment" and att.get("type") == "queued_command" and att.get("humanTurn"):
                prompt = att.get("prompt")
                parts = [prompt] if isinstance(prompt, str) else text_blocks(prompt)
                parts = [clean(p) for p in parts if p and p.strip()]
                if parts:
                    flush()
                    lines.append(f"\n## {args.user}（中途插话）  {stamp}\n")
                    lines.append("\n\n".join(parts) + "\n")
                    turns += 1
                continue
            if kind not in ("user", "assistant"):
                continue
            parts = [p for p in (clean(t) for t in text_blocks((rec.get("message") or {}).get("content"))) if p]
            if not parts:
                continue
            if kind == "assistant":
                pending = (stamp, "\n\n".join(parts))
                continue
            flush()
            if all(is_notification(p) for p in parts):
                lines.append(f"\n（{stamp} 后台任务结束）\n")
                continue
            parts = [p for p in parts if not is_notification(p)]
            if parts and parts[0].startswith(CONTINUED):
                lines.append(f"\n（{stamp} 上下文压缩，续接）\n")
                continue
            lines.append(f"\n## {args.user}  {stamp}\n")
            lines.append("\n\n".join(parts) + "\n")
            turns += 1
    flush()
    dst.write_text("".join(lines), encoding="utf-8")
    print(f"turns={turns} out={dst} bytes={dst.stat().st_size}")


if __name__ == "__main__":
    main()
