# -*- coding: utf-8 -*-
"""本地回怼语言模型（从头训练 · BPE 词表 + 词级 n-gram）。

这是「真正的训练」：把项目里积累的实战回怼语料当作训练数据，
用 BPE（字节对编码）自动从语料学习出「词表」（无监督分词），
再在词序列上用最大似然估计训练 n-gram 条件概率（插值回退平滑），
得到一个几百 KB 的统计语言模型。生成新回复时按学到的概率分布
做温度采样，逐词「写」出话术——而不是从写死的句池里挑。

为什么是 BPE + 词级 n-gram：
- 字符级 trigram 在几千句的小语料上计数稀疏，生成近乎随机；
- BPE 先把高频字组合并成词（如「你这段话」「不是」「事实」），
  词表从 757 个字符压缩到数百个词，n-gram 计数不再稀疏；
- 词表是从语料学出来的，属无监督训练，而非人工规定。

特点：
- 零第三方依赖（纯标准库），集显/无独显的机器毫秒级运行，不吃性能；
- 模型文件很小（几百 KB），训练秒级完成，随时可增量重训；
- 语料即常识：模型学的是语料里被验证过的表达方式，不会凭空发明，
  「基本常识」由语料质量保证，回怼锋芒也由语料锋芒保证；
- 温度与种子控制：同一条输入每次输出都不同，且能顺着策略种子
  （如「你这段话」「我问你」）续写出有锋芒的完整句子。

训练语料来源（可配 --db 自动汇总历史对线）：
  1. 内置策略语料 strategies.py / sharp.py 全部句子；
  2. 实时组装骨架句池 composer.py（占位符填充为示例值后入料）；
  3. 实战话术库 data/phrase_memory.json（学习过的段落）；
  4. 历史对线会话中我方 AI 回复（data/app.db）。

用法：
    py -3 train_model.py --train-lm                  # 训练并保存
    py -3 train_model.py --train-lm --db data/app.db # 纳入历史对线语料
    py -3 train_model.py --train-lm --lm-out x.json  # 自定义输出路径
"""

import json
import math
import os
import random
import re
from collections import Counter

from .detector import mask_profanity
from . import data_dir

LM_PATH = os.path.join(data_dir(), "lang_model.json")

ORDER = 3                      # n-gram 阶数（trigram）
BOS = "\u0001"                 # 句首标记（不出现在输出中）
EOS = "\u0002"                 # 句尾标记
SEP = "\u0003"                 # 词序列连接符（模型内部使用，不输出）
NUM_MERGES = 250               # BPE 合并轮数
_MIN_OUT = 14                  # 生成最短长度（字符）
_MAX_OUT = 160                 # 生成最长长度（字符）
_STOP_TOKENS = {"。", "！", "？", "!", "?", "…"}   # 句子终止标点词
_PUNC = set("。！？!?…，、；：,;")
_PLACEHOLDERS = ("{q}", "{w}", "{t}")

# 各策略的「话头种子」：让模型顺着这些开头续写，天然对路。
# 种子尽量口语化、带点损，避免生成结果像 AI 报告。
SEED_BY_STRATEGY = {
    "zen":     ["我不急，", "你继续，", "行，你说，", "我这边时间多得很，", "随你，"],
    "logic":   ["你这段话", "来来来，", "你说得这么满，", "我瞅瞅，", "照你这么说，"],
    "question": ["我问你：", "先回答我，", "你确定吗？", "所以呢，", "敢不敢正面回一句，"],
    "praise":  ["佩服，", "讲真，", "你这张嘴，", "可以啊，", "厉害了，"],
    "echo":    ["你反复说", "还是那句，", "你又在绕，", "翻来覆去就这点，", "车轱辘话，"],
    "care":    ["你这么大的火气，", "你越急，", "消消气，", "看把你气的，", "你最近是不是"],
}

