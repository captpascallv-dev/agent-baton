"""本平台原生构建 onedir；工作目录在源码之外，缓存也只放工作目录。"""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata as metadata
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tarfile
import tomllib
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SOURCES = json.loads((ROOT / "packaging/git-sources.json").read_text(encoding="utf-8"))


def download(key, cache, supplied=None):
    item = SOURCES[key]
    path = Path(supplied).resolve() if supplied else cache / item["url"].rsplit("/", 1)[1]
    if not path.exists():
        with urllib.request.urlopen(item["url"], timeout=120) as response, path.open("wb") as out:
            shutil.copyfileobj(response, out)
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != item["sha256"]:
        raise RuntimeError(f"{key}: checksum mismatch: {digest}")
    print(f"Verified {key} {item['version']}: {digest}", flush=True)
    return path


def run(command, **kwargs):
    print("+ " + " ".join(map(str, command)), flush=True)
    subprocess.run(list(map(str, command)), check=True, **kwargs)


def portable_git(work, cache, dest):
    """只依赖 OS ABI；Git 自带 hash 实现，zlib 静态链接，禁用非本机功能。"""
    zarchive, garchive = download("zlib", cache), download("git", cache)
    source = work / "git-build"
    source.mkdir()
    for archive in (zarchive, garchive):
        with tarfile.open(archive) as stream:
            stream.extractall(source, filter="data")
    zsrc, gsrc = source / "zlib-1.3.1", source / "git-2.56.1"
    zprefix = source / "zlib-installed"
    run(["sh", "./configure", "--static", "--prefix=" + str(zprefix)], cwd=zsrc)
    run(["make", "-j2"], cwd=zsrc)
    run(["make", "install"], cwd=zsrc)
    # C-only local operations cover init/write/history/revert/archive/bundle/clone.
    options = ["prefix=" + str(dest), "RUNTIME_PREFIX=YesPlease", "NO_GETTEXT=YesPlease",
               "NO_TCLTK=YesPlease", "NO_PERL=YesPlease", "NO_PYTHON=YesPlease",
               "NO_CURL=YesPlease", "NO_EXPAT=YesPlease", "NO_ICONV=YesPlease",
               "NO_OPENSSL=YesPlease", "NO_INSTALL_HARDLINKS=YesPlease", "NO_HOMEBREW=YesPlease",
               "NO_FINK=YesPlease", "NO_DARWIN_PORTS=YesPlease",
               "ZLIB_PATH=" + str(zprefix)]
    run(["make", "-j2", *options, "all"], cwd=gsrc)
    run(["make", *options, "install"], cwd=gsrc)
    # 保留对应源码、许可和配方，便于重建和满足再分发义务。
    sources = dest.parent / "SOURCES"
    sources.mkdir()
    for archive in (zarchive, garchive):
        shutil.copy2(archive, sources / archive.name)
    shutil.copy2(gsrc / "COPYING", dest.parent / "LICENSES/Git-COPYING")
    shutil.copy2(zsrc / "LICENSE", dest.parent / "LICENSES/zlib-LICENSE")
    return "git/bin/git"


