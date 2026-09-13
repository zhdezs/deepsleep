# deepsleep 项目约定

## 铁律：每次做新版本都要推到 GitHub

发布仓库：`zhdezs/deepsleep`（公开）。客户端 OTA 的「更新源」就是这个仓库，
所以**任何一次改版本号 / 重新构建 / 修 bug 后重新打包，都算新版本，收尾必须推上去**，
不允许只改本地不推送。完整流程：

1. 版本号四处同步：
   - `src\TrollWrangler.csproj` 的 `<Version>` 与 `<FileVersion>`
   - `installer\DeepSleepSetup\DeepSleepSetup.csproj` 的 `<Version>`
   - `installer\DeepSleepSetup\MainWindow.xaml.cs` 的 `AppVersion`
   - `installer\DeepSleepSetup\MainWindow.xaml` 的副标题
2. 发布主程序：`src` 下
   `dotnet publish -c Release -r win-x64 --self-contained true -o dist\deepsleep-win-x64`
3. 更新载荷：把 dist（**排除 data**，别把你的 Key 和聊天记录打进安装包）覆盖到
   `release\deepsleep-setup\package`，再重打 `installer\payload.zip` 和
   `release\deepsleep-<版本>-win-x64.zip`。
   **用 `robocopy <dist> <package> /MIR /XD data` 同步，不要只做"覆盖"**：覆盖会把上一版
   已剔除的文件（onnxruntime/DirectML 等）留在包里 —— v1.0.7 的安装包就是这么胖了 24MB，
   v1.0.8 起改用 /MIR 清干净。
   **必须用 .NET 的 ZipFile**，不要用 `tar -a`（中文文件名会缺 UTF-8 标记而乱码）。
   `使用说明.txt`、`uninstall.cmd`、`uninstall.ps1` 在 `release\deepsleep-setup\`（上一层），
   /MIR 之后要再拷回 `package\`。
4. 编译安装程序：`installer\DeepSleepSetup` 下
   `dotnet publish -c Release -r win-x64 --self-contained true -o publish`，
   把 `deepsleep-Setup.exe` 与 `.pdb` 复制到 `release\`。
5. 生成清单：`release\make-update.ps1 -Version <版本> -Notes "<更新内容>"`
6. **推到 GitHub（必做）**：给 `zhdezs/deepsleep` 建 `v<版本>` 的 Release，
   上传 `deepsleep-Setup.exe`（有便携版 zip 也一起传）。
   **一条命令搞定（Release + 源码，推荐）**：
   ```
   powershell -ExecutionPolicy Bypass -File .secrets\publish.ps1
   powershell -ExecutionPolicy Bypass -File .secrets\publish.ps1 -Notes "本次更新内容"
   powershell -ExecutionPolicy Bypass -File .secrets\publish.ps1 -Only release    # 只发 Release
   ```
   它做的事：解出 `.secrets` 里的令牌（不落明文/不打印）→ 建或复用 `v<版本>` Release →
   流式上传 `release\deepsleep-Setup.exe` 与便携包（大文件跨境很慢，>120s 的整包 POST 会超时，
   必须流式）→ 用 Git Data API 同步源码（整棵树替换，废弃文件会被删掉）。
   也可直接调底层工具（自备令牌）：`python release\tools\publish-via-api.py --all --token-env DS_GH_TOKEN`
   —— **沙箱里 git/curl 的 schannel TLS 不可用（SEC_E_NO_CREDENTIALS），发布一律走 Python 的 HTTPS**。
   ⚠ 软件本体**不包含任何发布功能**（不要做成软件里的按钮），发布一律由 AI 助手在命令行完成。
   最后把两份 `config.json` 的 `UpdateUrl` 写成 `zhdezs/deepsleep`、`AutoCheckUpdate = true`。
7. 收尾必须报告：Release 链接 + 用 GitHub 返回的 `digest` 核对上传文件的 SHA256 是否与本地一致。

## 发布令牌（AI 用，放在 `.secrets\`，不进仓库）

| 文件 | 作用 |
| --- | --- |
| `.secrets\github-publish.dpapi` | 推版本用的 GitHub 令牌，**DPAPI LocalMachine** 加密（沙箱账号没加载用户配置文件，CurrentUser 的 DPAPI 用不了） |
| `.secrets\get-token.ps1` | 解出令牌到标准输出（别在终端直接跑，免得留痕） |
| `.secrets\set-token.ps1` | 换令牌：`-Token "..."` 或交互输入 |
| `.secrets\publish.ps1` | 一条命令发布（Release + 源码） |

`.secrets\` 在仓库范围外（源码同步只走 `src` / `installer` / `release` + 三份根文件），
**永远不要把它加进同步清单**。客户端「检查更新」用的细粒度令牌是另一回事，用 ⚙ 设置 → 🔑 录入，
由软件自己多层加密存 `data\github.token`，AI 不需要、也解不开。

## 源码也要推（和 Release 一起）
仓库 `zhdezs/deepsleep` 同时存放**源码和 Release**：每次发版除了推 Release，还要把当前源码同步过去
（src / installer / release 脚本与文档 / AGENTS.md；**排除** dist、bin、obj、*.zip/*.exe、installer\payload.zip、
release\deepsleep-setup\package、任何 data 目录与密钥文件）。源码提交与 Release 推送由 AI 助手完成，不要做成软件功能。

## 仓库范围：只放 winui 版
GitHub 仓库 `zhdezs/deepsleep` **只放 winui 版源码**：`src/`（WinUI 3 客户端，原 winui）、`installer/`（WPF 安装程序）、`release/`（发布脚本与使用说明），
以及根目录的 `AGENTS.md` / `README.md` / `.gitignore`。
**不要**再推旧版 python（app/、main.py、trainer.py 等）、cpp/、trainer/ 那套（`以理服人` 时期的东西）。

## 令牌与安全

- 令牌多层加密存放在 `<安装目录>\data\github.token`
  （DPAPI 封装密钥 + AES-256-GCM + 机器 GUID/账号 SID 绑定 + 文件 ACL 只留当前账号）。
- 令牌**绝不**写进 `config.json`、不打印、不提交到任何仓库、不放进安装包。
- 只读 fine-grained 令牌暴露面已核实为 0 个私有仓库，可保留不必吊销。
- 安装包里自带的 `data\config.json` 必须保持 API Key 为空（每台机器各自配置）。

## 构建/环境注意

- 构建前先退出正在运行的 `deepsleep.exe`，否则文件被锁。
- 报 `CS2012 拒绝访问` 时先 `Stop-Process VBCSCompiler` 再重试。
- 沙箱里 .NET / curl 的 TLS 可能被拦（报「安全包中没有可用的凭证」），
  但 **Python 的 HTTPS 可用**，联网发布/校验走 Python；`tools\` 下的 PowerShell 脚本
  在你自己的桌面会话里可正常联网。
- 写含中文的 `.ps1` 一律 UTF-8 **带 BOM**，否则 PowerShell 5.1 会乱码。

## 禁区

- AI 气泡里不允许出现工具调用 JSON；工具调用走「软件发决策请求 → Agent 单独输出
  tool_call → 软件执行 → 结果回灌 → 单独生成最终回复」这条链。
- 不允许静默降级：API 配置错误或连不上时，直接把地址、模型名、失败原因显示给用户，
  不要偷偷换成本地小模型装傻。
## 已废弃（不要实现、不要恢复、不要当有效上下文）
- **「以理服人」回怼功能及其一切**（标签页、工具列表里的「以理服人」、回怼语气话术）—— 程序是纯 AI 助手，自训练模型仅留作以后用
- **旧版 Python 实现**：`app/`、`main.py`、`launcher.py`、`train_model.py`、`fetch_rants.py`、`generate_rants.py`、`requirements.txt`、`troll_wrangler.spec`
- **旧版 C++ 实现与 RL 训练器**：`cpp/`、`trainer/`
  → 以上都已移到 `archive\abandoned\`，**只作存档**，不进仓库、不参与构建
- **毛玻璃桌宠方案**（`PetWindow.xaml`，亚克力）—— 已改为 Win32 分层窗口的真透明桌宠
- **「破军」这个名字**（早已改名为 deepsleep）
- **用命令行 curl/PowerShell 代替「联网搜索」工具**的做法
- **软件内置发布功能**（曾实现过 `PublishReleaseAsync`，已删除）—— 发布一律由 AI 在命令行做
- **框架依赖瘦身路线**（要求目标机预装 .NET/WindowsAppRuntime，风险高，已放弃）—— 改用「剔除 onnxruntime/DirectML」的零风险方案（−43 MB，已实测）
- 更新清单指向本地路径/自建服务器的旧分发方式 —— 现以 GitHub Releases 为准

## 当前有效范围（唯一真相）
| 项 | 位置 |
| --- | --- |
| 客户端源码 | `src/`（WinUI 3 / .NET 10） |
| 安装程序 | `installer/DeepSleepSetup/`（WPF 单文件，内嵌 payload.zip） |
| 发布脚本与说明 | `release/`（`make-update.ps1`、`tools/`、`使用说明.txt`） |
| 发布仓库 | GitHub `zhdezs/deepsleep`（源码 + Releases），最新 **v1.0.8** |
| 辅助脚本 | `tools\publish-github-release.ps1` / `set-github-token.ps1` / `download-update.ps1` |
| 用户数据 | 安装目录 `data\`；令牌多层加密存 `data\github.token` |