# 常识性回怼金句：补充「基本常识 + 一击命中」语料，避免纯情绪输出
COMMON_SENSE_LINES = [
    "事实不会因为谁嗓门大就改变，论据也不会因为感叹号多就更扎实。",
    "水往低处流，论据往有出处的地方走；没有出处的话，再响也只是噪音。",
    "科学结论靠证据说话，不靠「我听说」和「我觉得」。",
    "键盘敲得再响，也敲不出一个事实。",
    "法律讲证据，辩论讲逻辑，评论区讲情绪——你现在只有第三种。",
    "读万卷书行万里路，都比不上你这一张嘴。",
    "常识不需要付费，你缺的是打开它的勇气。",
    "谁主张谁举证，这是最基本的规则，你好像没学过。",
    "把「我认为」当「事实」来用，是思维上最省钱也最贵的行为。",
    "重复不会让空话变成真话，音量也不会让偏见变成事实。",
    "先分清楚事实和观点，再回来跟我说话。",
    "你的发言里，观点和事实的比例大概是一比零。",
    "不懂不可怕，可怕的是把不懂当底气。",
    "数据不撒谎，撒谎的是引用数据的人。",
    "证据链断在半路的话，说得再满也是悬空。",
    # 更口语、带点损的抬杠话术（让生成更贴真人，少点 AI 腔）
    "来来来，你展开说说，我泡杯茶慢慢听。",
    "讲真，你这话的含金量，跟路边传单差不多。",
    "可以啊，理不直气倒挺壮。",
    "我瞅你这架势，是打算用音量把我震服？",
    "所以呢，说完了能落地的有半句吗？",
    "行了行了，你就剩嗓门了。",
    "你越急，我越觉得你心里没底。",
    "翻来覆去就这一套，储备告急了吧。",
    "看把你急的，字都打不利索了。",
    "嘴皮子挺溜，就是没一句有用的。",
    "你这么能说，咋一句事实都不带。",
    "别光喊，上点干货我看看。",
    "你说的都对，就是跟这事不搭边。",
    "急什么，又没人抢你键盘。",
    "这话我截图了，留着你以后自己看。",
]


# ----------------------------------------------------------------------
# 语料收集
# ----------------------------------------------------------------------

def collect_corpus(db_path=None):
    """汇总全部回怼语料为「按行切分」的训练文本列表（已脱敏、已去占位符）。"""
    from . import strategies as _st
    from . import sharp as _sp

    lines = []

    def add(pool):
        for s in pool:
            s = (s or "").strip()
            if not s:
                continue
            if any(p in s for p in _PLACEHOLDERS):
                # 占位符替换为示例值后入料，让模型学会「引用+拆解」的句式
                s = (s.replace("{q}", "你这句话")
                       .replace("{w}", "你用的那些词")
                       .replace("{t}", "这一轮"))
            s = mask_profanity(s).replace("**", "").strip(" \t\n")
            if len(s) >= 6:
                lines.append(s)

    # 1) 策略语料（温和 + 尖锐 + 补刀）
    for data in _st.STRATEGIES.values():
        add(data.get("first_lines", []))
        add(data.get("follow_ups", []))
        add(data.get("escalate_lines", []))
    for data in _sp.SHARP_LINES.values():
        add(data.get("first_lines", []))
        add(data.get("follow_ups", []))
        add(data.get("escalate_lines", []))
    add(_sp.SHARP_BLOW)

    # 2) 实时组装骨架句池
    try:
        from . import composer as _cp
        add(_cp._QUOTE_OPENS)
        for v in _cp._SKELETONS.values():
            add(v)
        add(_cp._QUICK_CUTS)
        for v in _cp._CATEGORY_LINES.values():
            add(v)
        add(_cp._DISSECT_TEMPLATES)
        add(_cp._FINISHERS)
    except Exception:
        pass

    # 3) 实战话术库
    try:
        from . import phrase_memory as _pm
        for e in _pm.load():
            add([e.get("text", "")])
    except Exception:
        pass

    # 4) 历史对线中我方 AI 回复
    if db_path and os.path.exists(db_path):
        try:
            from .storage import Storage
            st = Storage(db_path)
            for s in st.sessions():
                for m in st.session_messages(s["id"]):
                    if m.get("role") == "ai" and (m.get("content") or "").strip():
                        add([m["content"]])
        except Exception:
            pass

    # 5) 常识性回怼金句
    add(COMMON_SENSE_LINES)

    # 去重、去超短/超长
    seen, out = set(), []
    for s in lines:
        if s in seen or len(s) < 6 or len(s) > 220:
            continue
        seen.add(s)
        out.append(s)
    return out


