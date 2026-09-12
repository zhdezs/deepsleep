# -*- coding: utf-8 -*-
"""
攻击检测与文本脱敏模块。

说明：以下关键词表仅用于「识别」对方是否存在攻击行为，
绝不会出现在本应用生成的任何回复中。
生成的回复在输出前会统一经过 mask_profanity() 脱敏，
确保一个脏话/侮辱词都不出现。
"""

# 攻击类型 -> 关键词（用于分类检测）
CATEGORY_KEYWORDS = {
    "ad_hominem": [
        "垃圾", "废物", "蠢货", "智障", "白痴", "脑残", "傻逼", "贱人", "人渣",
        "饭桶", "蠢猪", "滚", "闭嘴", "菜鸡", "弱鸡", "辣鸡", "煞笔", "傻b",
        "sb", "nmsl", "去死", "活该", "你也配", "没脑子", "有病", "嘴贱",
        "吃错药", "没素质", "欠教育", "丢人现眼", "什么玩意", "狗屁不通", "没家教",
        "过过脑子", "打字打不明白", "欠教育", "找存在感", "说话这么冲", "喷两句",
        "多读点书", "现实生活", "脑子是个好东西", "全世界就你",
    ],
    "threat": [
        "打你", "收拾", "等着瞧", "删号", "举报", "弄死", "砍你", "砸",
        "报警", "人肉", "曝光你", "让你好看", "举报你", "封你", "报地址", "翻出来",
        "记下你", "线下", "全网挂", "查到你", "混不下去", "祈祷",
    ],
    "label": [
        "键盘侠", "喷子", "水军", "五毛", "洗地", "走狗", "舔狗", "带节奏",
        "网暴", "粉丝", "水帖", "撒泼", "理中客", "老套路", "口嗨", "一事无成",
        "收钱", "卖力", "装得可真像",
    ],
    "doubt": [
        "你懂什么", "你算老几", "凭什么", "你配", "你有什么资格", "你行你上",
        "轮得到你", "你有啥资本", "你几斤几两", "懂个屁", "说个屁",
        "算什么东西", "有毛病", "配吗", "也配", "掂量", "外行", "读过书",
        "指手画脚", "装懂", "教育别人", "评论这件事",
    ],
    "provocation": [
        "不服", "来啊", "有本事", "呵呵", "笑死", "就这", "菜", "怂",
        "战五渣", "敢吗", "你敢", "垃圾", "废物", "对线", "不敢", "谁怕谁",
        "抬杠", "哑巴", "认输", "接招", "继续编", "开麦", "经不起推敲", "能耐",
        "说的都对", "最懂行", "聪明坏", "好有道理", "较劲", "理解不了", "你最牛",
        "当专家", "凡人", "找证据", "陪着玩玩", "闲得慌",
    ],
}

# 需要被脱敏替换的敏感词（覆盖脏话与常见侮辱词）
SENSITIVE_WORDS = [
    "傻逼", "煞笔", "傻b", "傻B", "草泥马", "操你妈", "去你妈", "你妈的",
    "cnm", "nmsl", "tm的", "你妈", "妈逼", "妈b", "贱人", "妓女", "婊子",
    "蠢货", "智障", "脑残", "白痴", "废物", "垃圾", "人渣", "饭桶", "蠢猪",
    "杂种", "狗东西", "狗娘养", "sb", "SB", "蠢", "滚", "死",
]

CATEGORY_NAMES = {
    "ad_hominem": "人身攻击",
    "threat": "威胁恫吓",
    "label": "贴标签",
    "doubt": "贬低质疑",
    "provocation": "挑衅拉踩",
    "general": "其他",
}


def classify(text, use_model=True):
    """识别攻击类型：优先采信训练好的分类模型（置信度达标时），
    否则回退关键词规则。模型未训练（无 data/classifier.json）时等效纯规则。

    use_model=False 可强制只用关键词规则（如对比评估用）。
    """
    if use_model:
        try:
            from .classifier import predict_category
            mcat = predict_category(text)
            if mcat:
                return mcat
        except Exception:
            pass  # 模型缺失/损坏时静默回退规则
    for cat in ("ad_hominem", "threat", "label", "doubt", "provocation"):
        for w in CATEGORY_KEYWORDS[cat]:
            if w in text:
                return cat
    return "general"


def aggression_score(text):
    """估算攻击强度 0-10（仅用于判断策略与学习，不进入回复）。"""
    score = 0
    seen = set()   # 同一词可能出现在多个类别表里，去重避免重复计分
    for words in CATEGORY_KEYWORDS.values():
        for w in words:
            if w in text and w not in seen:
                score += 1
                seen.add(w)
    score += text.count("!") + text.count("！")
    score += min(3, len(text) // 60)
    return min(10, score)


def mask_profanity(text):
    """将所有脏话/侮辱词替换为 **，保证输出零脏话。"""
    for w in sorted(SENSITIVE_WORDS, key=len, reverse=True):
        if w in text:
            text = text.replace(w, "**")
    return text
