deepsleep 安装程序 v3.1.0
========================================

就一个文件：deepsleep-install.cmd
Windows / Linux / macOS 都用它，装的时候选「桌面版」还是「内核版」。

  Windows     双击 deepsleep-install.cmd
  macOS       sh deepsleep-install.cmd      （只有内核版，见下）
  Linux       sh deepsleep-install.cmd      （桌面版 / 内核版都能装）

也可以带参数跳过菜单：
  deepsleep-install.cmd -core        （Windows 内核版）
  deepsleep-install.cmd -desktop     （Windows 桌面版）
  sh deepsleep-install.cmd --core    （Linux / macOS）

为什么一个文件能在两个系统上跑？
  它同时是一份批处理脚本和一份 shell 脚本：
    第 1、2 行在 Windows 眼里是「标签」，忽略掉；在 Unix 眼里是「空命令 + 丢弃式
    heredoc」，于是 Windows 那段被整段跳过。两边各走各的，互不干扰。
  Windows 那一半的逻辑（含中文提示）是 base64 编码后放在文件里的，运行时解出来
  交给 PowerShell 执行 —— 因为这文件必须保持纯 ASCII：只要里面出现一个非 ASCII
  字节，cmd.exe 的逐行解析就会错位，把正常命令拆成两半去执行。

内核版是什么？
  没有窗口。启动后它会打印一个本机地址和一个配对令牌，用浏览器打开那个地址
  就能用完整的 AI 助手（界面、工具、文件操作全都有）。适合服务器、SSH、容器。

为什么 macOS 没有桌面版？
  macOS 桌面版是 Photino + ad-hoc 签名，在 Apple 芯片上会被 Gatekeeper 判成
  「已损坏」直接打不开（签名只覆盖了 fat 包的一半）。所以 macOS 一律给内核版，
  它是纯命令行 + 浏览器界面，不碰 .app / 签名这套。安装时还会自动清掉
  quarantine 属性，否则系统会拦下下载来的二进制。

要不要联网？
  先试 GitHub；如果下载速度低于 500 KB/s 持续 3 秒，自动改从 Gitee 下载；
  Gitee 也不行就退到 GitHub 加速镜像（ghproxy 等），全都不行才报错。
  之所以按速度而不是按「失败」切换：GitHub 在国内经常连得上但只有几十 KB/s，
  傻等十分钟不如 3 秒判一次。

想离线装：把这个文件旁边的包放好即可（脚本优先用本地文件，完全不联网）：
    Windows   deepsleep-3.1.0-win-x64.zip / deepsleep-core-3.1.0-win-x64.zip
    macOS     deepsleep-core-3.1.0-osx-arm64.tar.gz（Apple 芯片）
              deepsleep-core-3.1.0-osx-x64.tar.gz（Intel）
    Linux     deepsleep-3.1.0-linux-x64.tar.gz 或 deepsleep-core-3.1.0-linux-x64.tar.gz
               （arm64 把 x64 换成 arm64）

装到哪 / 怎么卸载
  Windows 桌面版  解压到 %LOCALAPPDATA%\Programs\deepsleep（旁边有 Setup.exe 时交给它装）
  Windows 内核版  %LOCALAPPDATA%\Programs\deepsleep-core，删目录即卸载
  macOS / Linux    ~/.local/opt/deepsleep(-core)，启动器在 ~/.local/bin
                   卸载：rm -rf ~/.local/opt/deepsleep-core ~/.local/bin/deepsleep-core

不放心这个文件？
  纯文本，可以直接打开看结构：开头十来行是 Windows 入口，后面是 base64 数据
  和 Unix 入口。它只做四件事：认系统和 CPU → 问你装哪个 → 找/下载包 → 解压并建启动器。
