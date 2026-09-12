# -*- coding: utf-8 -*-
"""回复生成引擎：负责攻击分类、策略选择与零脏话回复生成。

支持三种语气：
- tone="default"：温和语料（佛系/逻辑/反问/捧杀/复读/关怀）
- tone="sharp"  ：尖锐语料（同样策略内核，语言更锋利，依旧零脏话）
- tone="live"   ：实时组装（拒绝预制菜：引用对方原话、按攻击类型/策略/轮次
                   动态拼装多段长回复，字数多、多行、零脏话）
"""

import random

from .strategies import STRATEGIES, STRATEGY_ORDER
from .sharp import SHARP_LINES, SHARP_BLOW, sharp_lines_for
from .detector import classify, aggression_score, mask_profanity, CATEGORY_NAMES
from .composer import compose as live_compose, live_alternatives


class ResponseEngine:
    """带会话状态（轮次、上一策略、攻击强度走势）的回复引擎。"""

    def __init__(self):
        self._state = {}
        self._recent = {}

    def new_session(self, sid):
        self._state[sid] = {
            "turns": 0,
            "last_strategy": None,
            "last_aggression": 0,
        }
        self._recent[sid] = []

    def _pick_line(self, strategy, turns, rising, tone, sid):
        """采样回复，避免与同会话最近几条重复（保证连续对话不车轱辘）。"""
        recent = self._recent.setdefault(sid, [])
        for _ in range(8):
            line = self._sample_line(strategy, turns, rising, tone)
            if line not in recent:
                break
        recent.append(line)
        self._recent[sid] = recent[-4:]
        return line

    def _choose(self, category, agg, sid=None, manual=None):
        """选择策略：优先手动指定，否则按攻击类型与强度智能挑选，
        对方升级时自动换招，避免被摸清套路。"""
        st = self._state.get(sid, {})
        turns = st.get("turns", 0)
        last = st.get("last_strategy")
        last_agg = st.get("last_aggression", 0)
        rising = agg > last_agg

        if manual and manual in STRATEGIES:
            pool = [manual]
        else:
            if agg >= 7 and turns == 0:
                pool = ["zen", "care"]
            elif category == "ad_hominem":
                pool = ["praise", "zen"]
            elif category == "threat":
                pool = ["care", "zen"]
            elif category == "doubt":
                pool = ["logic", "question"]
            elif category == "label":
                pool = ["praise", "zen"]
            elif category == "provocation":
                pool = ["question", "zen", "echo"]
            elif category == "general":
                pool = ["question", "logic", "zen"]
            else:
                pool = list(STRATEGY_ORDER)

            if rising and last and last in pool and len(pool) > 1:
                pool = [s for s in pool if s != last]  # 对方升级 → 换招

        return random.choice(pool)

    def _sample_line(self, strategy, turns, rising, tone="default"):
        if tone == "sharp":
            data = sharp_lines_for(strategy)
            if turns == 0:
                return random.choice(data["first_lines"])
            if rising:
                return random.choice(data["escalate_lines"])
            return random.choice(data["follow_ups"])
        data = STRATEGIES[strategy]
        if turns == 0:
            return random.choice(data["first_lines"])
        if rising:
            return random.choice(data["escalate_lines"])
        return random.choice(data["follow_ups"])

    def _strategy_name(self, strategy, tone, source="local"):
        if tone == "sharp":
            return SHARP_LINES.get(strategy, {}).get("name", STRATEGIES[strategy]["name"])
        if tone == "live":
            tag = {"local-lm": " · 语言模型生成", "deepseek": " · DeepSeek"}.get(
                source, " · 实时组装")
            return STRATEGIES[strategy]["name"] + tag
        return STRATEGIES[strategy]["name"]

    @staticmethod
    def _should_use_llm(use_llm, category, agg, rising, turns):
        """决定本轮是否动用 DeepSeek（省 token 的关键）。

        use_llm=True      → 强制开启；
        use_llm=False     → 强制关闭（走本地）；
        use_llm="auto"    → 智能判断，仅在「值得动用大模型」时开启：
            · 攻击强度 ≥7（对方来势汹汹，本地未必压得住）；
            · 威胁恫吓类（涉及安全，值得更稳的大模型应对）；
            · 对方持续升级且已到第 3 轮以后（需要换更狠的角度破防）。
        其余情况交给本地语言模型/实时组装，能省则省。
        """
        if use_llm is True:
            return True
        if use_llm is False or use_llm is None:
            return False
        # use_llm == "auto"
        if agg >= 7:
            return True
        if category == "threat":
            return True
        if rising and turns >= 2:
            return True
        return False

    def _llm_response(self, troll_text, category, strategy, agg, rising,
                      turns, tone, sid):
        """调用 DeepSeek 实时生成回怼；任何失败返回 None（由本地组装兜底）。

        输出前统一脱敏，若被脱敏后为空也视为失败回退本地。
        """
        try:
            from .llm import SYSTEM_PROMPT, chat, is_configured
            from .detector import CATEGORY_NAMES as _N
            if not is_configured():
                return None
            recent = [r for r in self._recent.get(sid, []) if isinstance(r, str)][-2:]
            parts = [
                "对方言论：" + troll_text,
                "攻击类型：%s" % _N.get(category, category),
                "攻击强度：%d/10（%s）" % (
                    agg, "对方明显升级" if rising else "对方仍在坚持"),
                "当前轮次：第 %d 轮" % (turns + 1),
                "建议策略：%s" % STRATEGIES.get(strategy, {}).get("name", strategy),
            ]
            if recent:
                parts.append("我方此前的回复（请换全新角度，严禁重复套路）：\n" +
                             "\n---\n".join(recent))
            prompt = "\n".join(parts) + "\n请生成一条让对方破防的回怼："
            out = chat(prompt, system=SYSTEM_PROMPT)
            if not out:
                return None
            out = mask_profanity(out).strip()
            return out or None
        except Exception:
            return None

    def generate(self, troll_text, sid, manual="auto", tone="live", use_llm=False):
        """输入对方攻击内容，返回脱敏后的回复与结构化信息。

        tone：default（温和语料）/ sharp（尖锐语料）/ live（实时组装，默认）
        use_llm：接入 DeepSeek 时优先调用大模型实时生成（更强大），
                 失败/未配置 Key 自动回退本地组装；返回 source 字段标注来源。
        """
        troll_text = (troll_text or "").strip()
        category = classify(troll_text)
        agg = aggression_score(troll_text)
        st = self._state.setdefault(
            sid,
            {"turns": 0, "last_strategy": None, "last_aggression": 0},
        )
        strategy = self._choose(category, agg, sid, manual)
        rising = agg > st.get("last_aggression", 0)
        source = "local"
        response = ""
        if self._should_use_llm(use_llm, category, agg, rising, st["turns"]):
            response = self._llm_response(
                troll_text, category, strategy, agg, rising, st["turns"], tone, sid,
            )
            if response:
                source = "deepseek"
        if not response:
            if tone == "live":
                # 1) 优先用训练好的本地语言模型生成（字符 n-gram 温度采样，
                #    从语料学来的概率分布写出新话术，天然有变化且零脏话）；
                # 2) 模型缺失/生成过短时回退：召回实战话术 + 实时组装骨架。
                from . import lang_model, phrase_memory
                source = "local-lm"
                response = lang_model.generate_reply(
                    troll_text, category, strategy,
                )
                if not response:
                    source = "local"
                    learned = phrase_memory.recall(troll_text, category, k=3)
                    response = live_compose(
                        troll_text, category, strategy, agg, st["turns"], sid, rising,
                        learned=learned,
                    )
            else:
                response = self._pick_line(strategy, st["turns"], rising, tone, sid)
                if tone == "sharp" and random.random() < 0.35:
                    response = random.choice(SHARP_BLOW)
        # 输出前统一脱敏，确保零脏话
        response = mask_profanity(response)
        troll_text = mask_profanity(troll_text)

        st["turns"] += 1
        st["last_strategy"] = strategy
        st["last_aggression"] = agg

        return {
            "troll_text": troll_text,
            "response": response,
            "strategy": strategy,
            "strategy_name": self._strategy_name(strategy, tone, source),
            "category": category,
            "category_name": CATEGORY_NAMES.get(category, category),
            "aggression": agg,
            "turns": st["turns"],
            "tone": tone,
            "source": source,
        }

    def alternatives(self, troll_text, n=3, manual="auto", tone="live"):
        """生成若干备选回复（不推进会话状态），供手动挑选。"""
        troll_text = (troll_text or "").strip()
        if not troll_text:
            return []
        category = classify(troll_text)
        agg = aggression_score(troll_text)
        strategy = self._choose(category, agg, None, manual)
        name = self._strategy_name(strategy, tone)
        if tone == "live":
            texts = live_alternatives(troll_text, category, strategy, n=n)
            return [
                {"response": t, "strategy": strategy, "strategy_name": name}
                for t in texts
            ]
        if tone == "sharp":
            data = sharp_lines_for(strategy)
        else:
            data = STRATEGIES[strategy]
        pool = [mask_profanity(p) for p in
                data["first_lines"] + data["follow_ups"] + data["escalate_lines"]]
        picked = random.sample(pool, min(n, len(pool)))
        return [
            {"response": p, "strategy": strategy, "strategy_name": name}
            for p in picked
        ]
