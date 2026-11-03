"""兼容旧命令行模块启动，只转接到新入口。"""

from onestead.cli import main

if __name__ == "__main__":
    main()
