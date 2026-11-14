"""Onestead 的命令行入口：给不能接 MCP 的伙伴用，权限规则和 MCP 完全一样。

例子：
  onestead init <数据目录> --owner <身份键> --name <主人显示名>
  onestead company --as assistant
  onestead project example --as codex
  onestead status example --text "……" --as assistant --signed-as alex
"""

from __future__ import annotations

from .i18n import text as tr

import argparse
import json
import io
import os
import sys

from . import usage
from .base import CompanyBase
from . import config
from .store import BaseError, Store, owner_name_at


def resolve_agent(args) -> str | None:
    """命令行上的 --as 优先于环境变量，写在子命令前或后都一样。"""
    given = getattr(args, "agent", None)
    if given:
        return given
    return config.env("AGENT")


def resolve_data(args) -> str:
    given = getattr(args, "data", None)
    return given or config.data_dir()


def resolve_signed_as(args) -> str | None:
    return getattr(args, "signed_as", None)


def _text(args, field: str) -> str | None:
    """长文本可以直接给，也可以 --xxx-file 指一个 UTF-8 文件，或 - 从标准输入读。"""
    value = getattr(args, field, None)
    path = getattr(args, f"{field}_file", None)
    if path == "-":
        return sys.stdin.read()
    if path:
        return io.open(path, encoding="utf-8").read()
    return value


class LocalizedFormatter(argparse.HelpFormatter):
    def add_usage(self, usage, actions, groups, prefix=None):
        # argparse 给子命令算 prog 时传 prefix=""，只在 None 时才补本地化前缀。
        if prefix is None:
            prefix = tr("cli.usage")
        return super().add_usage(usage, actions, groups, prefix)


class LocalizedParser(argparse.ArgumentParser):
    """只改本命令的帮助和常见解析提示，不修改 argparse 的进程全局翻译函数。"""

    def __init__(self, *args, **kwargs):
        kwargs.setdefault("formatter_class", LocalizedFormatter)
        super().__init__(*args, **kwargs)
        self._positionals.title = tr("cli.positionals")
        self._optionals.title = tr("cli.options")
        for action in self._actions:
            if isinstance(action, argparse._HelpAction):
                action.help = tr("cli.help")

    def error(self, message):
        if config.lang() == "zh":
            for prefix, key in (("the following arguments are required: ", "cli.required"),
                                ("unrecognized arguments: ", "cli.unknown")):
                if message.startswith(prefix):
                    message = tr(key, values=message[len(prefix):])
                    break
            else:
                # argparse 的类型、选项和数量错误，保留实际参数／输入，翻译固定句式。
                import re
                message = re.sub(r"^argument ([^:]+): ", lambda m: tr("cli.argument", name=m[1]), message)
                message = message.replace("invalid choice: ", tr("cli.invalid_choice"))
                message = message.replace("(choose from ", tr("cli.choose_from"))
                for source, key in (("expected one argument", "cli.one_argument"),
                                    ("expected at least one argument", "cli.at_least_one"),
                                    ("invalid int value: ", "cli.invalid_int")):
                    message = message.replace(source, tr(key))
        self.print_usage(sys.stderr)
        self.exit(2, tr("cli.error", prog=self.prog, message=message))


