# deepsleep · 鸿蒙电脑版（HarmonyOS PC）

一个 **ArkWeb 壳**：原生窗口 + 原生图标/任务栏，内容区直接加载 deepsleep 内核的网页界面。

## 为什么是"壳"而不是原生重写

鸿蒙 PC 跑不了 .NET —— 两条路都堵死：

| 路线 | 卡在哪 |
|---|---|
| CoreCLR（JIT） | HarmonyOS 5.0.0(12) 起禁止应用映射**可执行匿名内存**，JIT 起不来 |
| NativeAOT | 鸿蒙 `app.seccomp.policy` 的白名单里**没有 `get_mempolicy`**，而文件开头写的是 `@returnValue TRAP` —— 非法 syscall 不是返回错误，是直接 SIGSYS 杀进程。.NET 运行时一初始化就死 |

所以内核必须跑在一个**真正的 Linux 虚拟机**里（那一侧是标准 Linux，.NET 的现成
`linux-arm64` 构建直接就能跑，JIT 也不受限）；鸿蒙这边只负责**显示**。

好在 deepsleep 的界面层本来就是纯标准 Web（`ui/` 是 HTML+CSS+JS，
`web/core/core-bridge.js` 只用 `fetch` + `EventSource` + `localStorage`），
而鸿蒙的 ArkWeb 是定制版 Chromium —— 所以这个壳非常薄，也很少需要维护。

```
鸿蒙 PC
├── deepsleep.hap（本工程）          原生窗口 + ArkWeb
│     └── 内容区 → Web 组件
└── Linux 虚拟机（官方「融合开发引擎」或自建 QEMU）
      └── deepsleep-core  监听 0.0.0.0:8756
            ↑ 靠虚拟机的端口转发，鸿蒙侧用 http://127.0.0.1:8756/ 访问
```

## 先决条件

1. **鸿蒙 PC 上有 Linux 环境**
   - 推荐：应用市场装官方 **「融合开发引擎」**（Converged Development Engine，
     基于 StratoVirt），它把虚拟机端口映射到鸿蒙本机，性能接近原生
   - 或者自建 QEMU（社区 `harmony-qemu` + Alpine），但第三方应用拿不到 HMV/KVM
     加速，只能 TCG 纯软件模拟，**会明显卡**，属于"能用但别指望流畅"
2. **内核在 VM 里跑起来了**
   ```
   tar xzf deepsleep-core-3.0.5-alpha-linux-arm64.tar.gz
   cd deepsleep-core-3.0.5-alpha-linux-arm64
   ./install.sh
   ~/.local/share/deepsleep/deepsleep-core.sh start
   ```
   它会打印配对令牌，记下来。

## 编译

### 装 DevEco：出可安装的签名包（主路径，推荐）

```bash
# 1) 用 DevEco Studio 打开本目录（File → Open → 选 harmony/）
# 2) 首次会提示同步依赖，让它跑完
# 3) 改一下配置（见下）
# 4) File → Project Structure → Signing Configs → 勾选 Automatically generate signature
#    （登录华为账号，DevEco 会自动生成调试证书）
# 5) Build → Build Hap(s)/APP(s) → Build Hap(s)
#    产物在 entry/build/default/outputs/default/，*-signed.hap 就是可安装的
# 6) 装到鸿蒙 PC：hdc install -r xxx-signed.hap，或者把 hap 拷过去用文件管理器装
```

### 不装 DevEco：CI 出验证包（可选，手动触发）

`.github/workflows/harmony-hap.yml` 用 OpenHarmony 公开 SDK（华为云镜像，无需华为账号）
+ hvigor 4.0.9（华为公开 npm 源）编译工程，产出**未签名 .hap**。
⚠ **实测限制**：SDK 完整包有 **4.25 GB**，GitHub 海外 runner 下载不稳定（run #1 就栽在这），
所以只保留手动触发（Actions → harmony-hap → Run workflow），跑一次可能要 10~30 分钟。
产物是「验证用」的：能证明代码、资源、配置全都能编过，但**装不进鸿蒙 PC** —— 可安装的
包要签名，走上面 DevEco 自动签名那条路。

