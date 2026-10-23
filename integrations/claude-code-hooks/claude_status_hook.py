"""Claude Code 状态钩子：把每个会话现在是在干活、休眠还是等你确认，写成一个小状态文件，给研究室读。

挂法：settings.json 里每个钩子都用 async + exec 形式（command 填 python，args 填本脚本）。
后台运行，对话不等它；无论发生什么都不往外输出、退出码 0，不会拦住或影响对话。

用法：python -I -S claude_status_hook.py <输出目录> <phase>
  phase：running（收到消息 / 工具跑完）  idle（回答结束 / 出错 / 会话开始）
         waiting（等你批准或回答）  model（切换了模型）  end（会话结束，删掉自己的文件）

每个会话一个文件 claude-status-<Claude Code 进程号>-<会话编号的哈希>.json，只写状态、模型、时间和
拉起本钩子的 Claude Code 进程号（研究室据此判断会话还在不在，进程号放进文件名是为了不用打开文件就能
先筛掉已关的会话）；不写会话原始编号、消息内容或路径。
"""
import hashlib
import json
import os
import re
import sys
import time

PHASES = ('running', 'idle', 'waiting', 'model', 'end')
MAX_INPUT = 1 << 20          # 钩子输入里可能带整段消息，只读前 1 MiB 就够拿 session_id
TRANSCRIPT_TAIL = 256 * 1024  # 找模型名只看会话记录末尾
MODEL_RE = re.compile(r'^[A-Za-z0-9._:\-\[\]]{1,80}$')
PRUNE_AFTER = 7 * 86400


def iso(t):
    return time.strftime('%Y-%m-%dT%H:%M:%S', time.gmtime(t)) + '.%03dZ' % int(t % 1 * 1000)


def clean_model(value):
    if isinstance(value, dict):
        value = value.get('id') or value.get('model')
    if isinstance(value, str) and MODEL_RE.match(value) and not value.startswith('<'):
        return value
    return None


def transcript_model(path):
    """会话记录是 JSONL；每条助手回复都带 "model"。取末尾最后一个真实模型名。"""
    if not isinstance(path, str) or not path:
        return None
    try:
        with open(path, 'rb') as f:
            f.seek(0, 2)
            size = f.tell()
            f.seek(max(0, size - TRANSCRIPT_TAIL))
            tail = f.read().decode('utf-8', 'replace')
    except OSError:
        return None
    for found in reversed(re.findall(r'"model"\s*:\s*"([^"]{1,80})"', tail)):
        model = clean_model(found)
        if model:
            return model
    return None


def read_json(path, limit=16 * 1024):
    try:
        with open(path, 'rb') as f:
            raw = f.read(limit + 1)
        return json.loads(raw.decode('utf-8')) if len(raw) <= limit else None
    except (OSError, ValueError):
        return None


def write_atomic(path, record):
    tmp = '%s.%d.tmp' % (path, os.getpid())
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(record, f, ensure_ascii=False)
    for _ in range(5):  # 研究室正好在读时 Windows 会拒绝替换，稍等重试
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            time.sleep(0.02)
    os.remove(tmp)


def prune(out_dir, now):
    for name in os.listdir(out_dir):
        if name.startswith('claude-status-') and (name.endswith('.json') or name.endswith('.tmp')):
            full = os.path.join(out_dir, name)
            try:
                if now - os.path.getmtime(full) > PRUNE_AFTER:
                    os.remove(full)
            except OSError:
                pass


def main():
    started = time.time()
    if len(sys.argv) != 3 or sys.argv[2] not in PHASES:
        return
    out_dir, phase = sys.argv[1], sys.argv[2]
    data = json.loads(sys.stdin.buffer.read(MAX_INPUT).decode('utf-8', 'replace'))
    session = data.get('session_id')
    if not isinstance(session, str) or not session:
        return
    key = hashlib.sha256(session.encode('utf-8')).hexdigest()[:24]
    owner = os.getppid()  # exec 形式的钩子由 Claude Code 会话进程直接拉起（10-23 终端和桌面 Code 页签都实测过）
    path = os.path.join(out_dir, 'claude-status-%d-%s.json' % (owner, key))
    if phase == 'end':
        try:
            os.remove(path)
        except OSError:
            pass
        return
    os.makedirs(out_dir, exist_ok=True)
    prev = read_json(path) or {}
    if isinstance(prev.get('eventAt'), (int, float)) and prev['eventAt'] > started:
        return  # 后台钩子可能乱序跑完；更新的事件已经写过了
    model = clean_model(prev.get('model'))
    if phase == 'model':
        model = clean_model(data.get('to_model')) or model
        phase = prev.get('phase') if prev.get('phase') in ('running', 'idle', 'waiting') else 'idle'
    model = clean_model(data.get('model')) or model  # SessionStart 有时带模型
    event = data.get('hook_event_name')
    if model is None or event in ('Stop', 'StopFailure', 'SessionStart'):
        model = transcript_model(data.get('transcript_path')) or model
    record = {
        'schemaVersion': 1,
        'source': 'claude-code-hooks',
        'ownerPid': owner,
        'phase': phase,
        'model': model,
        'eventAt': started,
        'observedAt': iso(started),
        'phaseSince': prev.get('phaseSince') if prev.get('phase') == phase and prev.get('phaseSince') else iso(started),
    }
    write_atomic(path, record)
    if event == 'SessionStart':
        prune(out_dir, started)


if __name__ == '__main__':
    try:
        main()
    except BaseException:
        pass
    os._exit(0)