def build_parser() -> argparse.ArgumentParser:
    common = LocalizedParser(add_help=False)
    # 子命令前后都能写。SUPPRESS 避免子解析器把前面已经拿到的值覆盖成 None。
    common.add_argument("--as", dest="agent", default=argparse.SUPPRESS,
                        help=tr("agent.167"))
    common.add_argument("--signed-as", default=argparse.SUPPRESS, help=tr("agent.168"))
    common.add_argument("--data", "--root", dest="data", default=argparse.SUPPRESS,
                        help=tr("agent.169"))
    common.add_argument("--config", default=argparse.SUPPRESS)
    common.add_argument("--state", default=argparse.SUPPRESS)

    p = LocalizedParser(prog="onestead", description=tr("agent.170"), parents=[common])
    sub = p.add_subparsers(dest="cmd", required=True)
    for command in ("bootstrap", "inspect"):
        sub.add_parser(command, help=tr("bundle." + command))
    sub.add_parser("api", parents=[common], help=tr("api.help"))
    sub.add_parser("backup", parents=[common], help=tr("bundle.backup"))
    s = sub.add_parser("web", parents=[common], help=tr("bundle.web"))
    s.add_argument("--port", type=int, help=tr("bundle.web_port"))
    s.add_argument("--no-browser", action="store_true", help=tr("bundle.web_no_browser"))
    s = sub.add_parser("init", parents=[common], help=tr("agent.171"))
    s.add_argument("data_dir", help=tr("agent.172"))
    s.add_argument("--owner", required=True, help=tr("agent.173"))
    s.add_argument("--name", required=True, help=tr("agent.174"))
    s.add_argument("--lang", default="en", choices=("en", "zh"), help=tr("agent.175"))
    s.add_argument("--force-config", action="store_true", help=tr("agent.176"))
    s = sub.add_parser("connect", parents=[common], help=tr("connect.help"))
    s.add_argument("--apply", action="store_true", help=tr("connect.help_apply"))
    s.add_argument("--client", action="append", choices=("claude-code", "codex", "cursor", "workbuddy"),
                   help=tr("connect.help_client"))
    s.add_argument("--agent-key", help=tr("connect.help_agent"))
    s = sub.add_parser("company", parents=[common], help=tr("agent.177"))
    s.add_argument("--cwd", help=tr("here.cwd_help"))
    sub.add_parser("whoami", parents=[common], help=tr("agent.178"))
    sub.add_parser("projects", parents=[common], help=tr("agent.179"))
    s = sub.add_parser("project", parents=[common], help=tr("agent.180"))
    s.add_argument("project_id")
    s.add_argument("--entries", type=int, default=5, help=tr("agent.181"))
    s = sub.add_parser("project-create", parents=[common],
                       help=tr("agent.182"))
    s.add_argument("project_id")
    s.add_argument("--title", required=True)
    s.add_argument("--goal", required=True)
    s.add_argument("--project-state", dest="project_state", default="构想中", help=tr("agent.183"))
    s.add_argument("--body")
    s.add_argument("--body-file", help=tr("agent.184"))
    s.add_argument("--sensitivity", default="internal", help=tr("agent.185"))
    s.add_argument("--path", action="append", help=tr("here.path_help"))
    s = sub.add_parser("search", parents=[common], help=tr("agent.186"))
    s.add_argument("query", nargs="+")
    s.add_argument("--limit", type=int, default=20)
    s = sub.add_parser("handbook", parents=[common], help=tr("agent.187"))
    s.add_argument("handbook_id")
    sub.add_parser("assets", parents=[common], help=tr("agent.188"))
    s = sub.add_parser("asset", parents=[common], help=tr("agent.189"))
    s.add_argument("asset_id")
    s = sub.add_parser("decisions", parents=[common], help=tr("agent.190"))
    s.add_argument("--project")
    s.add_argument("--limit", type=int, default=20)
    s.add_argument("--all", action="store_true", help=tr("lesson.include_inactive"))

    s = sub.add_parser("lessons", parents=[common], help=tr("lesson.cli.list"))
    s.add_argument("--project")
    s.add_argument("--all", action="store_true", help=tr("lesson.include_inactive"))
    s = sub.add_parser("lesson-add", parents=[common], help=tr("lesson.cli.add"))
    s.add_argument("--lesson")
    s.add_argument("--lesson-file")
    for field in ("title", "how", "why", "project", "source", "expires", "happened", "ref", "access-note"):
        s.add_argument("--" + field)
    s.add_argument("--pending", action="store_true")
    s.add_argument("--supersedes", action="append", help=tr("lesson.param.supersedes"))
    s.add_argument("--sensitivity", default="internal", choices=["public", "internal", "restricted"])
    s.add_argument("--visible-to", help=tr("agent.197"))
    s = sub.add_parser("record-update", parents=[common], help=tr("lesson.tool.update"))
    s.add_argument("record_id", help=tr("lesson.record_id"))
    s.add_argument("--json", required=True, help=tr("lesson.changes"))
    s = sub.add_parser("history", parents=[common], help=tr("history.tool"))
    s.add_argument("target", help=tr("history.target"))
    s.add_argument("--limit", type=int, default=10)

    s = sub.add_parser("status", parents=[common], help=tr("agent.191"))
    s.add_argument("project_id")
    s.add_argument("--text")
    s.add_argument("--text-file", help=tr("agent.192"))
    s = sub.add_parser("project-summary", parents=[common],
                       help=tr("agent.193"))
    s.add_argument("project_id")
    s.add_argument("--text", help=tr("agent.194"))
    s.add_argument("--text-file", help=tr("agent.195"))
    s.add_argument("--project-state", dest="project_state", help=tr("edit.param.state"))
    s.add_argument("--title", help=tr("edit.param.title"))
    s.add_argument("--goal", help=tr("edit.param.goal"))
    s = sub.add_parser("digest", parents=[common], help=tr("digest.tool.digest"))
    s.add_argument("--batch", type=int, default=1, help=tr("digest.param.batch"))
    s.add_argument("--notify", action="store_true", help=tr("digest.param.notify"))
    s = sub.add_parser("review", parents=[common], help=tr("digest.tool.review"))
    s.add_argument("--confirm", action="append", help=tr("digest.cli.confirm"))
    s.add_argument("--retire", help=tr("digest.param.retire"))
    s.add_argument("--done", action="store_true", help=tr("digest.param.done"))
    s = sub.add_parser("page-update", parents=[common], help=tr("edit.cli.page"))
    s.add_argument("kind", choices=["handbook", "asset"], help=tr("edit.param.kind"))
    s.add_argument("page_id", help=tr("edit.param.id"))
    page_body = s.add_mutually_exclusive_group(required=True)
    page_body.add_argument("--text", help=tr("edit.param.body"))
    page_body.add_argument("--text-file", help=tr("agent.192"))
    s.add_argument("--title", help=tr("edit.param.title"))
    s = sub.add_parser("decide", parents=[common], help=tr("agent.196"))
    s.add_argument("--title", required=True)
    s.add_argument("--decision")
    s.add_argument("--decision-file")
    s.add_argument("--reason")
    s.add_argument("--reason-file")
    s.add_argument("--decided-by", required=True)
    s.add_argument("--project")
    s.add_argument("--sensitivity", default="internal", choices=["public", "internal", "restricted"])
    s.add_argument("--visible-to", help=tr("agent.197"))
    s.add_argument("--access-note")
    s.add_argument("--source", help=tr("lesson.param.source"))
    s.add_argument("--expires", help=tr("lesson.param.expires"))
    s.add_argument("--pending", action="store_true", help=tr("lesson.param.pending"))
    s.add_argument("--supersedes", action="append", help=tr("lesson.param.supersedes"))
    s.add_argument("--happened", help=tr("lesson.param.happened"))
    s = sub.add_parser("handover", parents=[common], help=tr("agent.198"))
    s.add_argument("project_id")
    s.add_argument("--role", required=True)
    s.add_argument("--who", required=True)
    s.add_argument("--reason", required=True)
    s.add_argument("--owner-approval", help=tr("agent.199"))

    sub.add_parser("home", parents=[common], help=tr("agent.200"))
    s = sub.add_parser("propose-home", parents=[common], help=tr("agent.201"))
    s.add_argument("--modules", required=True, help=tr("agent.202"))
    s.add_argument("--note", required=True, help=tr("agent.203"))

    s = sub.add_parser("todos", parents=[common], help=tr("agent.204"))
    s.add_argument("--scope", default="open", choices=["open", "today", "week", "all", "asks"])
    s.add_argument("--project")
    s = sub.add_parser("todo-add", parents=[common], help=tr("agent.205"))
    s.add_argument("--title")
    s.add_argument("--due", help=tr("agent.206"))
    s.add_argument("--time", help=tr("agent.207"))
    s.add_argument("--project")
    s.add_argument("--notes")
    s.add_argument("--items", help=tr("agent.208"))
    s.add_argument("--note", help=tr("agent.209"))
    s.add_argument("--ask", choices=["approve", "act", "fyi"], help=tr("ask.cli_kind"))
    for field in ("context", "why", "if_skipped", "action"):
        s.add_argument("--" + field.replace("_", "-"), help=tr("ask.cli_" + field))
    s = sub.add_parser("todo-update", parents=[common], help=tr("agent.210"))
    s.add_argument("todo_id")
    for name in ("title", "due", "time", "project", "notes"):
        s.add_argument(f"--{name}")
    s.add_argument("--status", choices=["open", "done", "dropped"])
    s.add_argument("--ask", choices=["approve", "act", "fyi"], help=tr("ask.cli_kind"))
    for field in ("context", "why", "if_skipped", "action"):
        s.add_argument("--" + field.replace("_", "-"), help=tr("ask.cli_" + field))
    notice = s.add_mutually_exclusive_group()
    notice.add_argument("--notified", dest="notified", action="store_true", default=None, help=tr("ask.cli_notified"))
    notice.add_argument("--not-notified", dest="notified", action="store_false", help=tr("ask.cli_not_notified"))

    s = sub.add_parser("apps", parents=[common], help=tr("agent.211"))
    s.add_argument("--kind")
    s.add_argument("--project")
    s.add_argument("--all", action="store_true", help=tr("agent.212"))
    s = sub.add_parser("app", parents=[common], help=tr("agent.213"))
    s.add_argument("app_id")
    s = sub.add_parser("app-register", parents=[common], help=tr("agent.214"))
    s.add_argument("--json", help=tr("agent.215"))
    s.add_argument("--file", help=tr("agent.216"))
    s.add_argument("--note", help=tr("agent.217"))
    s = sub.add_parser("app-update", parents=[common], help=tr("agent.218"))
    s.add_argument("app_id")
    s.add_argument("--json", required=True, help=tr("agent.219"))

    s = sub.add_parser("skills", parents=[common], help=tr("agent.220"))
    s.add_argument("--all", action="store_true", help=tr("api.include_unlisted"))
    s.add_argument("--alerts", action="store_true", help=tr("agent.221"))
    s.add_argument("--fresh", action="store_true", help=tr("agent.222"))
    s = sub.add_parser("skill", parents=[common], help=tr("agent.223"))
    s.add_argument("skill_id")
    s = sub.add_parser("skill-register", parents=[common], help=tr("agent.224"))
    s.add_argument("--json")
    s.add_argument("--file", help=tr("agent.225"))
    s.add_argument("--note", help=tr("agent.217"))
    s = sub.add_parser("skill-update", parents=[common], help=tr("agent.226"))
    s.add_argument("skill_id")
    s.add_argument("--json", required=True)

    sub.add_parser("check", parents=[common], help=tr("agent.227"))
    return p


