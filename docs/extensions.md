# deepsleep 扩展：`.dslt` 自定义工具 / `.dslp` 皮肤

两个格式都是 **zip**，根目录放一个清单文件。装过 deepsleep 的电脑**双击即可安装**
（程序会带着 `--install-ext "<文件路径>"` 启动，装完弹提示）。

- `.dslt` — 自定义**工具**：AI 可以直接调用的脚本，装完就出现在它的工具箱里
- `.dslp` — **皮肤**：改配色 / 圆角 / 背景，支持用 CSS 做自定义动效

安装位置：`data/tools/<id>/`、`data/skins/<id>/`，登记在 `data/extensions.json`。
设置 → 🧩 扩展 里可以查看、启用 / 停用、切换皮肤、删除。

---

## 一、`.dslt` 工具包

### 结构

```
manifest.json      # 必需
main.py            # 入口脚本（名字随你，manifest.entry 指过去）
其他文件…           # 可选：数据、依赖、说明
```

### `manifest.json`

```json
{
  "id": "sci-calc",              // 必需，唯一标识：字母/数字/-/_/中文
  "name": "科学计算器",           // 显示名，也是 AI 调用的工具名
  "version": "1.0.0",
  "author": "you",
  "description": "算数学表达式：四则、幂、开方、三角、对数",
  "entry": "main.py",            // 必需，相对包目录
  "runtime": "python"            // python | cmd | powershell | node
}
```

### 运行约定

- 在**包目录**里运行（相对路径可以直接用）
- 输入同时以 **命令行参数** 和 **环境变量 `DEEPSLEEP_TOOL_INPUT`** 传入
- **stdout 就是返回给 AI 的结果**；非 0 退出码视为失败
- 超时 5 分钟；`work` 模式运行前会请用户确认，`boom` 模式直接跑
- 还有 `DEEPSLEEP_TOOL_ID`、`DEEPSLEEP_DATA`（data 目录）可用

### 例子

`main.py`：

```python
import sys, os
expr = " ".join(sys.argv[1:]) or os.environ.get("DEEPSLEEP_TOOL_INPUT", "")
print(f"{expr} = {eval(expr)}")   # 真实示例见 packages/科学计算器
```

装好之后，你直接跟 AI 说「用科学计算器算 sin(pi/2)+2^10」，它就会调用这个工具。

---

## 二、`.dslp` 皮肤包

### 结构

```
theme.json     # 必需
theme.css      # 可选，自定义样式与动效
bg.jpg         # 可选，壁纸
```

### `theme.json`

```json
{
  "id": "deepseek-chan",
  "name": "DeepSeek 娘",
  "version": "1.0.0",
  "author": "you",
  "description": "深海蓝紫 + 呼吸动效",
  "base": "dark",                                  // light | dark：建议搭配哪个模式（不再强制切主题）
  "vars": {                                        // 覆盖界面 CSS 变量
    "--bg": "#070d1a",
    "--panel": "rgba(14,22,42,.82)",
    "--text": "#e6efff",
    "--bubble": "#132444",
    "--self": "#3d6dff",
    "--radius": "18px"
  },
  "css": "theme.css",                              // 可选
  "wallpaper": "bg.jpg"                            // 可选
}
```

#### 主题 CSS 与皮肤是两层，别混

**亮色 / 暗色模式各有一套固有 CSS**，随程序分发，不是扩展包：

| 模式 | 文件 | 内容 |
|---|---|---|
| 暗色 | `ui/theme-dark.css` | 漂移氛围光、星点闪烁、流光气泡、工具光条（源自 `dsh-style` v1.1.0） |
| 亮色 | `ui/theme-light.css` | 雾白画布、墨绿强调色、柔和玻璃高光（源自 `dsh-light` v1.0.0） |

两套都是**全局选择器**（`body::before`、`.row`、`.bubble`、`::-webkit-scrollbar`…），
不能同时生效。`index.html` 用两个 `<link>` 按当前主题切换 `media=all / not all`，
只启用一份，点右上角 ☀️/🌙 即时整套换掉，无需重载。

`.dslp` 皮肤是**叠加层**：注入到 `#skinStyle`，变量作用域为 `body.skin-on`
（与 `body.dark` 同为 0,1,1，但后插入所以能覆盖），CSS 里可以写 `@keyframes` 做动效。
规则：

- 皮肤不会改主题，也不会因为切主题被换掉；`base` 只是给用户的搭配建议；
- 关掉皮肤 → 露出当前模式自带的那一套 CSS；
- 旧版本曾把这两套当皮肤灌进 `data\skins\`，启动时 `PurgeBuiltins` 会清掉残留。

> 因此**不要再给 .dslp 写 `defaultFor` / `builtin` 字段**，也不要试图做「深/浅默认皮肤包」——
> 模式的默认样式在 `ui/theme-*.css`，改那里就行。

### 可覆盖的变量

`--bg` `--panel` `--side` `--text` `--muted` `--border` `--bubble` `--self` `--self-text`
`--input` `--hover` `--sel` `--card` `--code` `--shadow` `--chipbg` `--chipon` `--ring` `--radius`

### 动效

`theme.css` 会被原样注入页面，所以直接写 `@keyframes` 挂到现有类名上即可：

```css
@keyframes pop { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; } }
.row .bubble { animation: pop .22s ease-out both; }
.bubble.thinking { animation: breathe 1.5s ease-in-out infinite; }
```

常用类名：`.row` `.bubble` `.bubble.tool` `.bubble.thinking` `.avatar` `.row.self` `.composer`。

---

## 三、安装

**双击** `.dslt` / `.dslp` 即可（第一次运行 deepsleep 时会自动登记文件关联，HKCU，不需要管理员）。

也可以在 设置 → 🧩 扩展 里粘贴绝对路径安装，或者让 AI 代劳：

> 把这个包装一下：`D:\下载\科学计算器.dslt`

命令行 / 内核版：

```bash
deepsleep-core --install-ext "D:\下载\科学计算器.dslt"
```

---

## 四、让 deepsleep 自己造

直接说需求即可，两个内置工具会配合完成：**写入文件** 造清单和脚本 → **制作扩展** 打包 → **安装扩展** 装上。

示例：

> 给我做一个「中文分词统计」工具，输入一段话输出词频前 10；做完打成 .dslt 装好

> 做一个「赛博朋克」皮肤：暗紫底 + 霓虹绿高亮，气泡出现带一点抖动，装上并启用

> 把桌宠换成 DeepSeek 娘风格：深蓝渐变背景、头像呼吸光、思考气泡流光

---

## 五、安全

- 工具是**本次会话里就能跑的本地脚本**，等于给 AI 加了一个新能力。只装可信来源的包。
- `work` 模式每次运行都会弹确认，能看清脚本名和输入；不确定就先别切 `boom`。
- 皮肤只注入 CSS/变量，不执行脚本；但它能改界面外观，恶意皮肤可以伪装界面（比如伪造按钮），同样只装可信的。
- 卸载 = 设置里点删除，或手动删 `data/tools/<id>` / `data/skins/<id>`。
