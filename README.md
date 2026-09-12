# deepsleep · AI 助手

Windows 桌面 AI 助手（WinUI 3 / 自带 .NET 运行时），能力类似 Claude Code / Codex。

## 安装
下载 **Releases** 里的 deepsleep-Setup.exe，双击 → 选择安装位置 → 开始安装。
不需要管理员权限；用户数据（API Key、对话历史、长期记忆、自训练模型）都在安装目录的 data 文件夹里，升级不会覆盖。

## 首次使用
1. 打开软件 → ⚙ 设置 → 填写 API 协议格式 / API 地址 / 模型名 / API Key
2. 不想用在线 API 就点「一键安装 Ollama + 大模型」走本地模型
3. 「更新源」填 zhdezs/deepsleep，即可享受 OTA 一键升级

## OTA 自动更新
客户端 → 更新源 填本仓库（zhdezs/deepsleep）→ 启动/手动检查更新 → 自动下载本仓库最新 Release 的安装包、
校验 SHA256、退出后静默覆盖安装并重启，用户数据不受影响。

## 主要能力
- 工具调用链：用户输入 → 分析意图 → tool_call(name+arguments) → 执行 → 结果回传 → 生成最终回复（逐 token 流式）
- 工具：运行命令 / 运行 Python / 读写文件 / 列出目录 / 联网搜索 / 抓取网页 / 生成图片 / 看图 / 记住 / 删除记忆 / 打开文件
- 支持 OpenAI 兼容、Anthropic Claude、Google Gemini、OpenAI Responses API（自动按地址纠正协议）
- Agent 集群：一句话拆成多个 Agent 并行干活
- 技能系统、长期记忆（自动压缩）、断点恢复（强杀/关机后仍可恢复）
- 沙箱与危险操作确认

## 辅助脚本（安装目录 tools\）
- 	ools\set-github-token.ps1：录入 GitHub 令牌（DPAPI 加密）
- 	ools\publish-github-release.ps1：一键把新版本发布到本仓库