def run(argv: list[str] | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
    argv = list(sys.argv[1:] if argv is None else argv)
    # 初始化的参数错误也必须进 JSON 协议，不能先被通用 parser 打断。
    skip_value = False
    for i, value in enumerate(argv):
        if skip_value:
            skip_value = False
            continue
        if value in ("--data", "--root", "--config", "--state", "--as", "--signed-as"):
            skip_value = True
        elif not value.startswith("-"):
            if value in ("bootstrap", "inspect"):
                from .bootstrap import run_cli
                return run_cli(value, argv[:i] + argv[i + 1:])
            break
    paths = argparse.ArgumentParser(add_help=False)
    config.path_arguments(paths)
    # --root 的旧别名同样在解析文案、配置前生效。
    paths.add_argument("--root", dest="data", default=argparse.SUPPRESS)
    preliminary, remaining = paths.parse_known_args(argv)
    if remaining and remaining[0] in ("project-create", "project-summary"):
        # 旧子命令的 --state 表示项目状态；在子命令之前才是全局私有目录。
        command_index = argv.index(remaining[0])
        argv = [value.replace("--state", "--project-state", 1) if i > command_index and
                (value == "--state" or value.startswith("--state=")) else value
                for i, value in enumerate(argv)]
        preliminary, remaining = paths.parse_known_args(argv)
    with config.use_paths(preliminary):
        return _run(argv)


def _run(argv: list[str]) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
    try:
        args = build_parser().parse_args(argv)
    except BaseError as error:
        print(str(error), file=sys.stderr)
        return 2
    if args.cmd == "api":
        from .api import run
        return run(args)
    if args.cmd == "backup":
        from .backup import main
        main()
        return 0
    if args.cmd == "web":
        # 和 onestead-web 同一个启动器：只凭 onestead 一个程序路径（venv 安装或随装工件）就能起本机网页。
        if args.port is not None and not 0 <= args.port <= 65535:
            print("--port: 0..65535", file=sys.stderr)
            return 2
        from .web.launcher import _main
        _main(args)
        return 0
    if args.cmd == "init":
        from .scaffold import init_repo
        try:
            init_repo(args)
        except BaseError as error:
            print(str(error), file=sys.stderr)
            return 2
        return 0
    if args.cmd == "connect":
        from .connect import run_connect
        return run_connect(args)
    try:
        root = resolve_data(args)
    except BaseError as error:
        print(str(error), file=sys.stderr)
        return 2
    if args.cmd == "check":
        return _check(root)
    agent = resolve_agent(args)
    if not agent:
        print(tr("agent.229"), file=sys.stderr)
        return 2
    try:
        base = CompanyBase(root, agent)
    except BaseError as error:
        print(str(error), file=sys.stderr)
        return 2
    signed = resolve_signed_as(args)
    usage.record(agent, args.cmd, "cli")
    try:
        out = {
            "company": lambda: base.company(args.cwd),
            "whoami": lambda: base.whoami(),
            "projects": lambda: base.list_projects(),
            "project": lambda: base.get_project(args.project_id, args.entries),
            "project-create": lambda: base.create_project(args.project_id, args.title, args.goal, args.project_state,
                                                         _text(args, "body"), args.sensitivity, args.path, signed),
            "search": lambda: base.search(" ".join(args.query), args.limit),
            "handbook": lambda: base.get_handbook(args.handbook_id),
            "assets": lambda: base.list_assets(),
            "asset": lambda: base.get_asset(args.asset_id),
            "decisions": lambda: base.list_decisions(args.project, args.limit, args.all),
            "lessons": lambda: base.list_lessons(args.project, args.all),
            "lesson-add": lambda: base.record_lesson(_text(args, "lesson"), args.title, args.how, args.why,
                args.project, args.source, args.expires, args.pending, args.supersedes, args.ref,
                args.sensitivity, [x.strip() for x in (args.visible_to or "").split(",") if x.strip()] or None,
                args.access_note, signed, args.happened),
            "record-update": lambda: base.update_record(args.record_id, _json(args.json, "--json"), signed),
            "history": lambda: base.history(args.target, args.limit),
            "status": lambda: base.update_project_status(args.project_id, _text(args, "text"), signed),
            "project-summary": lambda: base.update_project_summary(
                args.project_id, _json(_text(args, "text"), "--text") if args.text or args.text_file else None,
                signed, args.project_state, args.title, args.goal),
            "digest": lambda: base.lesson_digest(args.batch, args.notify, signed),
            "review": lambda: base.review_lessons(args.confirm, _json(args.retire, "--retire") if args.retire else None, args.done, signed),
            "page-update": lambda: base.update_page(args.kind, args.page_id, _text(args, "text"), args.title, signed),
            "decide": lambda: base.record_decision(
                args.title, _text(args, "decision"), _text(args, "reason"), args.decided_by,
                args.project, args.sensitivity,
                [x for x in (args.visible_to or "").split(",") if x.strip()] or None,
                args.access_note, signed, args.source, args.expires, args.pending, args.supersedes,
                args.happened),
            "handover": lambda: base.handover_role(args.project_id, args.role, args.who, args.reason,
                                                   args.owner_approval, signed),
            "home": lambda: base.home_layout(),
            "propose-home": lambda: base.propose_home(_json(args.modules, "--modules"), args.note, signed),
            "todos": lambda: base.list_todos(args.scope, args.project),
            "todo-add": lambda: base.add_todos(_todo_items(args), args.note, signed),
            "todo-update": lambda: base.update_todo(args.todo_id, _todo_changes(args), signed),
            "apps": lambda: base.list_apps(args.kind, args.project, args.all),
            "app": lambda: base.get_app(args.app_id),
            "app-register": lambda: (base.register_apps(_json(open(args.file, encoding="utf-8").read(), "--file"),
                                                        args.note or "批量登记", signed)
                                     if args.file else base.register_app(_json(args.json or "", "--json"), signed)),
            "app-update": lambda: base.update_app(args.app_id, _json(args.json, "--json"), signed),
            "skills": lambda: base.list_skills(args.alerts, args.fresh, args.all),
            "skill": lambda: base.get_skill(args.skill_id),
            "skill-register": lambda: (base.register_skills(_json(open(args.file, encoding="utf-8").read(), "--file"),
                                                            args.note or "批量登记", signed)
                                       if args.file else base.register_skill(_json(args.json or "", "--json"), signed)),
            "skill-update": lambda: base.update_skill(args.skill_id, _json(args.json, "--json"), signed),
        }[args.cmd]()
    except BaseError as e:
        print(str(e), file=sys.stderr)
        return 1
    sys.stdout.write(out)
    return 0


def _json(text: str, flag: str):
    try:
        return json.loads(text)
    except ValueError as e:
        raise BaseError(tr("agent.230", v0=flag, v1=e)) from None


def _todo_items(args) -> list:
    if args.items:
        return _json(args.items, "--items")
    item = {k: getattr(args, k) for k in ("title", "due", "time", "project", "notes", "ask", "context", "why", "if_skipped", "action") if getattr(args, k) is not None}
    return [item]


def _todo_changes(args) -> dict:
    return {k: getattr(args, k) for k in ("title", "due", "time", "project", "notes", "status", "ask", "context", "why", "if_skipped", "action", "notified")
            if getattr(args, k) is not None}


def _check(root: str) -> int:
    try:
        docs = Store(root).load()
    except BaseError as e:
        print(str(e), file=sys.stderr)
        return 1
    bad = [d for d in docs if d.problem]
    for d in bad:
        print(f"{d.rel}：{d.problem}")
    print(tr("agent.228", v0=len(docs), v1=len(bad), v2=owner_name_at(root)))
    return 1 if bad else 0


def main() -> None:
    sys.exit(run())


if __name__ == "__main__":
    main()
