# deepsleep · Windows AI 助手

**本地优先**的 Windows 桌面 AI 助手（WinUI 3），能力对标 Claude Code / Codex：
对话、工具调用、技能库、Agent 集群、长期记忆、断点恢复，外加**安装程序**与 **OTA 自动更新**。
自带 .NET 运行时，单文件安装包，**不需要管理员权限**。

| | |
| --- | --- |
| 当前版本 | **1.0.11** |
| 系统要求 | Windows 10 1809+ / x64（推荐 Windows 11，可享亚克力毛玻璃界面） |
| 下载 | [Releases](https://github.com/zhdezs/deepsleep/releases/latest) → `deepsleep-Setup.exe` |
| OTA 更新源 | `zhdezs/deepsleep`（GitHub + Gitee 双源，在 ⚙ 设置里填这个即可一键升级） |

---

## 一、核心能力

### 工具调用链
一句话说清它的工作方式：

```
用户输入 → LLM 分析意图 → 输出 tool_call(name + arguments)
        → Agent 执行工具 → 结果回传 LLM → 生成最终回复（逐 token 流式）
```

- 工具调用走**独立控制通道**：模型这一轮只准输出 JSON，软件解析后执行，结果回灌上下文再继续；
  **JSON 永远不会漏进聊天气泡**
- 收到的结果让模型单独生成最终回复，真流式逐 token 显示
- API 配置错误或网络不通时**绝不静默降级**，直接把地址、模型名、失败原因显示出来

### 工具清单（11 项）
`运行命令` · `运行Python脚本` · `生成图片` · `看图` · `联网搜索` · `抓取网页` ·
`打开文件` · `读取文件` · `写入文件` · `记住` · `删除记忆`

### 其他
| 能力 | 说明 |
| --- | --- |
| **多协议接入** | OpenAI 兼容（DeepSeek / 智谱 / 硅基流动 / Groq / OpenRouter）、Anthropic Claude、Google Gemini、**OpenAI Responses API**（`/v1/responses`）；地址只填主机名会自动补全，协议也会按地址自动纠正 |
| **本地模型** | 一键安装 Ollama + 大模型；另有内置自训练小模型（纯 CPU、零依赖） |
| **三种模式** | `chat` 聊天 / `work` 工作（执行命令需确认）/ `boom` 爆破（全自动） |
| **Agent 集群** | 一句话自动拆分成多个 Agent 并行干活，由指挥官汇总 |
| **技能库** | `data/skills/` 即插即用；支持从 SKILL.md 链接或本地文件夹安装，界面可看技能调用链 |
| **长期记忆** | 跨会话记住偏好与事实，超长自动压缩（`data/memory.json`） |
| **断点恢复** | 任务被中断、程序被杀甚至断电，重启后仍可从未完成处继续（`data/runtime_state.json`） |
| **沙箱与确认** | 危险命令/脚本执行前确认（含「打开文件」碰到 exe/脚本时）；可整体收紧权限 |
| **桌面桌宠** | 透明鲸鱼桌宠（Win32 分层窗口，真透明）：可拖动、单击唤出主界面、右键菜单；⚙ 设置里可开关，隐藏后会被记住 |
| **界面** | 毛玻璃（DesktopAcrylic）、线条风圆形按钮、思考中/正在执行 的呼吸动效气泡、滚动到底部 |

---

## 二、安装与首次使用

双击 `deepsleep-Setup.exe` → 选安装位置（默认 `%LOCALAPPDATA%\Programs\deepsleep`，**免管理员**）
→ 完成后自动建快捷方式，并登记到「设置 → 应用 → 已安装的应用」可卸载。

静默安装（脚本/批量部署）：

```powershell
deepsleep-Setup.exe --silent --dir "D:\Apps\deepsleep" --no-desktop --no-launch
```

**首次使用**：程序**不含任何 API Key**，每台电脑需单独配置 —— 打开 `⚙ 设置` 填写 API 协议 / 地址 / 模型名 / Key。
不想用在线 API，就点「一键安装 Ollama + 大模型」走本地模型。

### 数据位置
全部用户数据都在**安装目录的 `data/`**：`config.json`（配置与 Key）、`conversations.json`（对话）、
`memory.json`（记忆）、`runtime_state.json`（断点恢复）、`cluster.json`（集群）、`skills/`、`images/`。
**升级安装不会覆盖**这些文件；换电脑把整个 `data/` 拷过去即可。

---

## 三、OTA 自动更新

程序左下角有「⬆ 检查更新」，启动时也会自动检查（可关闭）。

**全自动流程**：读更新源 → 下载新安装包（用 GitHub 提供的 SHA256 摘要校验）→ 退出程序 →
静默覆盖安装到原目录 → 自动重启。用户数据（Key、对话、记忆、模型）不受影响。

更新源支持四种写法：`owner/repo`（走 GitHub Releases API，推荐）、`gitee.com/owner/repo`
（国内源）、`https://.../update.json` 直链、本地/局域网路径 `D:\release\update.json` 或
`\\服务器\共享\update.json`。

**Gitee 国内源（推荐开）**：填 GitHub 仓库时会自动同时看同名 Gitee 仓库（`zhdezs/deepsleep`），
同一个版本优先从 Gitee 下载（实测约 8 MB/s，比跨境直连快一个数量级），**校验值仍然用 GitHub
官方摘要**，所以镜像搬不了假货。Gitee 单个附件上限 100MB，装不下的大安装包在那边切成
`deepsleep-Setup.exe.part1` / `.part2` 存放，客户端逐片下载、支持断点续传，拼回整包后再校验。
设置里有「同时用 Gitee 同名仓库加速」开关（默认开）；Gitee 那边下不成会自动回退 GitHub 整包。

**国内下载慢 / 断线**：客户端先走直连，失败或中断时自动按顺序切换国内加速镜像
（`ghproxy.net` / `gh-proxy.com` / `hub.gitmirror.com`）**断点续传**接着下；下完仍然用 GitHub
官方给出的 SHA256 校验，镜像只搬字节、改不了内容。

---

## 四、目录结构

```
src/                      WinUI 3 客户端全部源码（.NET 10）
  ├─ MainWindow.xaml(.cs) 主界面（AI 助手 / Agent 集群 / 会话侧栏 / 设置）
  ├─ Agent.cs             智能体主循环 + 工具调用链 + 11 项工具实现
  ├─ ApiClient.cs         在线 API（OpenAI 兼容 / Claude / Gemini / Responses）
  ├─ OllamaClient.cs      本机 Ollama
  ├─ TokenVault.cs        GitHub 令牌保险箱（多层加密）
  ├─ Updater.cs           OTA 自动更新（GitHub + Gitee Releases / 直链 / 本地，含分片与镜像回退）
  ├─ DesktopPet.cs        桌面鲸鱼桌宠（Win32 分层窗口，真透明）
  ├─ Sandbox.cs           命令与脚本沙箱
  ├─ SkillStore.cs        技能库
  ├─ MemoryStore.cs       长期记忆
  ├─ ClusterStore.cs      Agent 集群
  ├─ RuntimeState.cs      断点恢复
  ├─ ImageGenClient.cs    图片生成
  ├─ VisionClient.cs      看图
  ├─ Engine.cs / NgramModel.cs / NaiveBayes.cs   内置自训练小模型
  └─ tools/               set-github-token.ps1、publish-github-release.ps1、download-update.ps1

installer/DeepSleepSetup/ WPF 图形安装程序（单文件，内嵌 payload.zip）
release/                  发布脚本与说明（make-update.ps1、tools/、使用说明.txt）
```

---

## 五、安全与隐私

- 所有数据只在本机安装目录的 `data/` 内，**不主动上传任何内容**（除你自己配置的模型 API）
- API Key 只存在本机 `data/config.json`；**安装包内的默认配置里 Key 为空**
- GitHub 令牌用**多层加密**保存（`data/github.token`）：Windows DPAPI 封装密钥 + AES-256-GCM 加密 +
  「机器 GUID + 用户 SID + 机器名」作为附加认证 + 文件 ACL 只留当前账号；不写进配置文件、不进日志
- 命令/脚本执行经过沙箱与确认机制；`work` 模式逐条确认，`boom` 模式全自动

---

## 六、从源码构建

```powershell
cd src
dotnet publish -c Release -r win-x64 --self-contained true -o dist\deepsleep-win-x64
```

安装包（内嵌应用本体）：

```powershell
# 1) 把 dist（不含 data）覆盖到 release\deepsleep-setup\package
# 2) 重打 installer\payload.zip，编译安装程序
cd installer\DeepSleepSetup
dotnet publish -c Release -r win-x64 --self-contained true
```

发版由维护脚本一体化完成：改版本号 → 编译 → 打包 → 生成 update.json →
推送 GitHub Release 与源码（`release\tools\publish-via-api.py`，需自备 GitHub 令牌）。

---

## 七、说明

生成内容仅供参考，请自行判断使用场景并对自己言行负责；请勿用于违规或骚扰用途。
