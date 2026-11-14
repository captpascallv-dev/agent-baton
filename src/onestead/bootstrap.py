"""本机初始化协议：只读探测、OS 锁、暂存建库及不覆盖的原子发布。"""
from __future__ import annotations

import argparse
from contextlib import contextmanager, nullcontext
import json
import os
from pathlib import Path
import time
import tomllib
import uuid

from . import config, gitops, scaffold
from .git_runtime import run_git
from .i18n import text as tr
from .store import BaseError, Busy, StorageError, Store, member_keys, now_taipei, valid_person_key
from .textutil import _check_text, _dump_front, toml_basic_string

PROTOCOL = 1
IDENTITY = ".onestead/dataset.json"


class BootstrapError(BaseError):
    def __init__(self, code, *, lang="en", **values):
        self.code = code
        super().__init__(tr("bundle." + code, lang=lang, **values))


@contextmanager
def initialization_lock(root: Path):
    """内核持锁，进程被杀立即释放；锁 inode 保留，避免等待者锁住不同文件。"""
    root.parent.mkdir(parents=True, exist_ok=True)
    path = root.parent / ("." + root.name + ".onestead-bootstrap.lock")
    with path.open("a+b") as stream:
        if path.stat().st_size == 0:
            stream.write(b"\0")
            stream.flush()
        deadline = time.monotonic() + 60
        while True:
            stream.seek(0)
            try:
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise Busy(tr("bundle.busy")) from None
                time.sleep(0.05)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream, fcntl.LOCK_UN)


def _settings(path: Path) -> dict:
    try:
        values = tomllib.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}
    if any(key in values and not isinstance(values[key], str) for key in ("data_dir", "owner_key", "state_dir")):
        raise BootstrapError("invalid_config", lang=config.env("LANG") or "en")
    return values


def inspect(root: Path, path: Path, agent: str | None = None) -> dict:
    """不调用任何建目录、写文件或刷新 index 的操作。checks 是实际检查结果。"""
    root, path = root.resolve(), path.resolve()
    checks = dict(template=False, owner=False, agent=agent is None, git=False)
    result = dict(initialized=False, status="absent", dataset_id=None, checks=checks,
                  data_dir=str(root), config_path=str(path), protocol=PROTOCOL)
    if not root.exists() or (root.is_dir() and not any(root.iterdir())):
        return result
    reasons = []
    result.update(status="incomplete", reasons=reasons)
    if not root.is_dir():
        reasons.append("not_directory")
        return result
    saved = _settings(path)
    identity = None
    identity_path = root / IDENTITY
    if identity_path.exists():
        try:
            identity = json.loads(identity_path.read_text(encoding="utf-8"))
            if (not isinstance(identity, dict) or type(identity.get("protocol")) is not int or identity["protocol"] != PROTOCOL or
                    not isinstance(identity.get("dataset_id"), str) or
                    len(identity["dataset_id"]) != 32 or
                    any(c not in "0123456789abcdef" for c in identity["dataset_id"]) or
                    not valid_person_key(identity.get("owner_key"))):
                raise ValueError("identity")
            result["dataset_id"] = identity["dataset_id"]
        except (OSError, ValueError):
            reasons.append("invalid_dataset")
            identity = None
    owner = identity["owner_key"] if identity else saved.get("owner_key") or config.env("OWNER")
    result["owner_key"] = owner
    if (root / ".git").is_dir():
        top = run_git(["rev-parse", "--show-toplevel"], root=root, capture_output=True)
        head = run_git(["rev-parse", "--verify", "HEAD^{commit}"], root=root, capture_output=True)
        if top.returncode == head.returncode == 0:
            checks["git"] = Path(top.stdout.decode("utf-8").strip()).resolve() == root
        if checks["git"]:
            checks["git"] = run_git(["fsck", "--connectivity-only", "--no-reflogs"],
                                     root=root, capture_output=True).returncode == 0
        if checks["git"] and identity is not None:
            committed = run_git(["show", "HEAD:" + IDENTITY], root=root, capture_output=True)
            if committed.returncode != 0 or committed.stdout != identity_path.read_bytes():
                reasons.append("uncommitted_dataset")
        try:
            docs = Store(root).load()
            by_rel = {d.rel: d for d in docs}
            required = ("FORMAT.md", "handbook/company.md", "handbook/collaboration.md")
            checks["template"] = all(rel in by_rel and not by_rel[rel].problem for rel in required)
            checks["template"] &= all((root / d).is_dir() for d in ("people", "projects"))
            members = member_keys(docs)
            person = members.get(owner)
            checks["owner"] = bool(person and not person.problem and person.meta.get("kind") == "human"
                                   and person.meta.get("access_mode") == "full"
                                   and person.meta.get("can_write") is True
                                   and not person.meta.get("archived"))
            if agent is not None:
                bot = members.get(agent)
                checks["agent"] = bool(bot and not bot.problem and bot.meta.get("kind") == "agent"
                                       and bot.meta.get("owner") == owner
                                       and bot.meta.get("access_mode") == "full"
                                       and bot.meta.get("can_write") is True
                                       and not bot.meta.get("archived"))
        except (BaseError, OSError, UnicodeError):
            reasons.append("unreadable_template")
    reasons.extend(key for key, ok in checks.items() if not ok)
    if all(checks.values()) and not reasons:
        result.update(initialized=True, status="ready" if identity else "needs_bootstrap")
    return result


