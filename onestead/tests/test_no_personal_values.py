"""公开文件不保留个人身份、项目名或用户目录；只豁免禁止词定义。"""

import ast
from pathlib import Path
import re


FORBIDDEN = re.compile(
    r"pascal|pascallv|BotRoom|C:\\+Users\\+|清清|小葡萄|格格|安安|涂涂|司令|小P|grokbot|grok bot|\btutu\b"
    r"|concerto|montian|captpascal|nutrii|小号地球|codex-plus-cli|\.dsh",
    re.IGNORECASE,
)


def test_no_personal_values():
    source = Path(__file__)
    root = source.parents[1]
    paths = {path for name in ("src", "tests", "docs", "examples")
             for path in (root / name).rglob("*")
             if path.is_file() and "__pycache__" not in path.parts}
    paths.update(path for path in root.glob("README*") if path.is_file())
    paths.update(root / name for name in ("config.example.toml", "pyproject.toml"))
    # 用语法节点界定词表的定义行，新增注释或代码不会跟着整个文件被豁免。
    definition = next(node for node in ast.parse(source.read_text(encoding="utf-8")).body
                      if isinstance(node, ast.Assign)
                      and any(isinstance(target, ast.Name) and target.id == "FORBIDDEN"
                              for target in node.targets))
    definition_lines = range(definition.lineno, definition.end_lineno + 1)
    found = []
    for path in sorted(paths):
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except UnicodeDecodeError:
            continue  # 二进制图标等不属于文本；编译缓存在枚举时已经排除。
        for number, line in enumerate(lines, 1):
            if path == source and number in definition_lines:
                continue
            if FORBIDDEN.search(line):
                found.append(f"{path.relative_to(root)}:{number}")
    assert not found, "公开文件还有个人值：\n" + "\n".join(found)