# ----------------------------------------------------------------------
# BPE：从语料自动学习词表（无监督分词）
# ----------------------------------------------------------------------

def _pre_tokens(text):
    """切出初始 token：连续汉字/字母数字成块，标点单独成词。"""
    toks = []
    buf = []
    for ch in text:
        if ch in _PUNC or ch.isspace():
            if buf:
                toks.append("".join(buf))
                buf = []
            toks.append(ch)
        else:
            buf.append(ch)
    if buf:
        toks.append("".join(buf))
    return [t for t in toks if t and not t.isspace()]


def _apply_merges(toks, merges):
    """按合并顺序贪心合并 token 对（近似 BPE 的最终词序列）。"""
    for a, b in merges:
        new = a + b
        out = []
        i = 0
        n = len(toks)
        while i < n:
            if i + 1 < n and toks[i] == a and toks[i + 1] == b:
                out.append(new)
                i += 2
            else:
                out.append(toks[i])
                i += 1
        toks = out
    return toks


def learn_merges(corpus, num_merges=NUM_MERGES):
    """BPE 训练：迭代合并语料中最高频的相邻 token 对，返回 merges 列表。"""
    token_lists = [_pre_tokens(s) for s in corpus]
    merges = []
    for _ in range(num_merges):
        stats = Counter()
        for toks in token_lists:
            for i in range(len(toks) - 1):
                stats[(toks[i], toks[i + 1])] += 1
        if not stats:
            break
        (a, b), cnt = stats.most_common(1)[0]
        if cnt < 2:  # 只合并出现 2 次以上的词对，避免过拟合
            break
        merges.append((a, b))
        # 更新语料 token 序列
        token_lists = [_apply_merges(t, merges) for t in token_lists]
    return merges


# ----------------------------------------------------------------------
# 训练 / 保存 / 加载
# ----------------------------------------------------------------------

def train(lines, order=ORDER):
    """训练流程：BPE 学词表 → 词序列上统计 n-gram 计数。

    模型结构：
      merges: [[a, b], ...]（BPE 合并表，生成时用于对种子分词）
      uni:   {词: 计数}
      bi:    {前词: {后词: 计数}}
      bi_ctx:{前词: 以它开头的 bi 计数总和}
      tri:   {前缀词对(SEP连接): {后词: 计数}}
      tri_ctx:{前缀词对: 以它开头的 tri 计数总和}
      vocab: 全部词表
    """
    merges = learn_merges(lines)
    token_lists = [_apply_merges(_pre_tokens(s), merges) for s in lines]

    uni, bi, tri = {}, {}, {}
    bi_ctx, tri_ctx = {}, {}

    def _inc(d, k, n=1):
        d[k] = d.get(k, 0) + n

    n_tokens = 0
    for toks in token_lists:
        seq = [BOS, BOS] + toks + [EOS]
        for i in range(2, len(seq)):
            a, b, c = seq[i - 2], seq[i - 1], seq[i]
            _inc(uni, c)
            bi.setdefault(b, {})
            _inc(bi[b], c)
            _inc(bi_ctx, b)
            h2 = a + SEP + b
            tri.setdefault(h2, {})
            _inc(tri[h2], c)
            _inc(tri_ctx, h2)
            if c != EOS:
                n_tokens += 1

    vocab = [t for t in uni if t != EOS]
    return {
        "order": order,
        "merges": merges,
        "vocab": sorted(vocab),
        "uni": uni,
        "bi": bi,
        "bi_ctx": bi_ctx,
        "tri": tri,
        "tri_ctx": tri_ctx,
        "meta": {
            "n_lines": len(lines),
            "n_tokens": n_tokens,
            "vocab_size": len(vocab),
            "model_bytes": 0,
        },
    }


