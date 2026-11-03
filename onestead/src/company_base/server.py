"""兼容旧 MCP 模块启动，只转接到新入口。"""

from onestead.server import main, main_remote

if __name__ == "__main__":
    main()
