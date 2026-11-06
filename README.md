# deepsleep · Windows AI 助手
&zwnj;**本地优先**&zwnj;的 Windows 桌面 AI 助手（WinUI 3），能力对标 Claude Code / Codex：对话、工具调用、技能库、Agent 集群、长期记忆、断点恢复，外加&zwnj;**安装程序**&zwnj;与 &zwnj;**OTA 自动更新**&zwnj;。自带 .NET 运行时，单文件安装包，&zwnj;**不需要管理员权限**&zwnj;。

| | |
| --- | --- |
| 当前版本 | &zwnj;**3.0.3**&zwnj;（本次修复：&zwnj;**Linux 版之前完全没有 OTA**&zwnj; —— 安装包没带配置、更新源为空，自动检查更新的第一行就返回，等于从来不查；就算查到也装不上：程序装在 root 所有的 `/opt/deepsleep`，就地覆盖必然失败，升级脚本还会 `pgrep` 到自己把自己 kill 掉。现在更新源有默认值、Linux 自更新静默落到用户目录（`~/.local/share/deepsleep/app`，免 root、不用输密码）并写好用户级启动项，全程记 `update.log`；顺带修正版本比较，`.alpha` 测试版以后能被同名正式版覆盖。 |

## ✨ 核心特性亮点
### ① 超级连接（6 位配对码 · 点对点直连 · 内置远程桌面）
桌面版工具栏点 🔗「超级连接」，内核版在 CMD 里输入「打开超级连接」（或 `/superlink`），生成一串 6 位配对码；另一台设备打开 `https://deepsleep.liziserver.site/superlink/?type=配对码`（或在它自己的「超级连接」里输入这 6 位数字）就能连上：同一网络走&zwnj;**点对点局域网直连**&zwnj;（延迟最低），不同网络自动开一条&zwnj;**内置隧道**&zwnj;（手写：直接驱动系统自带的 ssh 建反向隧道，&zwnj;**零下载**&zwnj;；多个免费入口自动择优、连上先自检、断了自动重建）、连上后立刻关掉保持 P2P；连上即可在浏览器里用&zwnj;**内置远程桌面**&zwnj;看画面并用鼠标 / 键盘 / 手机触屏操控。配对码 5 分钟有效、用完即撤，信令只传几 KB 握手消息（默认 ntfy.sh，可用 `DEEPSLEEP_SIGNAL_BASE` 换自建）。

### ② 超远程提问（内网穿透）
内核加 `--tunnel` 就自动建一条免注册的公网隧道（&zwnj;**手写隧道**&zwnj;：用系统自带的 ssh 反向隧道，serveo.net / localhost.run 自动择优，不用下任何组件），启动后直接打印一条带令牌的&zwnj;**配对链接**&zwnj;，手机存成书签，人在外面也能指挥这台电脑；想用自己的穿透工具就 `--public --tunnel-cmd "frpc …"`（或者 `--tunnel-cf 路径` 用已有的 cloudflared），frp / cpolar / ngrok / ssh -R 都行，内核会从命令输出里自己抓公网地址。

### ③ 安全可控机制
- 只有你主动要求才对外：默认依然只听 `127.0.0.1`，加 `--public` / `--host` 才监听网卡，加 `--tunnel` 才挂公网；隧道域名同源放行（网页端能正常调 API），别家网站照样 403。
- 令牌防暴破：令牌输错会递增延迟（400ms×次数，封顶 5 秒）再返回 401；不封 IP，因为隧道下来的来源都是 127.0.0.1，封了会误伤。
- 不带令牌的 `/api/ping` 只说名字和版本，端口 / 运行时长 / 数据目录都要带令牌才给。

### ④ 全平台原生兼容
桌面客户端也能被远程连：桌面版内置的内核服务和内核版是&zwnj;**同一份实现**&zwnj;（`CoreServer` 抽到了共用的 `Deepsleep.Core`），装上就默认监听本机，设置里「超远程提问」点一下就能开 / 关公网隧道，手机在外网一样能连；窗口类命令（showWindow / hideWindow / quitApp）走外壳接管，所以桌面版不会有「Core 有而我没有」的功能。
跨平台桌面版覆盖 Linux（x64 / ARM64）与 macOS（Apple 芯片 / Intel），解压即用，`./deepsleep.sh` 直接开原生桌面窗口，同一个包加 `--headless` 就是内核版；macOS 只发行桌面版。

