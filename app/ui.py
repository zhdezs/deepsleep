# -*- coding: utf-8 -*-
"""桌面界面（手写 GUI）：tkinter 实现微信聊天框风格。

- 聊天气泡：我方绿色靠右、对方白色靠左，圆角气泡 + 头像；
- 三档 DeepSeek 开关：自动（省 token）/ 本地 / DeepSeek；
- 横竖屏切换：一键切换窗口横/竖比例，气泡宽度随窗口自适应；
- 破防策略选择、换一招、统计、知识库、剪贴板监控、归档学习。

无第三方依赖，纯 tkinter（Python 内置），可直接打包为 exe 独立运行。
"""

import datetime
import threading
import tkinter as tk
import tkinter.font as tkfont
from tkinter import ttk

from . import __app_name__, __version__
from .analyzer import report, learn_from_session, format_duration, estimated_wasted_seconds
from .strategies import STRATEGIES, STRATEGY_ORDER
from .detector import CATEGORY_NAMES

try:
    from ctypes import windll
    windll.shcore.SetProcessDpiAwareness(1)
except Exception:
    pass

# 配色（微信风格）
BG = "#ededed"
TOP_BG = "#f7f7f7"
INPUT_BG = "#f7f7f7"
BUBBLE_SELF = "#95ec69"
BUBBLE_OTHER = "#ffffff"
TEXT = "#1a1a1a"
MUTED = "#9a9a9a"
ACCENT = "#07c160"
ACCENT_DARK = "#06ad56"
AVATAR_SELF = "#07c160"
AVATAR_OTHER = "#b0b0b0"
LINE = "#e2e2e2"

FONT = ("Microsoft YaHei UI", 11)
FONT_MSG = ("Microsoft YaHei UI", 12)
FONT_SMALL = ("Microsoft YaHei UI", 9)
FONT_META = ("Microsoft YaHei UI", 9)
FONT_BOLD = ("Microsoft YaHei UI", 11, "bold")

AVATAR_R = 18          # 头像半径
PAD_X = 12             # 消息左右边距
PAD_Y = 10             # 消息上下间距
GAP = 8                # 头像与气泡间距
BUBBLE_PAD_X = 12      # 气泡内左右留白
BUBBLE_PAD_Y = 9       # 气泡内上下留白
BUBBLE_MAX_RATIO = 0.72  # 气泡最大宽度占画布比例


def _round_rect(canvas, x1, y1, x2, y2, r, **kwargs):
    """用平滑多边形近似圆角矩形（smooth=True 贝塞尔平滑）。"""
    pts = [x1 + r, y1, x2 - r, y1, x2, y1, x2, y1 + r,
           x2, y2 - r, x2, y2, x2 - r, y2, x1 + r, y2,
           x1, y2, x1, y2 - r, x1, y1 + r, x1, y1]
    return canvas.create_polygon(pts, smooth=True, **kwargs)