def licenses(artifact):
    folder = artifact / "LICENSES"
    folder.mkdir(exist_ok=True)
    shutil.copy2(ROOT / "LICENSE", folder / "Onestead-LICENSE")
    python_license = Path(sys.base_prefix) / "LICENSE.txt"
    if not python_license.exists():
        python_license = Path(sys.base_prefix) / "lib/python3.12/LICENSE.txt"
    if not python_license.exists():
        python_license = ROOT / "packaging/licenses/CPython-3.12.13-LICENSE.txt"
    shutil.copy2(python_license, folder / "CPython-LICENSE.txt")
    inventory = []
    for dist in sorted(metadata.distributions(), key=lambda d: d.metadata["Name"].lower()):
        name = dist.metadata["Name"]
        if name.lower() == "onestead":
            continue
        texts = []
        for file in dist.files or []:
            normalized = str(file).replace("\\", "/")
            parts = normalized.lower().split("/")
            if (".." not in parts and any("license" in p or "copying" in p or "notice" in p for p in parts)
                    and dist.locate_file(file).is_file()):
                target = folder / name / normalized
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(dist.locate_file(file), target)
                texts.append(target.relative_to(artifact).as_posix())
        inventory.append(dict(name=name, version=dist.version,
                              license=dist.metadata.get("License-Expression") or dist.metadata.get("License"),
                              project_urls=dist.metadata.get_all("Project-URL", []), files=texts))
    # Python 扩展随附的 OpenSSL/libffi 等条款保留在 wheel 许可证目录及 CPython LICENSE 中。
    (folder / "python-packages.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2), encoding="utf-8")


def application_source(artifact, version):
    target = artifact / "SOURCES" / f"onestead-{version}-source.zip"
    target.parent.mkdir(parents=True, exist_ok=True)
    files = []
    for folder in ("src", "tests", "docs", "packaging", ".github"):
        files.extend(p for p in (ROOT / folder).rglob("*") if p.is_file()
                     and "__pycache__" not in p.parts and p.suffix != ".pyc")
    files.extend(ROOT / name for name in ("pyproject.toml", "uv.lock", "README.md", "README.zh.md", "LICENSE", "config.example.toml"))
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as stream:
        for file in sorted(files):
            stream.write(file, file.relative_to(ROOT).as_posix())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--git-archive", type=Path)
    args = parser.parse_args()
    work, output = args.work.resolve(), args.output.resolve()
    if work.is_relative_to(ROOT) or output.is_relative_to(ROOT):
        parser.error("build and output directories must be outside the source checkout")
    system = {"win32": "windows", "darwin": "macos", "linux": "linux"}[sys.platform]
    arch = {"AMD64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}[platform.machine()]
    if (system, arch) not in (("windows", "x64"), ("macos", "arm64"), ("macos", "x64"), ("linux", "x64")):
        parser.error("unsupported native target")
    if sys.version_info[:3] != (3, 12, 13):
        parser.error("build requires CPython 3.12.13")
    from packaging.requirements import Requirement
    for line in (ROOT / "packaging/requirements-build.lock").read_text().splitlines():
        if not line or line.startswith("#"):
            continue
        item = Requirement(line)
        if (not item.marker or item.marker.evaluate()) and metadata.version(item.name) not in item.specifier:
            raise RuntimeError("Build dependency does not match lock: " + item.name)
    work.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    cache = work / "downloads"
    cache.mkdir(exist_ok=True)
    env = dict(os.environ, PYINSTALLER_CONFIG_DIR=str(work / "pyinstaller-config"),
               PYTHONPATH=str(ROOT / "src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
    run([sys.executable, "-m", "PyInstaller", "--noconfirm", "--workpath", work / "pyinstaller-work",
         "--distpath", work / "dist", ROOT / "packaging/onestead.spec"], env=env)
    artifact = work / "dist/onestead"
    suffix = ".exe" if system == "windows" else ""
    for name in ("onestead-mcp", "onestead-web"):
        shutil.copy2(artifact / ("onestead" + suffix), artifact / (name + suffix))
    licenses(artifact)
    if system == "windows":
        archive = download("mingit", cache, args.git_archive)
        with zipfile.ZipFile(archive) as stream:
            stream.extractall(artifact / "git")
        shutil.copy2(artifact / "git/LICENSE.txt", artifact / "LICENSES/MinGit-LICENSE.txt")
        for directory in (artifact / "git").glob("*/share/licenses"):
            shutil.copytree(directory, artifact / "LICENSES" / (directory.parent.parent.name + "-git"))
        git_entry = "git/cmd/git.exe"
    else:
        git_entry = portable_git(work, cache, artifact / "git")
        command = ["otool", "-L", str(artifact / git_entry)] if system == "macos" else ["ldd", str(artifact / git_entry)]
        with (output / "git-linkage.txt").open("w", encoding="utf-8") as log:
            run(command, stdout=log)
    version = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))["project"]["version"]
    runtime = dict(schema=1, name="onestead", version=version, platform=system, arch=arch, protocol=1,
                   entries=dict(cli="onestead" + suffix, mcp="onestead-mcp" + suffix,
                                web="onestead-web" + suffix, git=git_entry))
    (artifact / "runtime.json").write_text(json.dumps(runtime, indent=2) + "\n", encoding="utf-8")
    shutil.copy2(ROOT / "packaging/SOURCES.md", artifact / "SOURCES.md")
    shutil.copy2(ROOT / "packaging/NOTICE", artifact / "NOTICE")
    recipes = artifact / "SOURCES/build-recipes"
    recipes.mkdir(parents=True, exist_ok=True)
    for name in ("build.py", "git-sources.json", "requirements-build.lock", "onestead.spec", "entry.py"):
        shutil.copy2(ROOT / "packaging" / name, recipes / name)
    application_source(artifact, version)
    archive = output / f"onestead-{version}-{system}-{arch}.zip"
    # zipfile 不能保留 POSIX 可执行位的自动解压行为；smoke.py 按 external_attr 还原。
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as stream:
        for file in sorted(artifact.rglob("*")):
            if file.is_file():
                stream.write(file, (Path("onestead") / file.relative_to(artifact)).as_posix())
    sizes = dict(archive=str(archive), zip_bytes=archive.stat().st_size,
                 unpacked_bytes=sum(p.stat().st_size for p in artifact.rglob("*") if p.is_file()), runtime=runtime)
    (output / "build-result.json").write_text(json.dumps(sizes, indent=2), encoding="utf-8")
    print(json.dumps(sizes), flush=True)


if __name__ == "__main__":
    main()