def save(model, path=LM_PATH):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(model, f, ensure_ascii=False, separators=(",", ":"))
    model["meta"]["model_bytes"] = os.path.getsize(path)
    return path


def load(path=LM_PATH):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def available(path=LM_PATH):
    return os.path.exists(path)


# ----------------------------------------------------------------------
# 概率与采样
# ----------------------------------------------------------------------

def _prob(model, ch, ctx):
    """插值回退概率：P = w3*P3 + w2*P2 + w1*P1（各阶加一平滑）。

    P3 = (tri[h2][ch]+1) / (tri_ctx[h2]+V)
    P2 = (bi[h1][ch]+1) / (bi_ctx[h1]+V)
    P1 = (uni[ch]+1)     / (total+V)
    """
    V = len(model["vocab"]) or 1
    uni = model["uni"]
    total = sum(uni.values()) - uni.get(EOS, 0)

    h2 = SEP.join(ctx[-2:]) if len(ctx) >= 2 else ""
    h1 = ctx[-1] if ctx else ""

    # 三阶
    if len(ctx) >= 2:
        c3 = model["tri"].get(h2, {}).get(ch, 0)
        n3 = model["tri_ctx"].get(h2, 0)
        p3 = (c3 + 1.0) / (n3 + V) if n3 > 0 else 1.0 / V
    else:
        p3 = 1.0 / V
    # 二阶
    if h1:
        c2 = model["bi"].get(h1, {}).get(ch, 0)
        n2 = model["bi_ctx"].get(h1, 0)
        p2 = (c2 + 1.0) / (n2 + V) if n2 > 0 else 1.0 / V
    else:
        p2 = 1.0 / V
    # 一阶
    c1 = uni.get(ch, 0)
    p1 = (c1 + 1.0) / (total + V) if total > 0 else 1.0 / V

    return 0.6 * p3 + 0.3 * p2 + 0.1 * p1


def _weighted_choice(model, ctx, temperature=0.9, top_k=60):
    """温度采样 + top-k 截断：先从学到的分布取 top-k 候选，再按温度采样。

    top-k 能挡住小概率的乱拼词，让生成稳定贴合语料风格。
    """
    scores = []
    for tok in model["vocab"] + [EOS]:
        p = _prob(model, tok, ctx)
        if p <= 0:
            continue
        scores.append((p, tok))
    scores.sort(reverse=True)
    scores = scores[:top_k]
    logits = {tok: math.log(p + 1e-12) / max(temperature, 0.05) for p, tok in scores}
    mx = max(logits.values())
    weights = {c: math.exp(v - mx) for c, v in logits.items()}
    total = sum(weights.values())
    r = random.random() * total
    acc = 0.0
    for c, w in weights.items():
        acc += w
        if r <= acc:
            return c
    return random.choice(list(weights))


def _tokenize_for_generation(model, text):
    """把种子文本按学到的 BPE 词表切成词序列。"""
    return _apply_merges(_pre_tokens(text or ""), model["merges"])