## 📋 基础运行要求
| 类别 | 说明 |
| --- | --- |
| 系统要求 | 桌面客户端：Windows 10 1809+ / x64（推荐 Windows 11，可享亚克力毛玻璃界面）；Linux（x64 / ARM64，需要系统自带 WebKitGTK + GTK3）与 &zwnj;**macOS 14+**&zwnj;（Apple 芯片 / Intel）都有原生桌面版 |
| 下载地址 | [Releases](https://github.com/zhdezs/deepsleep/releases/latest) → Windows `deepsleep-Setup.exe`；Linux `deepsleep_<版本>_amd64.deb` / `deepsleep-<版本>-1.x86_64.rpm`（另有 arm64 / aarch64）；macOS `deepsleep-<版本>-osx-arm64.dmg` / `deepsleep-<版本>-osx-x64.dmg` |
| OTA 更新源 | `zhdezs/deepsleep`（GitHub + Gitee 双源，⚙ 设置里可切换线路，默认 Gitee） |
| 网页版 | 免安装网页端内置免费模型矩阵：GLM-4.7-Flash 聊天 / GLM-4.6V-Flash 看图 / CogView-3-Flash 画图 |

## 🚀 三种使用方式
| 用法 | 入口 | 能力范围 |
| --- | --- | --- |
| 桌面客户端 | `deepsleep-Setup.exe` / 便携包 | 全功能：工具调用、文件/命令、技能、记忆、Agent 集群、桌宠、OTA |
| 网页版 | 官网 → 网页版（`web/app/`） | 纯前端：聊天 / 看图 / 画图，会话只存在浏览器里；不能碰本机文件与命令 |
| 内核版 + 网页 | `deepsleep-core.exe`（`src/DeepSleepCore`） | 本机只跑内核，用任意浏览器（含外网打开的官网内核页）操控这台电脑 |

> 内核版默认只监听 `127.0.0.1`、所有接口要配对令牌（`data/core-token.txt`，窗口里会打印）、跨域只放行官网与本机页面；令牌等于这台电脑的钥匙，别外传，用完关掉窗口即停。换令牌执行 `deepsleep-core.exe --new-token`，换端口使用 `--port 8888` 参数。

## 🏗️ 内核与界面分离架构（2.0.0 起）


Deepsleep.Core（内核类库，.NET 10，零界面依赖）
▲ 上行 {"id":1,"cmd":"send",...}
▼ 下行 {"ev":"delta","text":"..."}
WinUI 3 外壳（窗口 + 自绘标题栏 + 桌宠）→ WebView2 加载 ui/（纯 HTML/CSS/JS，本地跑）


- 内核只管逻辑：模型调用、工具调用链、技能库、长期记忆、Agent 集群、OTA 更新，&zwnj;**不引用任何界面类型**&zwnj;
- 界面是纯前端：`ui/index.html` + `ui/style.css` + `ui/app.js`，由外壳映射成虚拟站点 `app.local` 在 WebView2 里加载，&zwnj;**改界面不用碰 C#**&zwnj;
- 外壳只做三件事：开窗、把 `ui/` 挂成虚拟站点、转发消息（文件选择器 / 打开链接 / 窗口操作这类系统能力由外壳实现）

## 🛠️ 核心能力详解
### 工具调用链
一句话说清它的工作方式：


#### 用户输入 → LLM 分析意图 → 输出 tool_call(name + arguments) → Agent 执行工具 → 结果回传 LLM → 生成最终回复（逐 token 流式）


- 工具调用走&zwnj;**独立控制通道**&zwnj;：模型这一轮只准输出 JSON，软件解析后执行，结果回灌上下文再继续； &zwnj;**JSON 永远不会漏进聊天气泡**&zwnj;
- 收到的结果让模型单独生成最终回复，真流式逐 token 显示
- API 配置错误或网络不通时&zwnj;**绝不静默降级**&zwnj;，直接把地址、模型名、失败原因显示出来

#### 11 项内置工具清单
`运行命令` · `运行Python脚本` · `生成图片` · `看图` · `联网搜索` · `抓取网页` · `打开文件` · `读取文件` · `写入文件` · `记住` · `删除记忆`

### 其他扩展能力
| 能力 | 说明 |
| --- | --- |
| &zwnj;**多协议接入**&zwnj; | OpenAI 兼容（DeepSeek / 智谱 / 硅基流动 / Groq / OpenRouter）、Anthropic Claude、Google Gemini、&zwnj;**OpenAI Responses API**&zwnj;（`/v1/responses`）；地址只填主机名会自动补全，协议也会按地址自动纠正 |
| &zwnj;**本地模型**&zwnj; | 一键安装 Ollama + 大模型；另有内置自训练小模型（纯 CPU、零依赖） |
| &zwnj;**三种运行模式**&zwnj; | `chat` 纯聊天模式 / `work` 工作模式（执行命令需人工确认） / `boom` 全自动无干预模式 |
| &zwnj;**Agent 集群**&zwnj; | 一句话自动拆分成多个 Agent 并行干活，由指挥官智能体统一汇总输出结果 |
| &zwnj;**技能库**&zwnj; | `data/skills/` 即插即用；支持从 SKILL.md 链接或本地文件夹安装，界面可视化展示完整技能调用链 |
| &zwnj;**长期记忆**&zwnj; | 跨会话记住用户偏好与历史事实，超长内容自动智能压缩，全部存储在 `data/memory.json` |
| &zwnj;**断点恢复**&zwnj; | 任务被中断、程序被杀甚至设备断电，重启后仍可从未完成节点继续执行，状态存储在 `data/runtime_state.json` |
| &zwnj;**沙箱安全**&zwnj; | 危险命令/脚本执行前自动触发确认（含「打开文件」碰到 exe/脚本时）；支持整体收紧权限边界 |
| &zwnj;**透明桌宠**&zwnj; | 透明鲸鱼桌宠（Win32 分层窗口，真透明，按原图 45% 显示）：单击弹出桌宠专属聊天窗（与主界面同一条会话，不切主界面也能聊）、按住拖动摆放位置并记住坐标、右键菜单（聊天 / 打开主界面 / 隐藏 / 退出）；⚙ 设置里可开关，隐藏后状态会被自动记住 |
| &zwnj;**原生界面风格**&zwnj; | macOS 风格自绘标题栏 + 顶部分段控件切换 AI 助手 / Agent 集群、半透明侧边栏、iMessage 风大圆角气泡、系统蓝强调色，深色/浅色两套配色；毛玻璃（DesktopAcrylic）、线条风圆角按钮、呼吸动效气泡、滚动到底部 |

## 📥 安装与首次使用
双击 `deepsleep-Setup.exe` → 选安装位置（默认 `%LOCALAPPDATA%\\Programs\\deepsleep`，&zwnj;**免管理员权限**&zwnj;）→ 完成后自动建快捷方式，并登记到「设置 → 应用 → 已安装的应用」可正常卸载。
静默安装（脚本/批量部署）：
```powershell
deepsleep-Setup.exe --silent --dir "D:\\Apps\\deepsleep" --no-desktop --no-launch
```

首次使用说明

程序‌不含任何预置 API Key‌，每台电脑需单独配置 —— 打开 ⚙ 设置 填写 API 协议 / 地址 / 模型名 / Key。不想使用在线 API，直接点「一键安装 Ollama + 大模型」即可切换到完全离线的本地模型运行模式。

用户数据存储位置

全部用户数据都在‌安装目录的 data/‌：config.json（配置与 Key）、conversations.json（对话）、memory.json（记忆）、runtime_state.json（断点恢复）、cluster.json（集群）、skills/、images/。‌升级安装完全不会覆盖这些文件‌；换电脑把整个 data/ 目录拷贝过去即可完整迁移所有配置和数据。

## ⬆ OTA 自动更新

程序左下角有「⬆ 检查更新」，启动时也会自动检查更新（该功能可手动关闭）。
全自动更新流程：读取更新源 → 下载新安装包（用 GitHub 提供的 SHA256 摘要校验完整性）→ 退出程序 → 静默覆盖安装到原目录 → 自动重启，用户配置、对话、记忆、模型数据完全不受影响。
更新源支持四种写法：

owner/repo（走 GitHub Releases API，推荐）
gitee.com/owner/repo （国内源）
https://.../update.json 直链
本地/局域网路径 D:\\release\\update.json 或 \\服务器\\共享\\update.json
国内加速优化

更新线路可在客户端内手动切换，默认使用 Gitee 国内源：实测下载速度可达 2 MB/s，比 GitHub 直连的 45 KB/s 快 40 倍，校验值仍然使用官方 SHA256，国内镜像仅搬运文件字节无法篡改内容。Gitee 单个附件上限 100MB，大安装包会自动切成分片逐片下载，支持断点续传，拼回整包后再完成完整性校验。只有 Gitee 连不上、中途卡住 20 秒没数据或长时间下载速度低于 60 KB/s，才会自动回退到 GitHub 源继续断点续传。

## 📂 项目完整目录结构
```text
src/Deepsleep.Core/ 内核类库（.NET 10，零界面依赖，可单独构建）
├─ Kernel.cs 协议层：JSON 命令派发 + 事件推送 + 运行状态
├─ Kernel.Agent.cs AI 助手：三态 / 流式输出 / 思考占位 / 权限确认
├─ Kernel.Cluster.cs Agent 集群：拆解 → 并行 → 汇总 / 恢复 / 模板
├─ Kernel.Conv.cs 对话管理、持久化、记忆、技能、设置、上传
├─ Kernel.Ota.cs OTA 自动更新 + Ollama 探测
├─ Kernel.Templates.cs 预置提示词 / 集群模板
├─ Agent.cs 智能体主循环 + 工具调用链 + 11 项工具实现
├─ ApiClient.cs / OllamaClient.cs / ImageGenClient.cs / VisionClient.cs
├─ Sandbox.cs / SkillStore.cs / MemoryStore.cs / ClusterStore.cs / RuntimeState.cs
├─ Updater.cs OTA 更新（GitHub + Gitee Releases / 直链 / 本地，含分片与镜像回退）
├─ TokenVault.cs GitHub 令牌保险箱（多层加密）
└─ Engine.cs / NgramModel.cs / NaiveBayes.cs 内置自训练小模型

src/ WinUI 3 外壳（.NET 10）
├─ MainWindow.xaml(.cs) 外壳窗口：WebView2 宿主 + 自绘标题栏 + 兜底面板
├─ DesktopPet.cs 桌面鲸鱼桌宠（Win32 分层窗口，真透明，45% 缩放 + 拖拽）
├─ PetChatWindow.cs 桌宠专属聊天浮窗（HTML，与主界面同一条会话）
└─ tools/ set-github-token.ps1、publish-github-release.ps1、download-update.ps1

src/DeepSleepDesktop/ 跨平台桌面版外壳（Linux / macOS；照 Windows 桌面版 src/ 改，不是内核套壳）
├─ Program.cs 入口：解析 --data，构造 AppShell
├─ AppShell.cs Photino 窗口 + CoreServer + 界面通道 + 桌宠浮窗（对齐 src/MainWindow.xaml.cs）
└─ PetWindow.cs 桌宠（透明无边框置顶小窗：拖拽 / 单击 / 右键菜单）

src/DeepSleepCore/ 内核版宿主（自带 HTTP/SSE 服务，供浏览器连接；零界面依赖）
├─ Program.cs 启动参数（--port / --data / --token / --new-token）+ 令牌管理
├─ Http.cs / Server.cs 手写 HTTP/1.1 解析 + 路由（/api/* /data/ /web/ /ui/）
├─ ServerCors.cs CORS 白名单（官网 / 本机）+ 配对令牌校验
├─ Home.cs 本机首页（端口、令牌、一键打开）
└─ ServerCmd.cs 把网页命令转给内核 InvokeAsync，并广播 SSE 事件

ui/ 界面（纯 HTML/CSS/JS，由外壳在 WebView2 里本地加载）
├─ index.html 结构（`?pet=1` 为桌宠精简模式）
├─ style.css macOS 风格样式（深浅色两套 + 分段控件 + iMessage 气泡）
└─ app.js 渲染与交互 + 与内核的消息通信（cmd 上行 / ev 下行）

web/ 官网 + 网页版（GitHub Pages 直接托管，根目录 index.html 跳转到 web/）
├─ index.html 官网首页（介绍 / 免费模型矩阵 / 下载 / 内核版说明）
├─ app/index.html 免安装网页版（纯前端聊天 + 看图 + 画图）
├─ core/index.html 内核版网页端（core-bridge.js 把客户端的 webview 协议搬到 HTTP+SSE）
├─ web.js 网页版逻辑（localStorage 会话、SSE 流式、设置里的 AGENT.md / 记忆）
└─ assets/ 官网截图

installer/DeepSleepSetup/ WPF 图形安装程序（单文件，内嵌 payload.zip）
release/ 发布脚本与说明（make-update.ps1、tools/、使用说明.txt）
```

## 🔐 安全与隐私说明
所有数据只在本机安装目录的 data/ 内，‌不主动上传任何内容‌（除你自己配置的模型 API）
API Key 只存在本机 data/config.json；‌安装包内的默认配置里 Key 为空‌
GitHub 令牌用‌多层加密‌保存（data/github.token）：Windows DPAPI 封装密钥 + AES-256-GCM 加密 + 「机器 GUID + 用户 SID + 机器名」作为附加认证 + 文件 ACL 只留当前账号；不写进配置文件、不进运行日志
命令/脚本执行经过沙箱与确认机制；work 模式逐条人工确认，boom 模式全自动执行
## 🧑💻 从源码构建

Windows 编译桌面版：

```powershell
cd src
dotnet publish -c Release -r win-x64 --self-contained true -o dist\\deepsleep-win-x64
```


编译安装包（内嵌应用本体）：

```powershell
# 1) 把 dist（不含 data）覆盖到 release\\deepsleep-setup\\package
# 2) 重打 installer\\payload.zip，编译安装程序
cd installer\\DeepSleepSetup
dotnet publish -c Release -r win-x64 --self-contained true
```


编译内核版（免安装，配合网页端）：

```powershell
dotnet publish src\\DeepSleepCore\\DeepSleepCore.csproj -c Release -f net10.0-windows10.0.19041.0 -o src\\DeepSleepCore\\dist\\core
```

跨平台编译（Linux / macOS）

Linux 与 macOS 使用的是 ‌src/DeepSleepDesktop‌：外壳完全对齐 Windows 桌面版功能，仅将窗口后端替换为 Photino，界面和内核逻辑完全复用 Windows 版本的代码，不是「内核版套个窗口」。

```bash
# 交叉编译（Windows 上也能出 Linux / macOS 的包）：
dotnet publish src/DeepSleepDesktop/DeepSleepDesktop.csproj -c Release -r linux-x64 --self-contained true -o dist/desktop-linux-x64

# 直接运行打开原生桌面窗口
./dist/desktop-linux-x64/deepsleep.sh

# 追加参数切换为纯后台内核模式，浏览器打开 http://127.0.0.1:8756/web/core/ 即可管控
./dist/desktop-linux-x64/deepsleep.sh --headless
```

Linux 桌面需要系统自带 WebKitGTK：Debian / Ubuntu sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0，Fedora / RHEL sudo dnf install webkit2gtk4.1，.deb 包已自动写入 Debian 侧依赖。
macOS 的 .dmg 仅支持 macOS 14+ 系统，从网上下载后若被 Gatekeeper 拦截执行以下命令即可正常运行：
```bash
APP=/Applications/deepsleep.app
sudo xattr -cr "$APP"
sudo codesign --force --sign - "$APP/Contents/MacOS/Photino.Native.dylib"
sudo codesign --force --sign - "$APP"
open "$APP"
```

## 📌 使用说明

deepsleep生成内容仅供参考，请自行判断使用场景并对自己言行负责；请勿用于违规或骚扰用途。
