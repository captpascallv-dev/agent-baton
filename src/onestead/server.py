"""Onestead 的 MCP 服务。

- 本机（stdio）：每个 agent 打开会话时自己拉起一份，不常驻。身份：环境变量 ONESTEAD_AGENT（例如 claude、codex）。
- 远程（HTTP）：给同事用，身份由口令决定（tokens.py），不靠自报。见 remote_app。
数据仓库：配置 data_dir 或环境变量 ONESTEAD_DATA，无默认目录。
"""

from .i18n import text as tr

import logging
import os
from typing import Annotated

from mcp.server.auth.middleware.auth_context import get_access_token
from mcp.server.auth.provider import AccessToken
from mcp.server.mcpserver import MCPServer
from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import Field

from . import config, tokens, usage
from .base import CompanyBase, remote_message
from .store import AuthRejected, BaseError, Store, make_viewer, owner_name_at
from .redact import safe_text
from .research import FetchOutput, SearchOutput

# 保存文字键，建服务时再取语言；导入模块不读取本机配置。
INSTRUCTIONS = "mcp.instructions"
READ = ToolAnnotations(read_only_hint=True, open_world_hint=False)
WRITE = ToolAnnotations(read_only_hint=False, destructive_hint=False, open_world_hint=False)


def build_server(root: str, agent_key: str | None, *, remote: bool = False, oauth_provider=None, **server_kwargs) -> MCPServer:
    """remote=True 时不用 agent_key，每次调用从这次请求的口令里取身份。"""
    config.owner_key()
    owner_name = owner_name_at(root)
    Signed = Annotated[str | None, Field(description=tr("agent.374"))]
    name = config.name()
    # 名字就是产品名时不再括注一遍产品名
    title = tr("mcp.title_default") if name == "Onestead" else tr("mcp.title", name=name)
    mcp = MCPServer("company-base", title=config.display_name(),
                    instructions=tr(INSTRUCTIONS, title=title, owner_name=owner_name), **server_kwargs)

    def run(name: str, *args, **kwargs) -> str | SearchOutput | FetchOutput:
        key = agent_key
        if remote:
            token = get_access_token()
            if token is None:  # 正常到不了这里：没口令的请求在 HTTP 层就被拒了
                raise ToolError(tr("agent.376"))
            key = token.subject if oauth_provider else token.client_id
        usage.record(key, name, "remote" if remote else "mcp")
        if not remote:
            try:
                return getattr(CompanyBase(root, key), name)(*args, **kwargs)
            except BaseError as e:
                raise ToolError(str(e)) from None
        # 远程：这次请求从头到尾都必须还能远程接入；报错里不带本机路径，意外的错只说一句（细节记在本机日志）
        try:
            check = oauth_provider.auth_check(token.token, key) if oauth_provider else remote_auth_check(root, token.token, key)
            check()
            base = CompanyBase(root, key, require_remote=True, auth_check=check)
            if name in ("research_search", "research_fetch"):
                from . import research
                result = getattr(research, name.removeprefix("research_"))(base, *args, oauth_provider.origin)
                check()
                return result
            return safe_text(getattr(base, name)(*args, **kwargs))
        except BaseError as e:
            raise ToolError(remote_message(e, root)) from None
        except Exception:
            logging.getLogger(__name__).exception("远程调用 %s 出错", name)
            raise ToolError(tr("agent.377", v0=safe_text(owner_name))) from None

    @mcp.tool(annotations=READ, description=tr("agent.635"))
    def company(
        cwd: Annotated[str | None, Field(description=tr("here.mcp_cwd"))] = None,
    ) -> str:
        """入职说明：公司是什么、有哪些项目、谁在做、协作规矩和成员。新对话先调这个。"""
        return run("company", cwd)

    @mcp.tool(annotations=READ, description=tr("agent.636"))
    def whoami() -> str:
        """看你在底座里的身份、能看到多少内容、能不能写入。"""
        return run("whoami")

    @mcp.tool(annotations=READ, description=tr("agent.637"))
    def list_projects() -> str:
        """项目一览：id、名称、状态、一句话目标、现在由谁担任、现状摘要截至哪天。"""
        return run("list_projects")

    @mcp.tool(annotations=READ, description=tr("agent.638"))
    def get_project(
        project_id: Annotated[str, Field(description=tr("agent.378"))],
        status_entries: Annotated[int, Field(description=tr("agent.379"))] = 5,
    ) -> str:
        """项目详情：目标、现状摘要（带日期和以哪份台账为准）、下一步、角色与担任者、入口、应用、最近进展。"""
        return run("get_project", project_id, status_entries)

    @mcp.tool(annotations=WRITE, description=tr("agent.370", v0=owner_name))
    def create_project(
        project_id: Annotated[str, Field(description=tr("agent.380"))],
        title: Annotated[str, Field(description=tr("agent.381"))],
        goal: Annotated[str, Field(description=tr("agent.155"))],
        state: Annotated[str, Field(description=tr("agent.382"))] = "构想中",
        body: Annotated[str | None, Field(description=tr("agent.383"))] = None,
        sensitivity: Annotated[str, Field(description=tr("agent.384"))] = "internal",
        paths: Annotated[list[str] | None, Field(description=tr("here.mcp_paths"))] = None,
        signed_as: Signed = None,
    ) -> str:
        """主人 或项目负责人让你开新项目时用；建完用 handover_role 把担任者登记上。"""
        return run("create_project", project_id, title, goal, state, body, sensitivity, paths, signed_as)

    if oauth_provider:
        @mcp.tool(annotations=READ, description=tr("agent.661"))
        def search(query: Annotated[str, Field(description=tr("agent.424"))]) -> SearchOutput:
            """搜索获准文档，返回文档 id、标题和底座网页地址；用 fetch 读正文。多个词要求同时出现。"""
            return run("research_search", query)

        @mcp.tool(annotations=READ, description=tr("agent.662"))
        def fetch(id: Annotated[str, Field(description=tr("mcp.document_id"))]) -> FetchOutput:
            """按 search 给出的相对文档 id 读取获准正文和引用地址。"""
            return run("research_fetch", id)
    else:
        @mcp.tool(annotations=READ, description=tr("agent.663"))
        def search(
            query: Annotated[str, Field(description=tr("agent.424"))],
            limit: Annotated[int, Field(description=tr("agent.425"))] = 20,
        ) -> str:
            """跨手册、项目、进展、决定、资产、成员搜索。"""
            return run("search", query, limit)

    @mcp.tool(annotations=READ, description=tr("agent.640"))
    def get_handbook(handbook_id: Annotated[str, Field(description=tr("agent.385"))]) -> str:
        """读一份手册：company（公司概况）、collaboration（协作与规则入口）、format（格式说明）。"""
        return run("get_handbook", handbook_id)

    @mcp.tool(annotations=READ, description=tr("agent.641"))
    def list_assets() -> str:
        """资产一览（know-how、模板、规范等）。"""
        return run("list_assets")

    @mcp.tool(annotations=READ, description=tr("agent.642"))
    def get_asset(asset_id: Annotated[str, Field(description=tr("agent.386"))]) -> str:
        """读一份资产。"""
        return run("get_asset", asset_id)

    @mcp.tool(annotations=READ, description=tr("agent.643"))
    def list_decisions(
        project_id: Annotated[str | None, Field(description=tr("agent.387"))] = None,
        limit: Annotated[int, Field(description=tr("agent.388"))] = 20,
        include_inactive: Annotated[bool, Field(description=tr("lesson.include_inactive"))] = False,
    ) -> str:
        """决定记录，最新在前：日期、谁定的、定了什么、为什么。"""
        return run("list_decisions", project_id, limit, include_inactive)

    @mcp.tool(annotations=WRITE, description=tr("lesson.tool.record", owner=owner_name))
    def record_lesson(
        lesson: Annotated[str, Field(description=tr("lesson.param.lesson"))],
        title: Annotated[str | None, Field(description=tr("lesson.param.title"))] = None,
        how: Annotated[str | None, Field(description=tr("lesson.param.how"))] = None,
        why: Annotated[str | None, Field(description=tr("lesson.param.why"))] = None,
        project_id: Annotated[str | None, Field(description=tr("lesson.param.project_id"))] = None,
        source: Annotated[str | None, Field(description=tr("lesson.param.source"))] = None,
        expires: Annotated[str | None, Field(description=tr("lesson.param.expires"))] = None,
        pending: Annotated[bool, Field(description=tr("lesson.param.pending"))] = False,
        supersedes: Annotated[list[str] | None, Field(description=tr("lesson.param.supersedes"))] = None,
        ref: Annotated[str | None, Field(description=tr("lesson.param.ref"))] = None,
        sensitivity: Annotated[str, Field(description=tr("agent.397"))] = "internal",
        visible_to: Annotated[list[str] | None, Field(description=tr("agent.398"))] = None,
        access_note: Annotated[str | None, Field(description=tr("agent.399"))] = None,
        signed_as: Signed = None,
        happened: Annotated[str | None, Field(description=tr("lesson.param.happened"))] = None,
    ) -> str:
        return run("record_lesson", lesson, title, how, why, project_id, source, expires, pending,
                   supersedes, ref, sensitivity, visible_to, access_note, signed_as, happened)

    @mcp.tool(annotations=READ, description=tr("lesson.tool.list"))
    def list_lessons(
        project_id: Annotated[str | None, Field(description=tr("lesson.param.project_id"))] = None,
        include_inactive: Annotated[bool, Field(description=tr("lesson.include_inactive"))] = False,
        limit: Annotated[int, Field(description=tr("lesson.limit"))] = 50,
    ) -> str:
        return run("list_lessons", project_id, include_inactive, limit)

    @mcp.tool(annotations=WRITE, description=tr("lesson.tool.update"))
    def update_record(
        record_id: Annotated[str, Field(description=tr("lesson.record_id"))],
        changes: Annotated[dict, Field(description=tr("lesson.changes"))],
        signed_as: Signed = None,
    ) -> str:
        return run("update_record", record_id, changes, signed_as)

    @mcp.tool(annotations=WRITE, description=tr("digest.tool.digest"))
    def lesson_digest(
        batch: Annotated[int, Field(description=tr("digest.param.batch"))] = 1,
        notify: Annotated[bool, Field(description=tr("digest.param.notify"))] = False,
        signed_as: Signed = None,
    ) -> str:
        return run("lesson_digest", batch, notify, signed_as)

    @mcp.tool(annotations=WRITE, description=tr("digest.tool.review"))
    def review_lessons(
        confirm: Annotated[list[str] | None, Field(description=tr("digest.param.confirm"))] = None,
        retire: Annotated[list[dict] | None, Field(description=tr("digest.param.retire"))] = None,
        done: Annotated[bool, Field(description=tr("digest.param.done"))] = False,
        signed_as: Signed = None,
    ) -> str:
        return run("review_lessons", confirm, retire, done, signed_as)

    @mcp.tool(annotations=WRITE, description=tr("edit.tool.page", owner=owner_name))
    def update_page(
        kind: Annotated[str, Field(description=tr("edit.param.kind"))],
        page_id: Annotated[str, Field(description=tr("edit.param.id"))],
        body: Annotated[str, Field(description=tr("edit.param.body"))],
        title: Annotated[str | None, Field(description=tr("edit.param.title"))] = None,
        signed_as: Signed = None,
    ) -> str:
        return run("update_page", kind, page_id, body, title, signed_as)

    @mcp.tool(annotations=READ, description=tr("history.tool"))
    def history(
        target: Annotated[str, Field(description=tr("history.target"))],
        limit: Annotated[int, Field(description=tr("history.limit"))] = 10,
    ) -> str:
        return run("history", target, limit)

    @mcp.tool(annotations=WRITE, description=tr("agent.644"))
    def update_project_status(
        project_id: Annotated[str, Field(description=tr("agent.389"))],
        text: Annotated[str, Field(description=tr("agent.390"))],
        signed_as: Signed = None,
    ) -> str:
        """在项目的进展记录里追加一条（只追加，不改旧的），并以你的身份提交一次 git。"""
        return run("update_project_status", project_id, text, signed_as)

    @mcp.tool(annotations=WRITE, description=tr("agent.645"))
    def update_project_summary(
        project_id: Annotated[str, Field(description=tr("agent.389"))],
        sections: Annotated[dict | None, Field(description=tr("agent.391"))] = None,
        signed_as: Signed = None,
        state: Annotated[str | None, Field(description=tr("edit.param.state"))] = None,
        title: Annotated[str | None, Field(description=tr("edit.param.title"))] = None,
        goal: Annotated[str | None, Field(description=tr("edit.param.goal"))] = None,
    ) -> str:
        """改项目摘要；全读写入者还能改状态、标题、目标和边界。"""
        return run("update_project_summary", project_id, sections, signed_as, state, title, goal)

    @mcp.tool(annotations=WRITE, description=tr("agent.646"))
    def record_decision(
        title: Annotated[str, Field(description=tr("agent.392"))],
        decision: Annotated[str, Field(description=tr("agent.393"))],
        reason: Annotated[str, Field(description=tr("agent.394"))],
        decided_by: Annotated[str, Field(description=tr("agent.395", v0=owner_name))],
        project_id: Annotated[str | None, Field(description=tr("agent.396"))] = None,
        sensitivity: Annotated[str, Field(description=tr("agent.397"))] = "internal",
        visible_to: Annotated[list[str] | None, Field(description=tr("agent.398"))] = None,
        access_note: Annotated[str | None, Field(description=tr("agent.399"))] = None,
        signed_as: Signed = None,
        source: Annotated[str | None, Field(description=tr("lesson.param.source"))] = None,
        expires: Annotated[str | None, Field(description=tr("lesson.param.expires"))] = None,
        pending: Annotated[bool, Field(description=tr("lesson.param.pending"))] = False,
        supersedes: Annotated[list[str] | None, Field(description=tr("lesson.param.supersedes"))] = None,
        happened: Annotated[str | None, Field(description=tr("lesson.param.happened"))] = None,
    ) -> str:
        """记一条决定（新建一份决定记录），并以你的身份提交一次 git。"""
        return run("record_decision", title, decision, reason, decided_by, project_id,
                   sensitivity, visible_to, access_note, signed_as, source, expires, pending, supersedes, happened)

    @mcp.tool(annotations=WRITE, description=tr("agent.371", v0=owner_name))
    def handover_role(
        project_id: Annotated[str, Field(description=tr("agent.389"))],
        role: Annotated[str, Field(description=tr("agent.400"))],
        who: Annotated[str, Field(description=tr("agent.401"))],
        reason: Annotated[str, Field(description=tr("agent.402"))],
        owner_approval: Annotated[str | None, Field(description=tr("agent.403", v0=owner_name))] = None,
        signed_as: Signed = None,
    ) -> str:
        """换某个角色的担任者。普通角色直接生效；关键角色先记一条接任申请，带上 主人 的批准再调一次才生效。"""
        return run("handover_role", project_id, role, who, reason, owner_approval, signed_as)

    @mcp.tool(annotations=READ, description=tr("agent.648", owner=owner_name))
    def list_todos(
        scope: Annotated[str, Field(description=tr("agent.404"))] = "open",
        project_id: Annotated[str | None, Field(description=tr("agent.405"))] = None,
    ) -> str:
        """你的待办（agent 是它所属的人的），按逾期、今天、接下来 7 天、以后、没定日期分组。"""
        return run("list_todos", scope, project_id)

    @mcp.tool(annotations=WRITE, description=tr("agent.649", owner=owner_name))
    def add_todos(
        items: Annotated[list[dict], Field(description=(
            tr("agent.406")))],
        note: Annotated[str | None, Field(description=tr("agent.407"))] = None,
        signed_as: Signed = None,
    ) -> str:
        """加你的待办（agent 是它所属的人的；一次可加多条，作为一次提交）。有日期的会出现在网页的日历里。"""
        return run("add_todos", items, note, signed_as)

    @mcp.tool(annotations=WRITE, description=tr("agent.650", owner=owner_name))
    def update_todo(
        todo_id: Annotated[str, Field(description=tr("agent.408"))],
        changes: Annotated[dict, Field(description=(
            tr("agent.409")))],
        signed_as: Signed = None,
    ) -> str:
        """改你的待办（agent 是它所属的人的）：标题、日期、时间、项目、备注、状态。确认事情做完了可以直接标 done。"""
        return run("update_todo", todo_id, changes, signed_as)

    @mcp.tool(annotations=READ, description=tr("agent.651"))
    def list_apps(
        kind: Annotated[str | None, Field(description=tr("agent.410"))] = None,
        project_id: Annotated[str | None, Field(description=tr("agent.411"))] = None,
        include_unlisted: Annotated[bool, Field(description=tr("agent.412"))] = False,
    ) -> str:
        """应用广场：agent 做出来的应用一处看全，名称、类型、一句话、谁维护、怎么打开。"""
        return run("list_apps", kind, project_id, include_unlisted)

    @mcp.tool(annotations=READ, description=tr("agent.652"))
    def get_app(app_id: Annotated[str, Field(description=tr("agent.413"))]) -> str:
        """一个应用的详情：怎么打开、代码在哪、谁维护、相关链接、说明。"""
        return run("get_app", app_id)

    @mcp.tool(annotations=WRITE, description=tr("agent.372", v0=owner_name))
    def register_app(
        app: Annotated[dict, Field(description=(
            tr("agent.414")))],
        signed_as: Signed = None,
    ) -> str:
        """做完一个应用后登记上架，主人 就能在应用广场找到并打开它。"""
        return run("register_app", app, signed_as)

    @mcp.tool(annotations=WRITE, description=tr("agent.654"))
    def update_app(
        app_id: Annotated[str, Field(description=tr("agent.413"))],
        changes: Annotated[dict, Field(description=(
            tr("agent.415")))],
        signed_as: Signed = None,
    ) -> str:
        """改应用：改名、换图标、换打开方式、下架或重新上架。"""
        return run("update_app", app_id, changes, signed_as)

    if not remote:
        @mcp.tool(annotations=WRITE, description=tr("agent.664"))
        def publish_site(
            app_id: Annotated[str, Field(description=tr("agent.413"))],
            build_dir: Annotated[str, Field(description=tr("mcp.build_dir"))],
            slug: Annotated[str | None, Field(description=tr("mcp.slug"))] = None,
            signed_as: Signed = None,
        ) -> str:
            """发布静态网页：构建目录要用绝对路径，根部有 index.html。首次须给短名，主人在网页上定谁能看。"""
            return run("publish_site", app_id, build_dir, slug, signed_as)

    @mcp.tool(annotations=READ, description=tr("agent.655"))
    def list_skills(
        only_alerts: Annotated[bool, Field(description=tr("agent.416"))] = False,
        fresh: Annotated[bool, Field(description=tr("agent.417"))] = False,
        include_unlisted: Annotated[bool, Field(description=tr("api.include_unlisted"))] = False,
    ) -> str:
        """Skill 广场：Claude、Codex、PI、Grok CLI、Cursor、WorkBuddy，以及配置的额外本机工具和远程快照各装了哪些 Skill，各家内容一不一样。"""
        return run("list_skills", only_alerts, fresh, include_unlisted)

    @mcp.tool(annotations=READ, description=tr("agent.373", v0=owner_name))
    def get_skill(
        skill_id: Annotated[str, Field(description=tr("agent.418"))],
        fresh: Annotated[bool, Field(description=tr("agent.222"))] = False,
    ) -> str:
        """一个 Skill 在各家的位置和状态、以哪份为准，以及装到另一家放哪。装或更新之前先告诉 主人，他同意再动手。"""
        return run("get_skill", skill_id, fresh)

    @mcp.tool(annotations=WRITE, description=tr("agent.657"))
    def register_skill(
        skill: Annotated[dict, Field(description=(
            tr("agent.419")))],
        signed_as: Signed = None,
    ) -> str:
        """给一个 Skill 登记补充说明（中文名、一句话、正本在哪、谁维护）。装在哪几家不用登记，底座会现场扫。"""
        return run("register_skill", skill, signed_as)

    @mcp.tool(annotations=WRITE, description=tr("agent.658"))
    def update_skill(
        skill_id: Annotated[str, Field(description=tr("agent.420"))],
        changes: Annotated[dict, Field(description=tr("agent.421"))],
        signed_as: Signed = None,
    ) -> str:
        """改一个 Skill 的补充说明。"""
        return run("update_skill", skill_id, changes, signed_as)

    @mcp.tool(annotations=READ, description=tr("agent.659"))
    def get_home_layout() -> str:
        """看你的首页（agent 是它所属的人的）的模块、顺序和可用模块。改首页前先调这个。"""
        return run("home_layout")

    @mcp.tool(annotations=WRITE, description=tr("agent.660"))
    def propose_home(
        modules: Annotated[list[dict], Field(description=(
            tr("agent.422")))],
        note: Annotated[str, Field(description=tr("agent.423", v0=owner_name))],
        signed_as: Signed = None,
    ) -> str:
        """给你的首页提草稿（agent 是给它所属的人提）。本人在网页上预览后点“采用”才生效。"""
        return run("propose_home", modules, note, signed_as)

    return mcp


