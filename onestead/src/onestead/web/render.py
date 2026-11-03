"""把 Markdown 转成网页，并过滤掉一切脚本和危险标签。

内容里有 agent 写的进展和决定。页面以 主人 的身份运行，如果有人在内容里混进脚本，
脚本就能替 主人 点“撤回”“批准”。所以只放行排版用的标签，链接只认 http/https。

函数本体在 redact.to_html：同事输出先遮本机路径，核心不能反过来引用网页包。
"""

from ..redact import to_html

__all__ = ["to_html"]