### 命令行签名（不想装 DevEco 的场合）

华为官方给了完整的 headless 签名流程（开发者论坛《Creating Pipeline for HarmonyOS
Wearable Device Deployment》）：用 `hap-sign-tool.jar` 对 CI 产出的未签名包重签名，
需要四件套（.p12 密钥库 / .csr / .cer 证书 / .p7b 描述文件），证书在华为开发者
后台申请。一般用户走上面 DevEco 自动签名即可，不必折腾这条。

### 要改的地方

| 位置 | 改什么 |
|---|---|
| `AppScope/app.json5` → `bundleName` | 换成你自己的包名（上架要求唯一，格式 `com.你的域名.应用名`） |
| `build-profile.json5` → `signingConfigs` | 用 DevEco 的 **File → Project Structure → Signing Configs** 自动生成（需要华为开发者账号） |
| `entry/src/main/ets/pages/Index.ets` → `DEFAULT_URL` | 内核地址，默认 `http://127.0.0.1:8756`（融合开发引擎的映射端口就是它）。如果你的转发端口不同，改这里，或者进去后在设置面板里改 |

### 图标

`entry/src/main/resources/base/media/` 下需要一个图标。用
[deepsleep 官网](https://zhdezs.github.io/deepsleep/) 的 logo 导出 512×512 PNG，
命名 `app_icon.png`（以及 `startIcon.png` 作为启动页图标）即可。
DevEco 会提示缺哪个补哪个。

## 用起来

1. 先在 VM 里把内核起起来（见上），拿到令牌
2. 打开 deepsleep（这个 hap）
3. 壳会先探测 `内核地址/api/ping`：
   - **通了** → 直接进界面。第一次会让你填配对令牌（填完存在本机，之后不用再填）
   - **不通** → 会显示一张引导页，告诉你内核没跑起来、怎么在 VM 里起
4. 设置面板（右上角 ⚙）里可以改内核地址 —— 比如你不想用端口转发，
   直接填 VM 在局域网里的 IP（`http://192.168.x.x:8756`）也行

## 目录

```
harmony/
├── AppScope/
│   ├── app.json5                  应用级配置（包名、版本、cleartextTraffic）
│   └── resources/base/element/    应用名等
├── entry/
│   └── src/main/
│       ├── module.json5           模块配置（权限、设备类型 2in1、Ability）
│       ├── ets/
│       │   ├── entryability/EntryAbility.ets   窗口入口
│       │   └── pages/Index.ets                 壳本体：探测 + ArkWeb + 设置
│       └── resources/base/        字符串、颜色、页面清单、图标
├── build-profile.json5            构建配置（SDK 版本、签名）
├── oh-package.json5
└── hvigorfile.ts
```

## 常见问题

**Q：ArkWeb 里显示空白 / 加载失败？**
先确认 VM 里内核活着（`deepsleep-core.sh status`），再确认端口转发通不通：
在鸿蒙的终端里敲 `curl http://127.0.0.1:8756/api/ping`，应该回一段 JSON。

**Q：提示「缺少配对令牌」？**
令牌在 VM 里跑 `deepsleep-core.sh token` 就能看到。填一次就记住了。

**Q：远程桌面能用吗？**
截的是**虚拟机**的屏，不是鸿蒙本机的屏 —— 因为内核跑在 VM 里，看不到宿主机桌面。
要在鸿蒙本机上做远程桌面，得用鸿蒙自己的截屏/输入 API 另做一套（本工程不含）。

**Q：能不能不装虚拟机？**
目前不行。除非将来 .NET 官方支持鸿蒙，或者鸿蒙把 `get_mempolicy`
加进 seccomp 白名单（可以给鸿蒙提 issue）。
