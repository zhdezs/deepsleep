# -*- coding: utf-8 -*-
"""
连续对线引擎：把一批网上抓取的言论逐条输入系统，进行多轮连续对抗；
或进入交互式对话，一条一条地接招。

每一轮都会：
- 检测攻击类型与强度（detector）；
- 用当前会话状态（轮次/上一策略/强度走势）实时组装多行长回复（engine · live 模式，
  引用对方原话逐条拆解，字数多、多行、零脏话，不吐预制菜）；
- 双方向消息全部写入 SQLite（storage）；
对线结束后自动归档学习（analyzer.learn_from_session），沉淀有效策略。
"""

import datetime

from .analyzer import learn_from_session, format_duration, estimated_wasted_seconds
from .detector import CATEGORY_NAMES
from .strategies import STRATEGIES


class Duel:
    def __init__(self, storage, engine, tone="live", troll_name="网络键盘侠", use_llm=False):
        self.storage = storage
        self.engine = engine
        self.tone = tone if tone in ("default", "sharp", "live") else "live"
        self.troll_name = troll_name
        self.use_llm = use_llm

    @staticmethod
    def _tone_label(tone):
        return {
            "live": "实时组装·多行长文·零脏话",
            "sharp": "尖锐·零脏话",
            "default": "温和",
        }.get(tone, "实时组装")

    # ---------------------------------------------------------- 批量对线
    def run_batch(self, items):
        """把待处理言论队列逐条输入系统，生成尖锐回复并全部入库。"""
        sid = self.storage.create_session(self.troll_name)
        self.engine.new_session(sid)
        turns = []
        for it in items:
            text = (it.get("text") or "").strip()
            if not text:
                continue
            r = self.engine.generate(text, sid, tone=self.tone, use_llm=self.use_llm)
            self.storage.add_message(
                sid, "troll", r["troll_text"],
                category=r["category"], aggression=r["aggression"],
            )
            self.storage.add_message(sid, "ai", r["response"], strategy=r["strategy"])
            turns.append(r)
        self.storage.end_session(sid)
        win = learn_from_session(self.storage, sid)
        return sid, turns, win

    # ---------------------------------------------------------- 交互对话
    def chat(self, prompt_prefix="对方"):
        """交互式连续对话：手动输入对方言论，逐条接招。输入 q/quit/退出 结束。"""
        sid = self.storage.create_session(self.troll_name)
        self.engine.new_session(sid)
        print("=" * 60)
        print("连续对线模式已开启（%s）" % self._tone_label(self.tone))
        print("每行输入对方的一条言论，系统将实时生成多行长文连续接招；输入 q 退出并归档学习。")
        print("=" * 60)
        while True:
            try:
                line = input("\n%s > " % prompt_prefix).strip()
            except (EOFError, KeyboardInterrupt):
                print()
                break
            if not line:
                continue
            if line.lower() in ("q", "quit", "退出", "exit"):
                break
            r = self.engine.generate(line, sid, tone=self.tone, use_llm=self.use_llm)
            self.storage.add_message(
                sid, "troll", r["troll_text"],
                category=r["category"], aggression=r["aggression"],
            )
            self.storage.add_message(sid, "ai", r["response"], strategy=r["strategy"])
            print("[第%d轮 · %s · 攻击强度%d] %s" % (r["turns"], r["category_name"], r["aggression"], r["troll_text"]))
            print("[我方 · %s] %s" % (r["strategy_name"], r["response"]))
        self.storage.end_session(sid)
        win = learn_from_session(self.storage, sid)
        return sid, win

    # ---------------------------------------------------------- 战报
    def battle_report(self, sid):
        msgs = self.storage.session_messages(sid)
        trolls = [m for m in msgs if m["role"] == "troll"]
        ais = [m for m in msgs if m["role"] == "ai"]
        if not trolls:
            return "（本场无对话数据）"
        first_agg = trolls[0].get("aggression") or 0
        last_agg = trolls[-1].get("aggression") or 0
        by_cat = {}
        by_strat = {}
        for t in trolls:
            cat = t.get("category") or "general"
            by_cat[cat] = by_cat.get(cat, 0) + 1
        for a in ais:
            s = a.get("strategy")
            if s:
                by_strat[s] = by_strat.get(s, 0) + 1
        now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
        lines = []
        lines.append("=" * 60)
        lines.append("对线战报 · 会话 #%d · %s" % (sid, now))
        lines.append("-" * 60)
        lines.append("对方言论 %d 条，我方回复 %d 条" % (len(trolls), len(ais)))
        lines.append("攻击强度：首轮 %d → 末轮 %d（%s）" % (
            first_agg, last_agg,
            "对方明显降温" if last_agg < first_agg else "对方仍在坚持",
        ))
        lines.append("估算已消耗对方时间：%s" % format_duration(estimated_wasted_seconds(len(trolls))))
        lines.append("-" * 60)
        lines.append("攻击类型分布：%s" % "，".join(
            "%s×%d" % (CATEGORY_NAMES.get(k, k), v) for k, v in
            sorted(by_cat.items(), key=lambda x: -x[1])
        ))
        lines.append("破防策略分布：%s" % "，".join(
            "%s×%d" % (STRATEGIES.get(k, {}).get("name", k), v) for k, v in
            sorted(by_strat.items(), key=lambda x: -x[1])
        ))
        lines.append("=" * 60)
        return "\n".join(lines)