def _identity(owner: str) -> str:
    return json.dumps(dict(protocol=PROTOCOL, dataset_id=uuid.uuid4().hex, owner_key=owner),
                      ensure_ascii=False, indent=2) + "\n"


def _agent(key, name, owner):
    return _dump_front(dict(id=key, type="person", title=name, agent_key=key, kind="agent",
                            owner=owner, remote_enabled=False, access_mode="full", can_write=True,
                            sensitivity="internal")) + f"\n# {name}\n"


def _publish_config(path: Path, root: Path, owner: str, state: Path, lang: str):
    if path.exists():
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name("." + path.name + "." + uuid.uuid4().hex + ".tmp")
    body = "\n".join(f"{k} = {toml_basic_string(str(v))}" for k, v in
                     dict(name="Onestead", lang=lang, data_dir=root, owner_key=owner, state_dir=state).items()) + "\n"
    try:
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(body)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(temporary, path)  # 目标已出现就保留它；不会发布半份 TOML。
        except FileExistsError:
            pass
    finally:
        temporary.unlink(missing_ok=True)
    config.invalidate()


def bootstrap(root: Path, path: Path, state: Path, agent: str, *, agent_name=None, owner_name=None, lang="en"):
    root, path, state = root.resolve(), path.resolve(), state.resolve()
    if not valid_person_key(agent):
        raise BootstrapError("invalid_agent", lang=lang)
    agent_name = _check_text("agent_name", agent_name or agent, limit=80, single_line=True)
    owner_name = _check_text("owner_name", owner_name or tr("bundle.owner", lang=lang), limit=80, single_line=True)
    if path.is_relative_to(root) or state.is_relative_to(root) or path == state:
        raise BootstrapError("invalid_paths", lang=lang)
    saved = _settings(path)
    if saved.get("data_dir") and Path(saved["data_dir"]).expanduser().resolve() != root:
        raise BootstrapError("config_conflict", lang=lang)
    with initialization_lock(root):
        current = inspect(root, path, agent)
        created = current["status"] == "absent"
        if created:
            stage = root.parent / ("." + root.name + ".onestead-stage")
            marker = root.parent / ("." + root.name + ".onestead-transaction.json")
            if marker.exists():
                try:
                    owned = (not marker.is_symlink() and marker.resolve() == marker.absolute() and
                             json.loads(marker.read_text(encoding="utf-8")) == dict(protocol=PROTOCOL, data_dir=str(root)))
                except (OSError, ValueError):
                    owned = False
                if not owned:
                    raise BootstrapError("staging_conflict", lang=lang)
            if stage.exists():
                # 只清理由同一协议登记、同一目标且没有链接跳转的本次暂存目录。
                if (not marker.is_file() or stage.is_symlink() or
                        stage.resolve() != stage.absolute()):
                    raise BootstrapError("staging_conflict", lang=lang)
                from .backup import _to_recycle_bin
                _to_recycle_bin(stage)
                if stage.exists():
                    raise BootstrapError("staging_conflict", lang=lang)
            marker.write_text(json.dumps(dict(protocol=PROTOCOL, data_dir=str(root))), encoding="utf-8")
            stage.mkdir()
            owner = saved.get("owner_key") or "owner-" + uuid.uuid4().hex[:16]
            if not valid_person_key(owner) or owner == agent:
                raise BootstrapError("invalid_agent", lang=lang)
            scaffold._write_tree(stage, lang, owner, owner_name, now_taipei())
            (stage / "people" / (agent + ".md")).write_text(_agent(agent, agent_name, owner), encoding="utf-8")
            (stage / ".onestead").mkdir()
            (stage / IDENTITY).write_text(_identity(owner), encoding="utf-8")
            scaffold._commit(stage, owner, owner_name)
            verified = inspect(stage, path, agent)
            if verified["status"] != "ready":
                raise BootstrapError("verification_failed", lang=lang)
            # rename 不覆盖非空目标；空目录也先由调用方建立，使用 rmdir 只处理空目录。
            if root.exists():
                root.rmdir()
            stage.rename(root)
            marker.unlink(missing_ok=True)
        else:
            fatal = set(current.get("reasons", [])) - {"agent", "owner"}
            owner = current.get("owner_key")
            if saved.get("owner_key") and saved["owner_key"] != owner:
                raise BootstrapError("config_conflict", lang=lang)
            if fatal or not valid_person_key(owner) or agent == owner:
                return _result("needs_repair", current, path, state, agent)
            members = member_keys(Store(root).load())
            if ((root / "people" / (agent + ".md")).exists() and not current["checks"]["agent"]) or \
                    ((root / "people" / (owner + ".md")).exists() and not current["checks"]["owner"]):
                current.setdefault("reasons", []).append("member_conflict")
                return _result("needs_repair", current, path, state, agent)
            with gitops.write_lock(root) if current["status"] != "ready" else nullcontext():
                additions = {}
                if not (root / IDENTITY).exists():
                    additions[IDENTITY] = _identity(owner)
                if not (root / "people" / (owner + ".md")).exists():
                    values = dict(owner_key=owner, owner_name=owner_name,
                                  owner_name_yaml=toml_basic_string(owner_name))
                    additions[f"people/{owner}.md"] = scaffold._fill(scaffold._read_template(lang, "owner.md"), values)
                if agent not in members:
                    if (root / "people" / (agent + ".md")).exists():
                        current.setdefault("reasons", []).append("member_conflict")
                        return _result("needs_repair", current, path, state, agent)
                    additions[f"people/{agent}.md"] = _agent(agent, agent_name, owner)
                if additions:
                    gitops.write_and_commit(root, additions, tr("bundle.upgrade", lang=lang),
                                            "bootstrap", owner, None, _initialize_dataset=True)
        _publish_config(path, root, owner, state, lang)
        final = inspect(root, path, agent)
        if final["status"] != "ready":
            raise BootstrapError("verification_failed", lang=lang)
        return _result("created" if created else "existing", final, path, state, agent)


