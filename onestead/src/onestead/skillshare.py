"""共享包的统一清单：先过滤，再用同一批字节生成下载包。只向 主人 展示扣下的原因。"""

from __future__ import annotations

import io
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

from .redact import mask_local
from .i18n import text as tr
from .store import BaseError
from .textutil import SECRET_CONTENT, SECRET_PATTERNS

SKIP_DIRS = {".git", "__pycache__", "node_modules", ".venv", "venv", ".pytest_cache"}
MAX_FILE = 5 * 1024 * 1024
MAX_TOTAL = 50 * 1024 * 1024
MAX_TEXT = 1024 * 1024
TOO_LARGE = "这个 Skill 太大，不能直接下载，请找 {owner_name}。"


class PackageTooLarge(BaseError):
    """打包上限错误由调用入口补上主人的显示名。"""


def secret_filename(name: str) -> bool:
    low = name.lower()
    return (low == ".env" or low.startswith(".env.") or ".local." in low
            or any(w in low for w in ("secret", "token", "credential", "password", "cookie"))
            or Path(low).suffix in {".pem", ".key", ".p12", ".pfx", ".keystore", ".jks"}
            or low.startswith(("id_rsa", "id_ed25519")))


@dataclass
class Package:
    files: list[dict] = field(default_factory=list)
    withheld: list[dict] = field(default_factory=list)
    contents: dict[str, bytes] = field(default_factory=dict)
    total: int = 0
    skill_md: str | None = None

    @property
    def too_large(self) -> bool:
        return self.total > MAX_TOTAL


def package_manifest(directory: str | Path, *, capture: bool = False) -> Package:
    """目录联接可作为技能根；逐项核对真实路径，禁止越界，也不追踪循环联接。

    capture 只用于下载：保留筛选时读到的字节，避免 zip 再读盘时把变化后的凭据带出去。
    超过总上限后不再保留字节，仍完成清单供 主人 检查。
    """
    result = Package()
    try:
        root = Path(directory).resolve(strict=True)
    except (OSError, RuntimeError):
        return result

    def omit(rel: str, reason: str) -> None:
        result.withheld.append({"path": rel, "reason": reason})

    def walk(folder: Path, ancestors: frozenset[Path]) -> None:
        try:
            entries = sorted(folder.iterdir(), key=lambda p: p.name)
        except OSError:
            omit(folder.relative_to(root).as_posix(), "文件损坏")
            return
        for entry in entries:
            rel = entry.relative_to(root).as_posix()
            try:
                real = entry.resolve(strict=True)
                if not real.is_relative_to(root):
                    omit(rel, "联接指向技能目录外")
                    continue
                if entry.is_dir():
                    if entry.name.lower() in SKIP_DIRS:
                        omit(rel, "跳过目录")
                    elif secret_filename(entry.name):
                        omit(rel, "文件名像凭据")
                    elif real in ancestors:
                        omit(rel, "循环联接")
                    else:
                        walk(entry, ancestors | {real})
                    continue
                if secret_filename(entry.name):
                    omit(rel, "文件名像凭据")
                    continue
                if not entry.is_file():
                    omit(rel, "文件损坏")
                    continue
                if entry.stat().st_size > MAX_FILE:
                    omit(rel, "太大")
                    continue
                with entry.open("rb") as stream:
                    raw = stream.read(MAX_FILE + 1)
                if entry.resolve(strict=True) != real:
                    omit(rel, "联接指向技能目录外")
                    continue
                if len(raw) > MAX_FILE:
                    omit(rel, "太大")
                    continue
                if raw and not raw.strip(b"\0"):
                    omit(rel, "文件损坏")
                    continue
                text = None
                try:
                    text = raw.decode("utf-8")
                except UnicodeDecodeError:
                    pass
                if text is not None and any(p.search(text) for p in (SECRET_CONTENT, *SECRET_PATTERNS)):
                    omit(rel, "内容里像有密钥")
                    continue
                if text is not None:
                    # 清单、网页正文和 zip 使用同一份处理后的字节，不二次读取原文件。
                    text = mask_local(text)
                    raw = text.encode("utf-8")
                    if len(raw) > MAX_FILE:
                        omit(rel, "太大")
                        continue
                result.files.append({"path": rel, "size": len(raw)})
                result.total += len(raw)
                if rel == "SKILL.md":
                    result.skill_md = text
                if capture and not result.too_large:
                    result.contents[rel] = raw
                elif result.too_large:
                    result.contents.clear()
            except (OSError, RuntimeError):
                omit(rel, "文件损坏")

    if root.is_dir():
        walk(root, frozenset({root}))
    result.files.sort(key=lambda f: f["path"])
    result.withheld.sort(key=lambda f: f["path"])
    return result


def zip_package(skill_id: str, package: Package) -> bytes:
    if package.too_large:
        raise PackageTooLarge(tr("agent.522"))
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for item in package.files:
            archive.writestr(f"{skill_id}/{item['path']}", package.contents[item["path"]])
    return output.getvalue()
