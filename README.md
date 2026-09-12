# deepsleep · AI 全能助手

一个**本地优先**的 Windows AI 助手桌面应用：对话、工具调用、技能库、Agent 集群、
长期记忆一应俱全，支持在线大模型 API（OpenAI 兼容 / Claude / Gemini）与本机 Ollama 双路线，
自带**安装程序**与 **OTA 自动更新**。

- 当前版本：`1.0.0`
- 主程序：`deepsleep.exe`（WinUI 3，自带 .NET 运行时，免装依赖）
- 系统要求：Windows 10 1809+ / x64（推荐 Windows 11，可享亚克力毛玻璃界面）

---

## 一、界面与核心能力

主界面分**两个页面**：**AI 助手** 与 **Agent 集群**。

### AI 助手
| 能力 | 说明 |
| --- | --- |
| **多协议大模型接入** | OpenAI 兼容（DeepSeek / 智谱 / 硅基流动 / Groq / OpenRouter）、Anthropic Claude、Google Gemini，设置里自由切换 |
| **三种运行模式** | `chat` 聊天 / `work` 工作（执行命令需确认）/ `boom` 爆破（全自动） |
| **12 项工具调用** | 见下表 |
| **技能库（Skills）** | `data/skills/` 下即插即用的技能包（含 pptx / docx 文档处理等），可从本地文件夹或 SKILL.md 链接安装 |
| **长期记忆** | 跨会话记住你的偏好与事实（`data/memory.json`） |
| **联网搜索** | 输入框一键开关，AI 可实时查资料 |
| **文件上传** | 支持上传图片/文档/代码给 AI 分析 |
| **极速模式** | 短提示词 + 更小输出预算，响应更快 |
| **Prompt 模板库** | 翻译/总结/代码审查等快捷指令 |
| **断点恢复** | 任务被中断或关机后可从未完成处继续（`data/runtime_state.json`） |

### 工具清单（12 项）
`以理服人`（回怼生成）· `运行命令` · `运行Python脚本` · `生成图片` · `看图` ·
`联网搜索` · `抓取网页` · `打开文件` · `读取文件` · `写入文件` · `记住` · `删除记忆`

### Agent 集群
创建多个 Agent 分别承担子任务**并行执行**，由「总指挥」自动拆解任务、分配成员、汇总结果，
适合多步骤的复合型任务。

---

## 二、技术架构

```
winui/                     主应用源码（WinUI 3 / .NET 10）
  ├─ MainWindow.xaml(.cs)  主界面（AI 助手 / Agent 集群）
  ├─ Agent.cs              智能体主循环 + 12 项工具实现
  ├─ ApiClient.cs          在线 API（OpenAI 兼容 / Claude / Gemini）
  ├─ OllamaClient.cs       本机 Ollama 本地大模型
  ├─ ImageGenClient.cs     图片生成
  ├─ VisionClient.cs       视觉理解（看图）
  ├─ SkillStore.cs         技能库
  ├─ MemoryStore.cs        长期记忆
  ├─ ClusterStore.cs       Agent 集群
  ├─ RuntimeState.cs       断点恢复
  ├─ Sandbox.cs            命令/脚本沙箱
  ├─ Engine.cs             本地自训练回怼引擎
  ├─ NgramModel.cs         n-gram 语言模型
  ├─ NaiveBayes.cs         攻击文本分类器
  └─ Updater.cs            OTA 自动更新

installer/DeepSleepSetup/  安装器源码（WPF 单文件，内嵌 payload.zip）
  └─ 产物：deepsleep-Setup.exe

release/                   发布源（分发用）
  ├─ deepsleep-Setup.exe   安装包（OTA 下载的就是它）
  ├─ deepsleep-setup/      PowerShell 图形安装器 + package（应用本体）
  ├─ update.json           OTA 更新清单
  └─ make-update.ps1       生成更新清单脚本
```

---

## 三、安装与使用

### 安装
双击 `deepsleep-Setup.exe`，选择安装位置（默认 `%LOCALAPPDATA%\Programs\deepsleep`，**无需管理员权限**），
安装完成后自动创建桌面/开始菜单快捷方式，并登记到「设置 → 应用 → 已安装的应用」里可卸载。

静默安装（脚本/批量部署）：
```powershell
deepsleep-Setup.exe --silent --dir "D:\Apps\deepsleep" --no-desktop --no-launch
```

也支持 PowerShell 脚本安装（`release/deepsleep-setup/`）：双击「安装 deepsleep.cmd」走图形安装界面。

### 首次使用
程序**不含 API Key**，每台电脑需单独配置：打开 `⚙ 设置` → 填写 API 协议、地址、模型名、Key → 保存。

不想用在线 API？在设置里点「一键安装 Ollama + 大模型」，切到本地模型运行，不消耗额度。

### 数据位置
所有用户数据都在**安装目录的 `data/`** 内：