def remote_auth_check(root: str, token_value: str, client_id: str):
    """落笔前再认一次 Bearer：还是这个口令、还是这个身份、现在仍能远程接入。"""

    def check() -> None:
        rec = tokens.lookup_record(token_value)
        if not rec or rec.get("kind") != "token" or rec.get("key") != client_id:
            raise AuthRejected(tr("agent.375"))
        viewer = make_viewer(Store(root).load(), client_id)
        if not viewer.can_remote:
            raise AuthRejected(tr("agent.375"))

    return check


class TokenGate:
    """远程接口的口令核对：口令认得、没收回、没过期，而且那个身份现在仍允许远程接入。每个请求都现场核对。
    远程接口只认 token，旧 feishu 和 web 会话都不能当 Bearer。"""

    def __init__(self, root: str):
        config.owner_key()
        self.root = root

    async def verify_token(self, token: str) -> AccessToken | None:
        try:
            rec = tokens.lookup_record(token)
            if not rec:
                return None
            if rec.get("kind") != "token":
                return None
            key = rec.get("key")
            if not isinstance(key, str) or not key:
                return None
            viewer = make_viewer(Store(self.root).load(), key)
        except BaseError:
            return None
        if not viewer.can_remote:
            # 成员记录被改回本机伙伴、删掉、写错、和别人重名，或者 agent 所属的人无效：口令不再有效
            return None
        return AccessToken(token=token, client_id=key, scopes=[])


