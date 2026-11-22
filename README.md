# deepsleep

Windows 桌面 AI 助手。数据放在本机，模型接口由你自己填，支持对话、工具调用、技能、长期记忆、多智能体协作和自动更新。安装包自带 .NET 运行时，不需要管理员权限。

当前版本 3.2.4。

## 主要能力

**超级连接**

桌面版工具栏点「超级连接」，或者在输入框里用 `/superlink`，会得到一个 6 位配对码。另一台设备打开 `https://deepsleep.liziserver.site/superlink/?type=配对码`，把它输进去就能连上。同一个局域网时直连，延迟最低；跨网络时自动开一条基于系统 ssh 的反向隧道，不需要额外下载组件，多个免费入口自动挑能用的，断了会重连。连上以后可以在浏览器里用内置远程桌面看画面，用鼠标、键盘或手机触屏操作。配对码 5 分钟有效，用一次就作废。

**远程访问**

内核版加 `--tunnel` 会自动建一条免注册的公网隧道，启动后打印一条带令牌的访问链接。想用现成的穿透工具，用 `--public --tunnel-cmd "frpc ..."`，frp、cpolar、ngrok 都可以，内核会从输出里找公网地址。

默认只监听 `127.0.0.1`。加 `--public` 或 `--host` 才对外监听，加 `--tunnel` 才挂公网。令牌输错会有递增延迟，最多 5 秒再返回 401。不带令牌的 `/api/ping` 只返回名字和版本。

**SuperAgent**

消息发出去以后先交给 Router 判断：简单的自己答，复杂的拆成几路交给 Worker 并行做，最后回灌给主助手汇总。Router 和 Worker 可以分别绑定不同的模型和供应商，Worker 名单可以在设置里增删。每个 Worker 有独立会话，被回收后再次使用时会把历史带回来。点开 Worker 能看到它收到的任务和产出，每条回答下面标着是哪个模型给的。卡住的会话由看门狗定时清理，判定条件是会话不在活跃列表里、并且超过设定时间没有更新。

**定时与触发**

可以按间隔、按每天固定时间、或者按文件变化触发一条任务，走的是同一条路由链路。助手正在忙时这次触发直接跳过，不会排队。

**工具**

共 21 个：运行命令、运行 Python 脚本、写入文件、读取文件、打开文件、网络搜索、深度研究、抓取网页、渲染图片、生成图片、生成视频、看图、Blender、GitHub、记住、删除记忆、安装扩展、制作扩展、操作电脑、拆分任务、查知识库。

`渲染图片` 调用本机 Chrome 或 Edge 的无头模式，把网页或一段 HTML 画成 PNG，落在 `data/render`。抓取网页时如果正文拿不到（SPA、图表、Canvas），它会顺手截一张图。

`Blender` 是连接器：首次启动时自动在 `Program Files/Blender Foundation`、`LocalAppData\Programs` 和 PATH 里找 Blender，找到后用 `blender --version` 验一次。没连上时这个工具不会出现在模型可用的工具列表里。执行方式是 `--background --factory-startup --python`，导出格式按扩展名决定，支持 .blend、.glb、.gltf、.obj、.stl、.fbx。

`GitHub` 同样是连接器，用设备码登录：生成 8 位验证码，浏览器确认一次，令牌就存下来了。令牌用 DPAPI 包一层密钥再用 AES-256-GCM 加密，存在 `data/connectors.json`，不写进配置文件，也不进日志。

**模型接口**

支持 OpenAI 兼容接口（DeepSeek、智谱、硅基流动、Groq、OpenRouter 等）、Anthropic Claude、Google Gemini、OpenAI Responses API。地址只填主机名会自动补全，协议也会按地址纠正。请求一律走流式。另外可以一键装 Ollama 跑本地模型。

**上下文与思考**

上下文用量超过 90% 时自动压缩，保留结论和未完成的事。上限可以在设置里指定，会写进配置。思考强度分四档：关、低、中、高。

**三种模式**

| 模式 | 行为 |
| --- | --- |
| chat | 只聊天，不给文件和命令类工具，其余工具都能用 |
| work | 文件和命令操作逐条确认，确认后 1 小时内同类操作不再问 |
| boom | 全自动，不打断 |

**其他**

- 技能库放在 `data/skills/`，可以从 SKILL.md 链接或本地文件夹安装
- 长期记忆跨会话保留，`记住` 只记用户画像和长期偏好，不堆对话摘要
- 任务中断、进程被杀、断电，重启后可以从上次的节点继续
- 支持多套皮肤，首次启动默认用 ArkUI 风格
- 模型图标按名字匹配，匹配不到的用 deepsleep 自己的图标

## 安装

双击 `deepsleep-Setup.exe`，选安装位置，默认 `%LOCALAPPDATA%\Programs\deepsleep`。装完会建快捷方式，也会登记到「设置 → 应用」，可以正常卸载。静默安装：

```powershell
deepsleep-Setup.exe --silent --dir "D:\Apps\deepsleep" --no-desktop --no-launch
```

程序不带任何预置 API Key。打开设置，填协议、地址、模型名和 Key 就能用。不想用在线接口，点「一键安装 Ollama + 大模型」换成离线模型。

