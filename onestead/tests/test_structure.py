"""代码形状：核心不引用网页层；两套投影的公开函数名一致。"""

import ast
from pathlib import Path

ROOT = Path(__file__).parents[1] / "src" / "onestead"

# 进程入口把网页挂到远程服务上。pyproject 把 company-base-remote 钉在 onestead.server，
# online 也从 server.remote_app 拿应用，所以只允许这一处。业务模块不能再引用网页层。
ALLOWED = {("server.py", "web.host")}


def _imported_web(node: ast.AST) -> list[str]:
    found = []
    if isinstance(node, ast.Import):
        for alias in node.names:
            name = alias.name
            if name == "onestead.web" or name.startswith("onestead.web."):
                found.append(name)
    elif isinstance(node, ast.ImportFrom):
        module = node.module or ""
        if node.level == 0:
            if module == "onestead.web" or module.startswith("onestead.web."):
                found.append(module)
        elif module == "web" or module.startswith("web."):
            found.append(module)
        elif module == "":
            for alias in node.names:
                if alias.name == "web" or alias.name.startswith("web."):
                    found.append(alias.name)
    return found


def test_core_modules_do_not_import_web():
    bad = []
    for path in sorted(ROOT.rglob("*.py")):
        if "web" in path.relative_to(ROOT).parts:
            continue
        tree = ast.parse(path.read_text(encoding="utf-8"))
        for node in ast.walk(tree):
            for module in _imported_web(node):
                if (path.name, module) in ALLOWED or (path.name, module.removeprefix("onestead.")) in ALLOWED:
                    continue
                bad.append(f"{path.relative_to(ROOT)}:{getattr(node, 'lineno', '?')} {module}")
            if isinstance(node, ast.Call):
                func = node.func
                name = func.attr if isinstance(func, ast.Attribute) else getattr(func, "id", "")
                if name in {"import_module", "__import__"} and node.args:
                    arg = node.args[0]
                    text = arg.value if isinstance(arg, ast.Constant) and isinstance(arg.value, str) else ""
                    if "onestead.web" in text or text.startswith("web.") or ".web" in text:
                        bad.append(f"{path.relative_to(ROOT)}:{node.lineno} {text}")
    assert bad == []


def _public_names(obj) -> set[str]:
    return {name for name in dir(obj) if not name.startswith("_") and callable(getattr(obj, name))}


def test_projection_public_names_match():
    from onestead.web.projection import LOCAL, REMOTE
    assert _public_names(LOCAL) == _public_names(REMOTE)