def _result(status, checked, path, state, agent):
    return dict(status=status, dataset_id=checked["dataset_id"], owner_key=checked.get("owner_key"),
                agent_key=agent, data_dir=checked["data_dir"], config_path=str(path), state_dir=str(state),
                protocol=PROTOCOL, **({"reasons": checked.get("reasons", [])} if status == "needs_repair" else {}))


def run_cli(command, argv):
    # 独立 parser 保证坏配置、参数错误也能返回一行 JSON。
    class Parser(argparse.ArgumentParser):
        def error(self, message):
            raise BootstrapError("invalid_arguments", lang=language, detail=message)

    language = "zh" if config.env("LANG") == "zh" else "en"
    if "--lang" in argv and max(i for i, v in enumerate(argv) if v == "--lang") + 1 < len(argv):
        language = argv[max(i for i, v in enumerate(argv) if v == "--lang") + 1]
        if language not in ("zh", "en"):
            language = "en"
    parser = Parser(prog="onestead " + command)
    config.path_arguments(parser)
    parser.add_argument("--agent", required=command == "bootstrap")
    parser.add_argument("--lang", choices=("zh", "en"), default=language)
    parser.add_argument("--json", action="store_true")
    if command == "bootstrap":
        parser.add_argument("--agent-name")
        parser.add_argument("--owner-name")
    try:
        args = parser.parse_args(argv)
        with config.use_paths(args):
            root, path = Path(config.data_dir()), config.config_path()
            if command == "inspect":
                result = inspect(root, path, args.agent)
            else:
                result = bootstrap(root, path, config.state_dir(), args.agent, lang=args.lang,
                                   agent_name=args.agent_name, owner_name=args.owner_name)
        code = 2 if result.get("status") == "needs_repair" else 0
    except BootstrapError as error:
        result, code = dict(status="error", code=error.code, message=str(error)), 1
    except (tomllib.TOMLDecodeError, UnicodeError):
        result, code = dict(status="error", code="invalid_config", message=tr("bundle.invalid_config", lang=language)), 1
    except Busy:
        result, code = dict(status="error", code="busy", message=tr("bundle.busy", lang=language)), 1
    except StorageError:
        result, code = dict(status="error", code="git_failed", message=tr("bundle.git_failed", lang=language)), 1
    except BaseError as error:
        result, code = dict(status="error", code="invalid_arguments", message=str(error)), 1
    except OSError as error:
        result, code = dict(status="error", code="io_error", message=tr("bundle.io_error", lang=language, detail=str(error))), 1
    print(json.dumps(result, ensure_ascii=False))
    return code