def sample(model, seed="", temperature=0.9, max_len=_MAX_OUT, min_len=_MIN_OUT):
    """从模型采样生成一段文本（以 seed 为话头续写，已脱敏、已补句尾）。"""
    seed_toks = _tokenize_for_generation(model, seed)
    ctx = seed_toks[-2:] if seed_toks else [BOS, BOS]
    out = list(seed_toks)
    # 只统计「新生成部分」的长度：seed 不计入最短生成长度，
    # 否则较长的策略种子（如「你这么大的火气，」）会挤占最短长度，
    # 导致模型刚续写几个标点就满足 min_len 提前收尾，产出「你这段话，。」这类空回复。
    seed_len = sum(len(t) for t in seed_toks)
    stop = _STOP_TOKENS
    sentences = 0          # 已生成的完整句子数（种子里带标点不算）
    guard = 0
    while guard < max_len * 3:
        guard += 1
        tok = _weighted_choice(model, ctx, temperature)
        if tok == EOS:
            break
        out.append(tok)
        ctx = (ctx + [tok])[-2:]
        text_len = sum(len(t) for t in out)
        gen_len = text_len - seed_len
        if text_len >= max_len:
            break
        if tok in stop:
            sentences += 1
            # 生成部分已到最短长度：写完一个完整句子就收尾；最多写 3 句
            if gen_len >= min_len or sentences >= 3:
                break
    return postprocess("".join(out))


