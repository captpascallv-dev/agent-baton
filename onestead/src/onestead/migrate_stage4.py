"""成员字段迁移；默认只列字段变化，--apply 才在数据仓库提交一次。"""

from __future__ import annotations

from . import config

import argparse
import json
from pathlib import Path

from . import gitops
from .store import BaseError, check_meta, read_doc, split_front, valid_person_key
from .textutil import _check_signature, _dump_front


def migration_plan(root: str | Path) -> list[dict]:
    """只读成员文件，保留正文、授权和不涉及迁移的字段。重复运行没有变化。"""
    company_owner = config.owner_key()
    root = Path(root)
    if not (root / ".git").exists():
        raise BaseError("迁移目标必须是数据 git 仓库。")
    result = []
    for path in sorted((root / "people").glob("*.md")):
        doc = read_doc(root, path, "person")
        if not doc.meta or doc.meta.get("type") != "person":
            raise BaseError(f"{doc.rel} 无法作为成员记录迁移，请先修正。")
        meta = dict(doc.meta)
        if "scope" in meta:
            if meta.pop("scope") != "external":
                raise BaseError(f"{doc.rel} 的旧 scope 无法识别，请先核对。")
            meta.update(remote_enabled=True, access_mode="assigned")
        else:
            meta.setdefault("remote_enabled", False)
            meta.setdefault("access_mode", "full")
        if meta.get("kind") == "agent" and "owner" not in meta:
            meta["owner"] = company_owner
        changes = {k: {"before": doc.meta.get(k), "after": meta.get(k)}
                   for k in ("scope", "remote_enabled", "access_mode", "owner")
                   if (k in doc.meta) != (k in meta) or doc.meta.get(k) != meta.get(k)}
        if changes:
            result.append({"rel": doc.rel, "changes": changes,
                           "content": _dump_front(meta) + doc.body})
    return result


def migration_problems(root: Path, plan: list[dict]) -> list[str]:
    """整批投影先检查：kind、重名、所属的人、-draft、标记合规。有问题就不写。"""
    planned = {item["rel"]: item["content"] for item in plan}
    folder = root / "people"
    if not folder.is_dir():
        return []
    people = []
    for path in sorted(folder.glob("*.md")):
        doc = read_doc(root, path, "person")
        if doc.rel in planned:
            meta, _, problem = split_front(planned[doc.rel])
            people.append((doc.rel, meta or {}, problem))
        else:
            people.append((doc.rel, doc.meta, None))
    problems = []
    keys = []
    humans = set()
    for rel, meta, _problem in people:
        if meta.get("kind") not in ("human", "agent"):
            problems.append(f"{rel} 无法确定 kind")
            continue
        key = meta.get("agent_key")
        if isinstance(key, str) and key.endswith("-draft"):
            problems.append(f"{rel} 的身份名不能以 -draft 结尾")
        elif rel in planned:
            # 标记合规只挡这次要改写的记录。仓库里本来就写错、迁移不会动的成员不挡住其他文件。
            issue = check_meta(meta, "person")
            if issue:
                problems.append(f"{rel}：{issue}")
        if isinstance(key, str) and key:
            keys.append((rel, key))
            if meta.get("kind") == "human" and valid_person_key(key):
                humans.add(key)
    seen = set()
    for rel, key in keys:
        if key in seen or sum(1 for _, other in keys if other == key) < 2:
            continue
        involved = "、".join(item for item, other in keys if other == key)
        problems.append(f"身份名 `{key}` 在 {involved} 里重复")
        seen.add(key)
    # 这次要改写的记录才查所属的人。仓库里本来就登不了录、迁移也不会动的 agent 不挡住其他文件。
    for rel, meta, _problem in people:
        if rel in planned and meta.get("kind") == "agent" and meta.get("owner") not in humans:
            problems.append(f"{rel} 的 owner 不是现有的 human")
    return problems


def migrate(root: str | Path, *, apply: bool = False, author: str | None = None,
            signed_as: str | None = None) -> dict:
    root = Path(root)
    if not apply:
        plan = migration_plan(root)
        return {"changes": [{"rel": p["rel"], "fields": p["changes"]} for p in plan], "commit": None,
                "problems": migration_problems(root, plan)}
    if not author or not author.strip():
        raise BaseError("照改时必须传 --author（提交作者身份）。")
    author = _check_signature(author)
    signed_as = _check_signature(signed_as)
    with gitops.write_lock(root):
        plan = migration_plan(root)
        problems = migration_problems(root, plan)
        if problems:
            raise BaseError("成员迁移没有写入：" + "；".join(problems))
        commit = gitops.write_and_commit(root, {p["rel"]: p["content"] for p in plan},
                                        "成员迁移：拆开远程接入与内容授权", "migrate_stage4", author, signed_as) if plan else None
    return {"changes": [{"rel": p["rel"], "fields": p["changes"]} for p in plan], "commit": commit, "problems": []}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="成员字段迁移：默认只列出字段变化。")
    parser.add_argument("--root", required=True, help="由调用人明确指定的数据 git 仓库")
    parser.add_argument("--apply", action="store_true", help="照改并提交一次")
    parser.add_argument("--author", help="提交作者身份，例如 claude")
    parser.add_argument("--signed-as", help="作者署名，例如 alex")
    args = parser.parse_args(argv)
    try:
        result = migrate(args.root, apply=args.apply, author=args.author, signed_as=args.signed_as)
    except BaseError as error:
        parser.error(str(error))
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