class TrollWranglerApp(tk.Tk):
    def __init__(self, storage, engine):
        super().__init__()
        self.storage = storage
        self.engine = engine
        self._generating = False
        self._last_troll = ""
        self._landscape = False
        self._last_width = 0
        self._messages = []      # {role, text, meta}；role: troll/ai/sys

        self.title(f"{__app_name__} · 键盘侠反制助手 v{__version__}")
        self.geometry("460x760")
        self.minsize(360, 560)
        self.configure(bg=BG)

        self.session_id = storage.create_session()
        self.engine.new_session(self.session_id)

        # 三档 DeepSeek 开关：auto / off / on
        self.llm_mode = tk.StringVar(value="auto")
        self.auto_copy = tk.BooleanVar(value=True)
        self.watch_clip = tk.BooleanVar(value=False)
        self._last_seen_clipboard = self._safe_clipboard()
        self._last_processed_clip = None

        self._build_ui()
        self.after(2000, self._watch_clipboard)
        self.protocol("WM_DELETE_WINDOW", self._on_close)

    # ------------------------------------------------------------------ UI
    def _build_ui(self):
        # 顶部栏
        top = tk.Frame(self, bg=TOP_BG, height=52)
        top.pack(side="top", fill="x")
        top.pack_propagate(False)

        title_col = tk.Frame(top, bg=TOP_BG)
        title_col.pack(side="left", padx=12, pady=6)
        tk.Label(title_col, text="网络键盘侠", bg=TOP_BG, fg=TEXT,
                 font=("Microsoft YaHei UI", 14, "bold")).pack(anchor="w")
        self.subtitle_var = tk.StringVar(value="在线")
        tk.Label(title_col, textvariable=self.subtitle_var, bg=TOP_BG, fg=MUTED,
                 font=FONT_SMALL).pack(anchor="w")

        # 右侧功能按钮
        actions = tk.Frame(top, bg=TOP_BG)
        actions.pack(side="right", padx=8)
        self._mode_btns = {}
        for key, label, tip in (
            ("auto", "自动", "强度高/威胁类/持续升级才用 DeepSeek，省 token"),
            ("off", "本地", "语言模型 + 实时组装，零开销"),
            ("on", "DeepSeek", "全程大模型增强"),
        ):
            b = tk.Button(actions, text=label, command=lambda k=key: self._set_mode(k),
                          relief="flat", font=("Microsoft YaHei UI", 9),
                          bg=TOP_BG, fg=MUTED, cursor="hand2", padx=8, pady=3,
                          activebackground=TOP_BG)
            b.pack(side="left", padx=1)
            self._mode_btns[key] = b
        self._icon(actions, "📊", self._open_stats, "对线统计").pack(side="left", padx=2)
        self._icon(actions, "📚", self._open_knowledge, "辩论知识库").pack(side="left", padx=2)
        self._icon(actions, "⚙", self._open_settings, "设置").pack(side="left", padx=2)
        self._icon(actions, "↻", self._toggle_orientation, "横竖屏切换").pack(side="left", padx=2)
        self._icon(actions, "✚", self._new_session, "新开一场").pack(side="left", padx=2)
        self._icon(actions, "✕", self._on_close, "退出").pack(side="left", padx=2)
        self._set_mode("auto")

        # 聊天区（Canvas 气泡）
        chat_frame = tk.Frame(self, bg=BG)
        chat_frame.pack(fill="both", expand=True)
        self.canvas = tk.Canvas(chat_frame, bg=BG, highlightthickness=0, bd=0)
        sb = ttk.Scrollbar(chat_frame, command=self.canvas.yview)
        self.canvas.configure(yscrollcommand=sb.set)
        sb.pack(side="right", fill="y")
        self.canvas.pack(side="left", fill="both", expand=True)
        self.canvas.bind("<Configure>", self._on_canvas_resize)
        self.canvas.bind_all("<MouseWheel>", self._on_wheel)

        # 底部输入区
        input_bar = tk.Frame(self, bg=INPUT_BG, padx=10, pady=8)
        input_bar.pack(side="bottom", fill="x")

        self.input_text = tk.Text(
            input_bar, height=2, wrap="word", font=FONT_MSG, bd=0,
            highlightthickness=0, bg="white", padx=10, pady=8,
            insertbackground=ACCENT, relief="flat",
        )
        self.input_text.pack(side="left", fill="x", expand=True)
        self.input_text.bind("<Return>", lambda e: (self._on_respond(), "break")[0])
        self.input_text.bind("<Shift-Return>", lambda e: None)

        self.btn_respond = tk.Button(
            input_bar, text="发送", command=self._on_respond,
            bg=ACCENT, fg="white", relief="flat", padx=18, pady=8,
            font=FONT_BOLD, activebackground=ACCENT_DARK, activeforeground="white",
            cursor="hand2", borderwidth=0,
        )
        self.btn_respond.pack(side="left", padx=(8, 0))

        # 底部细状态栏
        status = tk.Frame(self, bg=TOP_BG)
        status.pack(side="bottom", fill="x")
        self.status_var = tk.StringVar()
        tk.Label(status, textvariable=self.status_var, bg=TOP_BG, fg=MUTED,
                 font=("Microsoft YaHei UI", 8)).pack(side="left", padx=10, pady=2)
        tk.Label(status, text="仅用于回应攻击，请勿主动挑衅他人。",
                 bg=TOP_BG, fg=MUTED, font=("Microsoft YaHei UI", 8)).pack(side="right", padx=10)

        self._refresh_status()
        self._append_sys("新会话已创建。把对方的话粘贴到输入框，回车发送即可。")

    def _icon(self, parent, char, cmd, tip):
        return tk.Button(parent, text=char, command=cmd, relief="flat",
                         bg=TOP_BG, fg=TEXT, font=("Segoe UI Emoji", 13),
                         cursor="hand2", padx=4, pady=2, activebackground=TOP_BG,
                         takefocus=0)

    def _set_mode(self, key):
        self.llm_mode.set(key)
        for k, b in self._mode_btns.items():
            if k == key:
                b.config(bg=ACCENT, fg="white")
            else:
                b.config(bg=TOP_BG, fg=MUTED)
        tips = {"auto": "自动：强度高时才用 DeepSeek，省 token",
                "off": "本地：语言模型 + 实时组装，零开销",
                "on": "DeepSeek：全程大模型增强"}
        self.subtitle_var.set(tips[key])

    def _toggle_orientation(self):
        self._landscape = not self._landscape
        self.geometry("820x520" if self._landscape else "460x760")

    def _on_canvas_resize(self, event):
        if event.width != self._last_width:
            self._last_width = event.width
            self._redraw_chat()

    def _on_wheel(self, event):
        self.canvas.yview_scroll(int(-event.delta / 120), "units")

    # ------------------------------------------------------------ 气泡绘制
    def _wrap(self, text, font, max_w):
        """按像素宽度换行。"""
        lines = []
        for para in str(text).split("\n"):
            if not para:
                lines.append("")
                continue
            cur = ""
            for ch in para:
                if self._text_width(cur + ch, font) <= max_w:
                    cur += ch
                else:
                    lines.append(cur)
                    cur = ch
            lines.append(cur)
        return lines

    def _font(self, font):
        """按字体元组（兼容 2 元组/3 元组）缓存 tkinter 字体对象。"""
        key = (font[0], font[1], font[2] if len(font) > 2 else "normal")
        if not hasattr(self, "_fonts_cache"):
            self._fonts_cache = {}
        f = self._fonts_cache.get(key)
        if f is None:
            kw = dict(family=font[0], size=font[1])
            if len(font) > 2:
                kw["weight"] = font[2]
            f = tk.font.Font(**kw)
            self._fonts_cache[key] = f
        return f

    def _text_width(self, s, font):
        return self._font(font).measure(s)

    def _redraw_chat(self):
        self.canvas.delete("all")
        cw = max(self.canvas.winfo_width(), 320)
        y = PAD_Y
        max_bubble_w = int(cw * BUBBLE_MAX_RATIO)

        for msg in self._messages:
            role = msg["role"]
            if role == "sys":
                y += self._draw_sys(canvas=self.canvas, text=msg["text"], cw=cw, y=y)
                continue
            y += self._draw_bubble(
                cw=cw, y=y, text=msg["text"], meta=msg.get("meta", ""),
                is_self=(role == "ai"), max_w=max_bubble_w,
            )

        self.canvas.configure(scrollregion=(0, 0, cw, max(y + PAD_Y, self.canvas.winfo_height())))
        self.canvas.yview_moveto(1.0)

    def _draw_sys(self, canvas, text, cw, y):
        """居中灰色小字，返回占用高度。"""
        wrapped = self._wrap(text, FONT_SMALL, int(cw * 0.9))
        lh = 16
        for ln in wrapped:
            canvas.create_text(cw / 2, y + lh / 2, text=ln, fill="#b0b0b0",
                               font=FONT_SMALL, justify="center")
            y += lh
        return len(wrapped) * lh + 4

    def _draw_bubble(self, cw, y, text, meta, is_self, max_w):
        """绘制一条气泡消息，返回占用高度。"""
        canvas = self.canvas
        lh = 20
        text_max_w = max_w - BUBBLE_PAD_X * 2
        lines = self._wrap(text, FONT_MSG, text_max_w)
        bubble_w = max(self._text_width(l, FONT_MSG) for l in lines) + BUBBLE_PAD_X * 2
        bubble_h = len(lines) * lh + BUBBLE_PAD_Y * 2
        bubble_w = max(bubble_w, 40)

        avatar_x = cw - PAD_X - AVATAR_R if is_self else PAD_X + AVATAR_R
        if is_self:
            bx2 = avatar_x - AVATAR_R - GAP
            bx1 = bx2 - bubble_w
        else:
            bx1 = avatar_x + AVATAR_R + GAP
            bx2 = bx1 + bubble_w

        by1, by2 = y, y + bubble_h

        # 头像（圆形 + 字）
        canvas.create_oval(avatar_x - AVATAR_R, y - AVATAR_R,
                           avatar_x + AVATAR_R, y + AVATAR_R,
                           fill=AVATAR_SELF if is_self else AVATAR_OTHER, outline="")
        canvas.create_text(avatar_x, y, text="理" if is_self else "侠",
                           fill="white", font=("Microsoft YaHei UI", 13, "bold"))

        # 气泡
        fill = BUBBLE_SELF if is_self else BUBBLE_OTHER
        _round_rect(canvas, bx1, by1, bx2, by2, 8, fill=fill, outline="")

        ty = by1 + BUBBLE_PAD_Y
        for ln in lines:
            tx = (bx2 if is_self else bx1) + (-BUBBLE_PAD_X if is_self else BUBBLE_PAD_X)
            canvas.create_text(tx, ty, text=ln, fill=TEXT, font=FONT_MSG,
                               anchor="ne" if is_self else "nw")
            ty += lh

        # 元信息（来源 / 策略 / 攻击类型）
        if meta:
            mw = self._text_width(meta, FONT_META)
            if is_self:
                canvas.create_text(bx2, by2 + 14, text=meta, fill=MUTED,
                                   font=FONT_META, anchor="ne")
            else:
                canvas.create_text(bx1, by2 + 14, text=meta, fill=MUTED,
                                   font=FONT_META, anchor="nw")

        return bubble_h + 24

    # ------------------------------------------------------------ 交互
    def _on_respond(self):
        text = self.input_text.get("1.0", "end").strip()
        if not text or self._generating:
            return "break"
        self._generating = True
        self.btn_respond.config(state="disabled", text="…")
        self.input_text.config(state="disabled")
        self.status_var.set("正在思考（DeepSeek 最长约 60s）…")
        manual = self._strat_keys.get(self.strategy_var.get(), "auto")
        mode = self.llm_mode.get()
        use_llm = {"on": True, "off": False}.get(mode, "auto")
        threading.Thread(
            target=self._generate_worker,
            args=(text, manual, use_llm),
            daemon=True,
        ).start()
        return "break"

    def _generate_worker(self, text, manual, use_llm):
        try:
            result = self.engine.generate(text, self.session_id, manual=manual, use_llm=use_llm)
        except Exception as e:
            result, err = None, str(e)
        else:
            err = None
        self.after(0, lambda: self._on_generation_done(result, err))

    def _on_generation_done(self, result, err):
        self._generating = False
        self.btn_respond.config(state="normal", text="发送")
        self.input_text.config(state="normal")
        if result is None:
            self._append_sys("生成失败：" + (err or "未知错误"))
            self._refresh_status()
            return
        self._last_troll = result["troll_text"]
        self.storage.add_message(self.session_id, "troll", result["troll_text"],
                                 category=result["category"], aggression=result["aggression"])
        self.storage.add_message(self.session_id, "ai", result["response"],
                                 strategy=result["strategy"])

        src = {"deepseek": "DeepSeek", "local-lm": "本地语言模型"}.get(
            result.get("source"), "实时组装")
        self._messages.append({"role": "troll", "text": result["troll_text"],
                               "meta": f"{result['category_name']} · 强度{result['aggression']}"})
        self._messages.append({"role": "ai", "text": result["response"],
                               "meta": f"{result['strategy_name']} · {src}"})
        self._redraw_chat()

        self.input_text.delete("1.0", "end")
        if self.auto_copy.get():
            self._copy(result["response"])
        self._refresh_status()

    def _alternatives(self):
        if not self._last_troll:
            return
        manual = self._strat_keys.get(self.strategy_var.get(), "auto")
        alts = self.engine.alternatives(self._last_troll, n=3, manual=manual)
        top = tk.Toplevel(self)
        top.title("备选回复（不写入记录）")
        top.geometry("560x400")
        top.configure(bg="white")
        tk.Label(top, text="以下三条备选回复任选其一：", bg="white",
                 font=FONT_BOLD).pack(anchor="w", padx=12, pady=(12, 6))
        for item in alts:
            frame = tk.Frame(top, bg="#e8f1fd", padx=8, pady=6)
            frame.pack(fill="x", padx=12, pady=4)
            t = tk.Text(frame, wrap="word", height=2, bg="#e8f1fd", fg="#1c3f7a",
                        font=FONT, bd=0)
            t.insert("1.0", item["response"])
            t.configure(state="disabled")
            t.pack(fill="x")
            tk.Button(frame, text="复制此条", command=lambda s=item["response"]: self._copy(s),
                      bg=ACCENT, fg="white", relief="flat", font=FONT_SMALL,
                      activebackground=ACCENT_DARK, cursor="hand2").pack(anchor="e")

    def _new_session(self):
        if self.storage.session_messages(self.session_id):
            win = learn_from_session(self.storage, self.session_id)
            self.storage.end_session(self.session_id)
        else:
            win = None
        self.session_id = self.storage.create_session()
        self.engine.new_session(self.session_id)
        self._messages = []
        self._redraw_chat()
        self._append_sys("新会话开始。" if win is None else
                         f"上一场已归档（{'策略有效' if win else '待优化'}）。")
        self._refresh_status()

    def _open_stats(self):
        top = tk.Toplevel(self)
        top.title("对线统计")
        top.geometry("520x480")
        top.configure(bg="white")
        t = tk.Text(top, wrap="word", bg="white", font=FONT, bd=0, padx=14, pady=10)
        t.pack(fill="both", expand=True)
        t.insert("1.0", report(self.storage))
        t.configure(state="disabled")

    def _open_knowledge(self):
        top = tk.Toplevel(self)
        top.title("辩论知识库")
        top.geometry("560x420")
        top.configure(bg="white")
        cols = ("category", "strategy", "uses", "wins", "rate")
        tree = ttk.Treeview(top, columns=cols, show="headings", height=16)
        heads = {"category": "攻击类型", "strategy": "策略", "uses": "使用", "wins": "有效", "rate": "有效率"}
        widths = {"category": 110, "strategy": 100, "uses": 70, "wins": 70, "rate": 90}
        for c in cols:
            tree.heading(c, text=heads[c])
            tree.column(c, width=widths[c], anchor="center")
        tree.pack(fill="both", expand=True, padx=10, pady=10)
        for row in self.storage.knowledge():
            rate = f"{row['rate'] * 100:.0f}%" if row["uses"] else "—"
            tree.insert("", "end", values=(
                CATEGORY_NAMES.get(row["category"], row["category"]),
                STRATEGIES.get(row["strategy"], {}).get("name", row["strategy"]),
                row["uses"], row["wins"], rate))

    def _open_settings(self):
        top = tk.Toplevel(self)
        top.title("设置")
        top.geometry("460x360")
        top.configure(bg="white")

        # 破防策略
        f1 = tk.Frame(top, bg="white")
        f1.pack(fill="x", padx=14, pady=(14, 4))
        tk.Label(f1, text="破防策略：", bg="white", font=FONT).pack(side="left")
        self.strategy_var = tk.StringVar(value="自动（智能选择）")
        names = ["自动（智能选择）"] + [STRATEGIES[k]["name"] for k in STRATEGY_ORDER]
        self._strat_keys = {n: ("auto" if n.startswith("自动") else
                                next(k for k in STRATEGY_ORDER if STRATEGIES[k]["name"] == n))
                            for n in names}
        cb = ttk.Combobox(f1, textvariable=self.strategy_var, values=names,
                          state="readonly", width=18, font=FONT)
        cb.pack(side="left", padx=6)

        tk.Checkbutton(top, text="回复自动复制到剪贴板", variable=self.auto_copy,
                       bg="white", font=FONT, activebackground="white").pack(anchor="w", padx=14, pady=6)
        tk.Checkbutton(top, text="剪贴板监控（检测到新内容自动回怼）", variable=self.watch_clip,
                       bg="white", font=FONT, activebackground="white").pack(anchor="w", padx=14)

        from . import llm
        ok = llm.is_configured()
        tip = ("DeepSeek 已配置，三档开关可用。" if ok else
               "未配置 DeepSeek Key：将始终走本地模型（请编辑 data/config.json）。")
        tk.Label(top, text=tip, bg="white", fg=MUTED, font=FONT_SMALL,
                 wraplength=420, justify="left").pack(anchor="w", padx=14, pady=10)

        # 换一招按钮入口
        tk.Button(top, text="对上一句换一招", command=self._alternatives,
                  bg=ACCENT, fg="white", relief="flat", font=FONT,
                  activebackground=ACCENT_DARK, cursor="hand2", padx=12, pady=6,
                  ).pack(anchor="w", padx=14, pady=6)

        tk.Button(top, text="关闭", command=top.destroy, relief="flat", bg="#f0f0f0",
                  fg="#333", font=FONT, padx=16, pady=6, cursor="hand2",
                  ).pack(side="bottom", pady=12)

    # ------------------------------------------------------------ 工具
    def _append_sys(self, text):
        self._messages.append({"role": "sys", "text": text})
        self._redraw_chat()

    def _safe_clipboard(self):
        try:
            return self.clipboard_get().strip()
        except Exception:
            return ""

    def _copy(self, text):
        try:
            self.clipboard_clear()
            self.clipboard_append(text)
            self._last_seen_clipboard = text
        except Exception:
            pass

    def _watch_clipboard(self):
        try:
            txt = self.clipboard_get().strip()
        except Exception:
            txt = ""
        if (self.watch_clip.get() and txt and txt != self._last_seen_clipboard
                and txt != self._last_processed_clip and len(txt) <= 2000):
            self._last_processed_clip = txt
            self.input_text.delete("1.0", "end")
            self.input_text.insert("1.0", txt)
            self._on_respond()
        if txt:
            self._last_seen_clipboard = txt
        self.after(2000, self._watch_clipboard)

    def _refresh_status(self):
        msgs = self.storage.session_messages(self.session_id)
        troll_n = sum(1 for m in msgs if m["role"] == "troll")
        wasted = format_duration(estimated_wasted_seconds(troll_n))
        try:
            from .llm import is_configured
            llm_ok = is_configured()
        except Exception:
            llm_ok = False
        mode = self.llm_mode.get()
        llm_state = {"auto": "自动", "off": "本地", "on": "DeepSeek"}.get(mode, "自动")
        if mode == "on" and not llm_ok:
            llm_state = "DeepSeek(未配置Key)"
        self.status_var.set(
            f"会话 #{self.session_id} · 轮次 {troll_n} · 已消耗对方 {wasted} · "
            f"DeepSeek：{llm_state}"
        )

    def _on_close(self):
        try:
            if self.storage.session_messages(self.session_id):
                learn_from_session(self.storage, self.session_id)
                self.storage.end_session(self.session_id)
        finally:
            self.destroy()
