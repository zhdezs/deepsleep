﻿﻿# deepsleep · Windows AI 助手

**本地优先**的 Windows 桌面 AI 助手（WinUI 3），能力对标 Claude Code / Codex：
对话、工具调用、技能库、Agent 集群、长期记忆、断点恢复，外加**安装程序**与 **OTA 自动更新**。
自带 .NET 运行时，单文件安装包，**不需要管理员权限**。

| | |
| --- | --- |
| 当前版本 | **3.0.3**（本次修复：**Linux 版之前完全没有 OTA** —— 安装包没带配置、更新源为空，自动检查更新的第一行就返回，等于从来不查；就算查到也装不上：程序装在 root 所有的 `/opt/deepsleep`，就地覆盖必然失败，升级脚本还会 `pgrep` 到自己把自己 kill 掉。现在更新源有默认值、Linux 自更新静默落到用户目录（`~/.local/share/deepsleep/app`，免 root、不用输密码）并写好用户级启动项，全程记 `update.log`；顺带修正版本比较，`.alpha` 测试版以后能被同名正式版覆盖。① **超级连接（6 位配对码 · 点对点直连 · 内置远程桌面）** —— 桌面版工具栏点 🔗「超级连接」，内核版在 CMD 里输入「打开超级连接」（或 `/superlink`），生成一串 6 位配对码；另一台设备打开 `https://zhdezs.github.io/deepsleep/superlink/?type=配对码`（或在它自己的「超级连接」里输入这 6 位数字）就连上：同一网络走**点对点局域网直连**（延迟最低），不同网络自动开一条**内置隧道**（手写：直接驱动系统自带的 ssh 建反向隧道，**零下载**；多个免费入口自动择优、连上先自检、断了自动重建）、连上后立刻关掉保持 P2P；连上即可在浏览器里用**内置远程桌面**看画面并用鼠标 / 键盘 / 手机触屏操控。配对码 5 分钟有效、用完即撤，信令只传几 KB 握手消息（默认 ntfy.sh，可用 `DEEPSLEEP_SIGNAL_BASE` 换自建）。② **超远程提问（内网穿透）** —— 内核加 `--tunnel` 就自动建一条免注册的公网隧道（**手写隧道**：用系统自带的 ssh 反向隧道，serveo.net / localhost.run 自动择优，不用下任何组件），启动后直接打印一条带令牌的**配对链接**，手机存成书签，人在外面也能指挥这台电脑；想用自己的穿透工具就 `--public --tunnel-cmd "frpc …"`（或者 `--tunnel-cf 路径` 用已有的 cloudflared），frp / cpolar / ngrok / ssh -R 都行，内核会从命令输出里自己抓公网地址。② **只有你主动要求才对外** —— 默认依然只听 `127.0.0.1`，加 `--public` / `--host` 才监听网卡，加 `--tunnel` 才挂公网；隧道域名同源放行（网页端能正常调 API），别家网站照样 403。③ **令牌防暴破** —— 令牌输错会递增延迟（400ms×次数，封顶 5 秒）再返回 401；不封 IP，因为隧道下来的来源都是 127.0.0.1，封了会误伤。④ **桌面客户端也能被远程连** —— 桌面版内置的内核服务和内核版是**同一份实现**（`CoreServer` 抽到了共用的 `Deepsleep.Core`），装上就默认监听本机，设置里「超远程提问」点一下就能开 / 关公网隧道，手机在外网一样能连；窗口类命令（showWindow / hideWindow / quitApp）走外壳接管，所以桌面版不会有「Core 有而我没有」的功能。⑤ **不带令牌的 `/api/ping` 只说名字和版本**，端口 / 运行时长 / 数据目录都要带令牌才给。其余与 2.3.2 一致：命令输出中文不乱码、运行的命令点开看完整输出、桌宠资源回收、兜底面板不再误报、chat 模式不落盘、代码块兜底不吞正文、DSML 工具标记直接执行、截断自动续写、Agent 名片 + 按需派工、集群名册只增不减、内核跨平台（Windows / Linux / macOS）；⑥ **跨平台桌面版** —— Linux（x64 / ARM64）与 macOS（Apple 芯片 / Intel）解压即用，`./deepsleep.sh` 直接开原生桌面窗口（Linux 直接发 `.deb` / `.rpm`，macOS 发 `.dmg`，也都有免安装的 `.tar.gz`；同一个包加 `--headless` 就是内核版；macOS 只发行桌面版） |
| 系统要求 | 桌面客户端：Windows 10 1809+ / x64（推荐 Windows 11，可享亚克力毛玻璃界面）；Linux（x64 / ARM64，需要系统自带 WebKitGTK + GTK3）与 macOS 11+（Apple 芯片 / Intel）都有原生桌面版 |
| 下载 | [Releases](https://github.com/zhdezs/deepsleep/releases/latest) → Windows `deepsleep-Setup.exe`；Linux `deepsleep_<版本>_amd64.deb` / `deepsleep-<版本>-1.x86_64.rpm`（另有 arm64 / aarch64）；macOS `deepsleep-<版本>-osx-arm64.dmg` / `deepsleep-<版本>-osx-x64.dmg` |
| OTA 更新源 | `zhdezs/deepsleep`（GitHub + Gitee 双源，⚙ 设置里可切换线路，默认 Gitee） |
| 官网 / 网页版 | <https://zhdezs.github.io/deepsleep/>（网页版免安装，内置免费模型矩阵：GLM-4.7-Flash 聊天 / GLM-4.6V-Flash 看图 / CogView-3-Flash 画图） |
| 内核版 | `deepsleep-core-<版本>-win-x64.zip`（Windows）；Linux · macOS 直接给 `src/DeepSleepDesktop` 编译出来的**桌面版**：装 `.deb` / `.rpm` / `.dmg`，或者下免安装的 `deepsleep-<版本>-<平台>.tar.gz` 解压即用（`./deepsleep.sh` 直接开原生桌面窗口，加 `--headless` 就是内核模式，用浏览器连上操控本机） |

### 三种用法

| 用法 | 入口 | 能力 |
| --- | --- | --- |
| 桌面客户端 | `deepsleep-Setup.exe` / 便携包 | 全功能：工具调用、文件/命令、技能、记忆、Agent 集群、桌宠、OTA |
| 网页版 | 官网 → 网页版（`web/app/`） | 纯前端：聊天 / 看图 / 画图，会话只存在浏览器里；不能碰本机文件与命令 |
| 内核版 + 网页 | `deepsleep-core.exe`（`src/DeepSleepCore`） | 本机只跑内核，用任意浏览器（含外网打开的官网内核页）操控这台电脑 |

内核版只监听 `127.0.0.1`、所有接口要配对令牌（`data/core-token.txt`，窗口里会打印）、
跨域只放行官网与本机页面；令牌等于这台电脑的钥匙，别外传，用完关掉窗口即停。
换令牌 `deepsleep-core.exe --new-token`，换端口 `--port 8888`。

### 架构：内核与界面分离（2.0.0 起）

```
Deepsleep.Core（内核类库，.NET 10，零界面依赖）
   ▲ 上行 {"id":1,"cmd":"send",...}      ▼ 下行 {"ev":"delta","text":"..."}
WinUI 3 外壳（窗口 + 自绘标题栏 + 桌宠）→ WebView2 加载 ui/（纯 HTML/CSS/JS，本地跑）
```

- 内核只管逻辑：模型调用、工具调用链、技能库、长期记忆、Agent 集群、OTA 更新，**不引用任何界面类型**
- 界面是纯前端：`ui/index.html` + `ui/style.css` + `ui/app.js`，由外壳映射成虚拟站点 `app.local` 在 WebView2 里加载，**改界面不用碰 C#**
- 外壳只做三件事：开窗、把 `ui/` 挂成虚拟站点、转发消息（文件选择器 / 打开链接 / 窗口操作这类系统能力由外壳实现）

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
| **桌面桌宠** | 透明鲸鱼桌宠（Win32 分层窗口，真透明，按原图 45% 显示）：**单击弹出桌宠专属聊天窗**（与主界面同一条会话，不切主界面也能聊）、**按住拖动**摆位置并记住坐标、右键菜单（聊天 / 打开主界面 / 隐藏 / 退出）；⚙ 设置里可开关，隐藏后会被记住 |
| **界面（macOS 风格）** | 自绘标题栏 + 顶部分段控件切换 AI 助手 / Agent 集群、半透明侧边栏、iMessage 风大圆角气泡、系统蓝强调色，深色/浅色两套配色；毛玻璃（DesktopAcrylic）、线条风圆角按钮、呼吸动效气泡、滚动到底部 |

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

**更新线路：客户端里可切换，默认 Gitee**（⚙ 设置 → OTA 自动更新 → 更新线路）。填 GitHub 仓库时会
自动同时看同名 Gitee 仓库（`zhdezs/deepsleep`），同一个版本优先从 Gitee 下载（实测 **2 MB/s** 上下，
同一时刻 GitHub 直连只有 **45 KB/s**，差 40 多倍），**校验值仍然用官方 SHA256**，所以镜像搬不了假货。
Gitee 单个附件上限 100MB，装不下的大安装包在那边切成 `deepsleep-Setup.exe.part1` / `.part2` 存放，
客户端逐片下载、支持断点续传，拼回整包后再校验。

- **Gitee 国内源（默认）**：**不再探速比快慢**，直接从 Gitee 下；只有 Gitee 连不上、中途卡住（20 秒没数据）
  或长时间低于 60 KB/s，才回退 GitHub 接着下——回退同样按官方 SHA256 校验。
- **GitHub**：走 GitHub 直连（失败自动切国内加速镜像、断点续传），想强制走 GitHub 时选它。

Gitee 那条 Release 说明里会带上安装包的 SHA256，所以走 Gitee 时整条链路连 GitHub API 都不用访问
（国内网络下这点很关键），校验也不打折。

**国内下载慢 / 断线**：客户端先走直连，失败或中断时自动按顺序切换国内加速镜像
（`ghproxy.net` / `gh-proxy.com` / `hub.gitmirror.com`）**断点续传**接着下；下完仍然用 GitHub
官方给出的 SHA256 校验，镜像只搬字节、改不了内容。

---

## 四、目录结构

```
src/Deepsleep.Core/       内核类库（.NET 10，零界面依赖，可单独构建）
  ├─ Kernel.cs            协议层：JSON 命令派发 + 事件推送 + 运行状态
  ├─ Kernel.Agent.cs      AI 助手：三态 / 流式输出 / 思考占位 / 权限确认
  ├─ Kernel.Cluster.cs    Agent 集群：拆解 → 并行 → 汇总 / 恢复 / 模板
  ├─ Kernel.Conv.cs       对话管理、持久化、记忆、技能、设置、上传
  ├─ Kernel.Ota.cs        OTA 自动更新 + Ollama 探测
  ├─ Kernel.Templates.cs  预置提示词 / 集群模板
  ├─ Agent.cs             智能体主循环 + 工具调用链 + 11 项工具实现
  ├─ ApiClient.cs / OllamaClient.cs / ImageGenClient.cs / VisionClient.cs
  ├─ Sandbox.cs / SkillStore.cs / MemoryStore.cs / ClusterStore.cs / RuntimeState.cs
  ├─ Updater.cs           OTA 更新（GitHub + Gitee Releases / 直链 / 本地，含分片与镜像回退）
  ├─ TokenVault.cs        GitHub 令牌保险箱（多层加密）
  └─ Engine.cs / NgramModel.cs / NaiveBayes.cs   内置自训练小模型

src/                      WinUI 3 外壳（.NET 10）
  ├─ MainWindow.xaml(.cs) 外壳窗口：WebView2 宿主 + 自绘标题栏 + 兜底面板
  ├─ DesktopPet.cs        桌面鲸鱼桌宠（Win32 分层窗口，真透明，45% 缩放 + 拖拽）
  ├─ PetChatWindow.cs     桌宠专属聊天浮窗（HTML，与主界面同一条会话）
  └─ tools/               set-github-token.ps1、publish-github-release.ps1、download-update.ps1

src/DeepSleepDesktop/    跨平台桌面版外壳（Linux / macOS；照 Windows 桌面版 src/ 改，不是内核套壳）
  ├─ Program.cs           入口：解析 --data，构造 AppShell
  ├─ AppShell.cs          Photino 窗口 + CoreServer + 界面通道 + 桌宠浮窗（对齐 src/MainWindow.xaml.cs）
  └─ PetWindow.cs         桌宠（透明无边框置顶小窗：拖拽 / 单击 / 右键菜单）

src/DeepSleepCore/        内核版宿主（自带 HTTP/SSE 服务，供浏览器连接；零界面依赖）
  ├─ Program.cs           启动参数（--port / --data / --token / --new-token）+ 令牌管理
  ├─ Http.cs / Server.cs  手写 HTTP/1.1 解析 + 路由（/api/* /data/ /web/ /ui/）
  ├─ ServerCors.cs        CORS 白名单（官网 / 本机）+ 配对令牌校验
  ├─ Home.cs              本机首页（端口、令牌、一键打开）
  └─ ServerCmd.cs         把网页命令转给内核 InvokeAsync，并广播 SSE 事件

ui/                       界面（纯 HTML/CSS/JS，由外壳在 WebView2 里本地加载）
  ├─ index.html           结构（`?pet=1` 为桌宠精简模式）
  ├─ style.css            macOS 风格样式（深浅色两套 + 分段控件 + iMessage 气泡）
  └─ app.js               渲染与交互 + 与内核的消息通信（cmd 上行 / ev 下行）

web/                      官网 + 网页版（GitHub Pages 直接托管，根目录 index.html 跳转到 web/）
  ├─ index.html           官网首页（介绍 / 免费模型矩阵 / 下载 / 内核版说明）
  ├─ app/index.html       免安装网页版（纯前端聊天 + 看图 + 画图）
  ├─ core/index.html      内核版网页端（core-bridge.js 把客户端的 webview 协议搬到 HTTP+SSE）
  ├─ web.js               网页版逻辑（localStorage 会话、SSE 流式、设置里的 AGENT.md / 记忆）
  └─ assets/              官网截图

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

内核版（免安装，配合网页端）：

```powershell
dotnet publish src\DeepSleepCore\DeepSleepCore.csproj -c Release -f net10.0-windows10.0.19041.0 -o src\DeepSleepCore\dist\core
```

### 跨平台桌面版（Linux / macOS）

Linux 与 macOS 用的是 **`src/DeepSleepDesktop`**：外壳照 Windows 桌面版（`src\MainWindow.xaml.cs`）改，
只把窗口后端换成 Photino（Linux = WebKitGTK，macOS = WKWebView），界面还是 `ui/` 那一套 HTML，
内核还是 `src/Deepsleep.Core` —— 所以桌面版有的功能它一个不少（工具、文件、命令、记忆、技能、
网络搜索 + 深度研究、Agent 集群、桌宠、超级连接、超远程提问），**不是「内核版套个窗口」**。

```bash
# 交叉编译（Windows 上也能出 Linux / macOS 的包）：
dotnet publish src/DeepSleepDesktop/DeepSleepDesktop.csproj -c Release -r linux-x64 --self-contained true -o dist/desktop-linux-x64
#   -r linux-x64 / linux-arm64 / osx-x64 / osx-arm64 任选
./dist/desktop-linux-x64/deepsleep.sh              # 直接开原生桌面窗口
./dist/desktop-linux-x64/deepsleep.sh --headless   # 只跑内核服务：浏览器打开 http://127.0.0.1:8756/web/core/
```

- 安装包：Linux 出 `.deb`（Debian / Ubuntu）与 `.rpm`（Fedora / RHEL / openSUSE），macOS 出 `.dmg`。
  deb / rpm 的格式是**手写**的（`release\tools\mk-linux-pkgs.py`，本机没有 dpkg-deb / rpmbuild），
  写完会自己再解析一遍自检：头里的文件表、cpio 顺序、摘要、大小全对上才算过。
- `.dmg` 只能在 macOS 上生成（hdiutil / codesign），所以交给 `.github/workflows/macos-dmg.yml`
  在 GitHub 的 `macos-14` runner 上跑：下载 Release 里的 `osx-*.tar.gz` → 拼成 `deepsleep.app`
  （带 Info.plist 与 .icns）→ ad-hoc 签名 → `hdiutil` 压成 `.dmg` → 传回同一个 Release。
  `发布.cmd` 发完版会自动触发它，也可以在 Actions 页面手动 Run。
- Linux 桌面需要系统自带 WebKitGTK：Debian / Ubuntu `sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0`，
  Fedora / RHEL `sudo dnf install webkit2gtk4.1`（`.deb` 已把 Debian 侧的依赖写进 Depends）。
- macOS 的 `.dmg` 是临时签名（没有 Apple 开发者证书），第一次打开要右键 →「打开」。
- 数据目录默认在 `$XDG_DATA_HOME/deepsleep`（macOS 是 `~/Library/Application Support/deepsleep`）。
- Unix 上的 OTA 仍然挑本平台的 `.tar.gz`（`.deb` / `.rpm` / `.dmg` 只用于首次安装）。

发版由维护脚本一体化完成：改版本号 → 编译 → 打包（安装版 / 便携版 / 内核版 / deb / rpm）→ 生成 update.json →
推送 GitHub + Gitee Release、源码与官网（`release\tools\publish-via-api.py` / `publish-gitee.py`，需自备令牌）→
自动触发 macOS `.dmg` 构建。

---

## 七、说明

生成内容仅供参考，请自行判断使用场景并对自己言行负责；请勿用于违规或骚扰用途。
