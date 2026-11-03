# Contributing to Codex Usage HUD

感谢你愿意帮助改进 Codex Usage HUD。

## 提交反馈

- 普通问题、功能建议和可复现 Bug 请提交 GitHub Issue。
- 不要在公开 Issue 中粘贴真实 rollout、thread ID、用户名、绝对路径、数据库、截图中的私人会话，
  或任何凭据。
- 安全和隐私问题请按 `SECURITY.md` 私下报告。

## 提交代码

1. Fork 本仓库并从 `main` 创建短分支。
2. 只修改与当前问题有关的文件。
3. 保留本地只读、无正文、无凭据、无遥测和不修改 service tier 的边界。
4. 使用合成数据补充或更新测试；禁止提交真实 Codex 会话数据。
5. 运行：

```powershell
.\scripts\test.ps1
.\scripts\source-hierarchy-check.ps1
.\scripts\quota-check.ps1
```

6. 提交 Pull Request，说明问题、方案、验证结果和仍然存在的限制。

维护者可能合并、要求修改或关闭 Pull Request。自动测试通过只是证据，不代表变更一定适合产品。

## License

提交贡献即表示你同意按本仓库的 MIT License 发布该贡献。