数据都在安装目录的 `data/` 下：`config.json`（配置和 Key）、`conversations.json`（对话）、`memory.json`（记忆）、`runtime_state.json`（断点）、`cluster.json`、`skills/`、`images/`、`connectors.json`。升级安装不会覆盖这些文件。换电脑时把整个 `data/` 拷过去就行。

## 自动更新

左下角有「检查更新」，启动时也会自动查一次，可以关掉。

流程是：读更新源，下载安装包，用 SHA256 校验，退出程序，静默覆盖安装到原目录，然后重启。配置、对话、记忆都不受影响。

更新源支持四种写法：

- `owner/repo`，走 GitHub Releases
- `gitee.com/owner/repo`，国内源
- `https://.../update.json` 直链
- 本地或局域网路径，比如 `D:\release\update.json`

默认走 Gitee，线路可以在客户端里切换。Gitee 单个附件不能超过 100MB，大包会自动切片下载，支持断点续传，拼回整包后再校验。Gitee 连不上、或者 20 秒没有数据、或者速度长时间低于 60KB/s 时，回退到 GitHub 继续下。

## 目录结构

```text
src/Deepsleep.Core/      内核类库（.NET 10，不依赖界面）
  Kernel.cs              命令派发、事件推送、运行状态
  Kernel.Agent.cs        对话与流式输出
  Kernel.Router.cs       Router / Worker 调度
  Kernel.Split.cs        拆分并行执行
  Kernel.Trigger.cs      定时与文件触发
  Kernel.Watchdog.cs     卡死会话回收
  Kernel.Cluster.cs      多智能体协作
  Kernel.Conv.cs         会话、记忆、技能、设置
  Kernel.Ota.cs          更新与 Ollama 探测
  Agent.cs               智能体循环与工具实现
  Connectors.cs          连接器注册
  GitHubLogin.cs         设备码登录
  BlenderConnector.cs    Blender 探测与执行
  PageRenderer.cs        无头浏览器渲染
  Updater.cs             更新下载、分片、镜像回退

src/                     Windows 外壳（WPF + WebView2）
  MainWindow.xaml.cs     窗口、标题栏、WebView2 宿主
  DesktopPet.cs          桌宠
  PetChatWindow.cs       桌宠聊天窗

src/DeepSleepDesktop/    跨平台外壳（Linux / macOS，Photino）
src/DeepSleepCore/       内核版宿主，自带 HTTP 和 SSE 服务

ui/                      界面（HTML / CSS / JS）
  index.html             结构与启动脚本
  style.css              样式
  app.js                 渲染、交互、与内核通信

web/                     官网和网页版
  index.html             首页
  app/index.html         免安装网页版
  core/index.html        内核版网页端
  help/index.html        使用说明

installer/DeepSleepSetup/  图形安装程序
release/                   发布脚本与产物
```

## 安全说明

数据只在本机 `data/` 下，除了你自己配置的模型接口，不会往外发。

API Key 存在 `data/config.json`，安装包里的默认配置是空的。

GitHub 令牌用 DPAPI 加 AES-256-GCM 加密，附加认证用机器 GUID、用户 SID 和机器名，文件 ACL 只给当前账号。

命令和脚本执行有确认机制，work 模式逐条确认，boom 模式全自动。

## 从源码构建

Windows 桌面版：

```powershell
cd src
dotnet publish -c Release -r win-x64 --self-contained true -o dist\deepsleep-win-x64
```

安装包：把 dist（不含 data）覆盖到 `release\deepsleep-setup\package`，重打 `installer\payload.zip`，再编译安装程序。

```powershell
cd installer\DeepSleepSetup
dotnet publish -c Release -r win-x64 --self-contained true
```

内核版：

```powershell
dotnet publish src\DeepSleepCore\DeepSleepCore.csproj -c Release -f net10.0-windows10.0.19041.0 -o src\DeepSleepCore\dist\core
```

Linux / macOS 用的是 `src/DeepSleepDesktop`，窗口后端换成 Photino，界面和内核逻辑与 Windows 版共用一份代码。

```bash
dotnet publish src/DeepSleepDesktop/DeepSleepDesktop.csproj -c Release -r linux-x64 --self-contained true -o dist/desktop-linux-x64

./dist/desktop-linux-x64/deepsleep.sh

./dist/desktop-linux-x64/deepsleep.sh --headless
```

Linux 需要系统自带 WebKitGTK。Debian / Ubuntu 装 `libwebkit2gtk-4.1-0 libgtk-3-0`，Fedora / RHEL 装 `webkit2gtk4.1`，.deb 包里已经写了依赖。

macOS 的 .dmg 要求 14 以上。从网上下载后如果被 Gatekeeper 拦下：

```bash
APP=/Applications/deepsleep.app
sudo xattr -cr "$APP"
sudo codesign --force --sign - "$APP/Contents/MacOS/Photino.Native.dylib"
sudo codesign --force --sign - "$APP"
open "$APP"
```

## 说明

deepsleep 生成的内容仅供参考，用之前自己判断一下。请勿用于违规用途。