| 文件 | 内容 |
| --- | --- |
| `config.json` | 模型与 API 配置（含 Key） |
| `conversations.json` | 对话历史 |
| `memory.json` | 长期记忆 |
| `runtime_state.json` | 断点恢复状态 |
| `cluster.json` | Agent 集群配置 |
| `skills/` | 技能目录 |
| `model_lm.json` / `model_nb.json` | 本地自训练模型 |

升级安装**不会覆盖**这些文件；换电脑时把整个 `data/` 目录拷过去即可。

### 卸载
- 方式一：`设置 → 应用 → 已安装的应用 → deepsleep AI 助手 → 卸载`
- 方式二：双击安装目录里的「卸载 deepsleep.cmd」（可选择是否保留用户数据，默认保留）

---

## 四、OTA 自动更新

程序左下角有「⬆ 检查更新」按钮，启动时也会自动检查（可在设置里关闭）。

**升级流程全自动**：检查清单 → 下载新安装包（带 SHA256 校验）→ 退出程序 →
静默覆盖安装到原目录 → 自动重启。用户数据（API Key、对话、记忆、模型）不受影响。

### 发布方（开发者）每次发版流程

```powershell
# 1. 编译新版本主应用，覆盖 release/deepsleep-setup/package/
cd winui
dotnet publish -c Release -o ..\release\deepsleep-setup\package

# 2. 重新打包安装器内嵌的 payload.zip（把 package 内容压进去），
#    然后重新编译安装器，产出新的 deepsleep-Setup.exe
cd ..\installer\DeepSleepSetup
dotnet publish -c Release

# 3. 把新的 deepsleep-Setup.exe 放到 release/，生成更新清单
cd ..\..\release
powershell -ExecutionPolicy Bypass -File make-update.ps1 -Version 1.0.1 -Notes "本次更新内容"

# 4. 已上传服务器时改用下载地址
powershell -ExecutionPolicy Bypass -File make-update.ps1 -Version 1.0.1 -Notes "..." `
    -BaseUrl "https://你的地址/deepsleep/"
```

生成的 `update.json`：
```json
{
  "version": "1.0.1",
  "url": "https://你的地址/deepsleep/deepsleep-Setup.exe",
  "sha256": "安装包的 SHA256 校验值",
  "notes": "本次更新内容"
}
```

客户端在 `⚙ 设置 → 更新清单地址` 填入该 `update.json` 的 URL 即可。
更新源既支持 `https://` 网址，也支持本地磁盘与局域网共享路径（`\\服务器\共享\update.json`）。

---

## 五、可选：本地自训练模型

默认走在线 API / Ollama。若想**完全离线、零额度**使用，「🧠 自训练」开关可切到本机自训练模型
（零依赖、纯 CPU）。该模型由根目录的 Python 脚本训练，纯标准库、无第三方依赖。

### 训练攻击文本分类器
```bash
py -3 train_model.py                  # 用现有生成数据训练并保存
py -3 train_model.py --count 2000     # 按类别等量生成 2000 条再训练（避免样本不均衡）
py -3 train_model.py --test "你懂个屁"  # 测试单句
```
纯 Python 多项式朴素贝叶斯，产物存 `data/classifier.json`；
识别链路为「模型优先（置信度阈值）+ 关键词规则兜底」。实测 982 条均衡样本准确率 **98.6%**。

### 训练回怼语言模型
```bash
py -3 train_model.py --train-lm            # 从头训练并保存
py -3 train_model.py --train-lm --db data/app.db   # 纳入历史语料
```
BPE 学习词表 + 词序列 n-gram 条件概率（插值回退平滑），温度采样 + top-k 逐词生成；
模型仅几百 KB（`data/lang_model.json`），纯 CPU 毫秒级推理。

### 批量生成训练数据
```bash
py -3 generate_rants.py --count 1000   # 模板+槽位批量生成语料
py -3 generate_rants.py --seed-queue   # 导入待处理队列
```

### RL 自训练进化器（C#）
`trainer/` 是一个**强化学习式自训练进化器**，多轮训练让本地模型持续进化：

```bash
cd trainer
dotnet build -c Release
# 阶段A：用在线 API 批量生成高质量样本喂给本地模型（校准）
# 阶段B：本地模型自采样 → 大模型当评委打分 → 合格 Learn（奖励）/ 不合格 Penalize（惩罚）
& "bin\Release\net10.0\deepsleep-trainer.exe" --count 100 --rl 100 --min-score 7
```

产出 `model_lm.json` / `model_nb.json`，主应用启动时自动加载。

---

## 六、数据与隐私

- 所有数据仅保存在本机安装目录的 `data/` 内，**不会上传任何内容**；
- API Key 仅存本机 `data/config.json`，也可用环境变量提供（优先级更高）；
- 命令/脚本执行经过沙箱与确认机制（`work` 模式需逐条确认，`boom` 模式全自动）；
- 对话历史、记忆、统计全部可离线查看。

---

## 七、使用边界（请务必遵守）

- 本工具用于提升个人工作效率，请勿用于生成违规、侵权或骚扰他人的内容；
- 生成内容仅供参考，请自行判断使用场景，并对自己的言行负责；
- 涉及人身安全等真实威胁，请直接举报或报警，不要依赖话术应对。