LOOPBACK = ("127.0.0.1", "localhost", "::1")


def remote_app(root: str, port: int, host: str = "127.0.0.1", public_host: str | None = None,
               *, web_test_origin: str | None = None):
    """共用工作台、远程接口（/mcp）和旧地址 /portal 的固定跳转。

    服务本身只监听本机。对外经 Cloudflare 隧道：隧道程序在这台电脑上连到 127.0.0.1，
    外面的请求带的主机名是 public_host（例如 base.example.com），HTTPS 由 Cloudflare 那头负责。
    拼装在 web.host：命令行和 online 仍从这里拿到应用。
    """
    from . import folders
    from .web.host import remote_app as mount

    if config.shared_folders():
        folders.warm()  # 后台同步落盘索引；同步期间搜索仍用已经建好的索引
    return mount(root, port, host, public_host, web_test_origin=web_test_origin)


def main(argv: list[str] | None = None) -> None:
    import argparse
    import sys

    argv = list(sys.argv[1:] if argv is None else argv)
    if argv[:1] == ["--remote"]:  # 保留已有远程入口。
        return main_remote(argv[1:])
    parser = argparse.ArgumentParser(prog="onestead-mcp")
    config.path_arguments(parser)
    parser.add_argument("--agent", "--as")
    args = parser.parse_args(argv)
    with config.use_paths(args):
        root = config.data_dir()
        sys.stderr.reconfigure(encoding="utf-8")
        build_server(root, args.agent or config.env("AGENT")).run("stdio")


def main_remote(argv: list[str] | None = None) -> None:
    """onestead-remote [端口] [--public-host 域名]：在本机起远程接口和网页工作台，给同事接入。
    对外的域名也可以用环境变量 ONESTEAD_PUBLIC_HOST 给。"""
    import sys

    import uvicorn

    argv = list(sys.argv[1:] if argv is None else argv)
    public_host = config.public_host()
    if "--public-host" in argv:
        i = argv.index("--public-host")
        public_host = argv[i + 1]
        del argv[i:i + 2]
    root = config.data_dir()
    port = int(argv[0]) if argv else 8765
    uvicorn.run(remote_app(root, port, public_host=public_host), host="127.0.0.1", port=port,
                log_level="warning", proxy_headers=False)


if __name__ == "__main__":
    main()
