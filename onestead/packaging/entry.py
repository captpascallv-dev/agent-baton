"""三个同目录入口共享一个 PyInstaller 依赖闭包。"""
from pathlib import Path
import sys

if Path(sys.executable).stem == "onestead-mcp":
    from onestead.server import main
elif Path(sys.executable).stem == "onestead-web":
    from onestead.web.launcher import main
else:
    from onestead.cli import main

main()
