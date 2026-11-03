"""同事在自己的工作区里管理名下 agent 的口令。本机 主人 的口令清点仍在 manage.py。"""

from __future__ import annotations

from .i18n import text as tr

from . import config

from . import tokens
from .store import BaseError, make_viewer, member_keys
from .textutil import _check_text


class TokenMixin:
    """给同事用的口令操作，都走本人工作区。主人 清点全部口令不在这里。"""

    def agent_token_list(self) -> list[dict]:
        """我的 Agent 只含本人的 agent 口令；不含本人登录口令或任何会话。"""
        viewer, docs, _ = self._human_workspace()
        keys = {key for key, d in member_keys(docs).items()
                if d.meta.get("kind") == "agent" and d.meta.get("owner") == viewer.key}
        return [t for t in tokens.list_tokens() if t["kind"] == "token" and t["key"] in keys]

    def token_issue(self, key: str, label: str) -> dict:
        label = _check_text(tr("agent.592"), label, limit=60).replace("\n", " ")
        with self._write():
            viewer, docs, _ = self._human_workspace()
            if key == config.owner_key() or not make_viewer(docs, key).can_remote:
                raise BaseError(tr("agent.593"))
            if not viewer.is_owner:
                target = member_keys(docs).get(key)
                if not (viewer.can_remote and target and target.meta.get("kind") == "agent"
                        and target.meta.get("owner") == viewer.key):
                    raise BaseError(tr("agent.594"))
            token, record = tokens.issue(key, label, issued_by=viewer.key)
        return {"token": token, "record": record}

    def token_revoke(self, token_id: str) -> dict:
        with self._write():
            viewer, docs, _ = self._human_workspace()
            members = member_keys(docs)

            def permitted(record):
                if viewer.is_owner:
                    return True
                target = members.get(record.get("key"))
                return (record.get("kind") == "token" and viewer.can_remote and target is not None
                        and target.meta.get("kind") == "agent" and target.meta.get("owner") == viewer.key)

            return tokens.revoke(str(token_id), authorize=permitted)