def postprocess(text):
    """后处理：去空白、清理残留标点、补句号、去掉占位符与脱敏残影。"""
    t = mask_profanity(text or "")
    t = re.sub(r"\s+", "", t)
    t = re.sub(r"[\u0001\u0002\u0003]", "", t)
    t = t.replace("**", "")
    for p in _PLACEHOLDERS:
        t = t.replace(p, "")
    # 清理孤立引号：只删除无法配对的引号，成对的引用保留
    for left, right in (("「", "」"), ("『", "』"), ("“", "”")):
        stack = []
        keep = []
        for ch in t:
            if ch == left:
                stack.append(len(keep))   # 记录左引号位置
                keep.append(ch)
            elif ch == right:
                if stack:                 # 能配对 → 保留这对引号
                    stack.pop()
                    keep.append(ch)
                else:                     # 孤立右引号 → 丢弃
                    pass
            else:
                keep.append(ch)
        # 丢弃未配对的左引号（含其标记）
        drop = set(stack)
        t = "".join(c for i, c in enumerate(keep) if i not in drop)
    # 合并连续同类标点（「，，」「。。」等）
    t = re.sub(r"[，、；:：,;]{2,}", "，", t)
    t = re.sub(r"([。！？!?…]){2,}", r"\1", t)
    # 清理「逗号/顿号/冒号紧跟句末标点」的残留（如「，。」「、。」）
    t = re.sub(r"[，、；:：,;]+(?=[。！？!?…])", "", t)
    # 清理句子中间出现的孤立双标点（如「。，」「？。」）
    t = re.sub(r"([。！？!?…])([，、；:：,;])+", r"\1", t)
    # 删除相邻重复片段（≥6 字的整句重复）
    for span in range(6, len(t) // 2 + 1):
        i = 0
        while i + span * 2 <= len(t):
            if t[i:i + span] == t[i + span:i + span * 2]:
                t = t[:i + span] + t[i + span * 2:]
            else:
                i += 1
    t = t.lstrip("，、；:：,;")
    if t and t[-1] not in "。！？!?…":
        t += "。"
    return t


# ----------------------------------------------------------------------
# 引擎入口
# ----------------------------------------------------------------------

def _looks_like_scraps(text):
    """判断 n-gram 采样结果是否「残片拼接」：
    大量孤立短残片、重复短词、语气词收尾（与 C# 版 LooksLikeScraps 对齐）。
    """
    clauses = re.split(r"[，。！？、；：]", text)
    clauses = [c.strip() for c in clauses if c.strip()]
    if len(clauses) <= 1:
        return False
    # ① 3 字以内的孤立残片 ≥ 2 个
    if sum(1 for c in clauses if len(c) <= 3) >= 2:
        return True
    # ② 末尾孤零零语气词/助词收尾（「。，」「。嗯」）
    if re.search(r"[，。]{2,}$", text):
        return True
    if re.search(r"(嗯|啊|吧|吗|呢|哈)[，。]{1,3}$", text):
        return True
    # ③ 同一短词重复 ≥ 3 次
    short = [c for c in clauses if len(c) <= 4]
    if any(n >= 3 for n in Counter(short).values()):
        return True
    return False


def generate_reply(troll_text, category, strategy, temperature=None):
    """面向引擎的高层入口：锚定对方原话 → 采样续写 → 返回可用回复。

    关键：语言模型本身只学了「回怼文风」，无法凭空理解对方语义。
    所以必须【强制锚定】——把对方原话片段包进「引用式开头」当种子，
    让续写从引用处顺着语料里学到的「拆解/拆台」句式怼回去，并校验
    输出确实带上了该片段，保证回复和对方发言强相关（是回怼，不是复制，
    也不是无关的自由发挥）。

    返回脱敏后的文本；抽不出原话片段 / 模型缺失 / 生成过短时返回 None，
    由引擎回退到 composer 实时组装（同样引用原话逐条拆解，保证相关）。
    """
    model = load()
    if not model:
        return None

    # 抽出对方原话里最有信息量的片段（已脱敏），作为回怼靶心
    from .composer import _extract_quotes
    quotes = _extract_quotes(troll_text, max_len=10, max_n=1)
    q = quotes[0] if quotes else None
    if not q:
        return None

    # 关键分工：
    #  - 「引用对方原话」负责【相关】（是回怼、不是复制也不是无关发挥）；
    #  - 「策略种子」负责【流畅】：原话片段是词表外内容，硬塞进 seed 会让
    #    BPE 切碎、续写变碎，所以主体用策略种子（语料高频、续写自然）生成。
    body_seed = random.choice(SEED_BY_STRATEGY.get(strategy, SEED_BY_STRATEGY["zen"]))
    openers = [
        "你说「%s」。" % q,
        "「%s」——" % q,
        "你抛出「%s」。" % q,
        "就冲你这句「%s」。" % q,
    ]
    opener = random.choice(openers)

    # 温度采样有随机性：最多重试 5 次，取第一个【主体流畅】的结果
    temp_pool = temperature if temperature is not None else [0.7, 0.8, 0.9, 1.0]
    for _ in range(5):
        temp = temp_pool if isinstance(temp_pool, (int, float)) else random.choice(temp_pool)
        body = sample(model, seed=body_seed, temperature=temp)
        if len(body) < 8 or body == body_seed:
            continue
        text = opener + body
        if len(text) >= 12 and q in text and not _looks_like_scraps(text):
            return text
    return None


# ----------------------------------------------------------------------
# 评估
# ----------------------------------------------------------------------

def perplexity(model, lines, sample_n=200):
    """在语料子集上计算平均困惑度（越低说明模型越贴合语料）。"""
    rng = random.Random(42)
    picked = rng.sample(lines, min(sample_n, len(lines)))
    nll = 0.0
    n_tokens = 0
    for line in picked:
        toks = _apply_merges(_pre_tokens(line), model["merges"])
        if not toks:
            continue
        ctx = [BOS, BOS]
        for tok in toks:
            p = _prob(model, tok, ctx)
            nll += -math.log(max(p, 1e-12))
            n_tokens += 1
            ctx = (ctx + [tok])[-2:]
    return math.exp(nll / n_tokens) if n_tokens else float("inf")


# ----------------------------------------------------------------------
# 自检：py -3 app/lang_model.py
# ----------------------------------------------------------------------
if __name__ == "__main__":
    lines = collect_corpus()
    m = train(lines)
    ppl = perplexity(m, lines)
    print("语料行数：%d，BPE 词表：%d，困惑度：%.2f" % (
        len(lines), len(m["vocab"]), ppl))
    print("生成演示（temperature 0.7 / 0.9 / 1.1）：")
    for t in (0.7, 0.9, 1.1):
        print("  T=%.1f  %s" % (t, sample(m, temperature=t)))
    print("保存至：%s" % save(m))
