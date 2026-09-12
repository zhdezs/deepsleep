# -*- coding: utf-8 -*-
"""
轻量攻击类型分类模型：纯 Python 多项式朴素贝叶斯（零第三方依赖）。

训练数据来自 generate_rants.py 脚本生成的键盘侠言论
（data/train_rants.json），训练脚本为根目录 train_model.py，
训练产物保存为 data/classifier.json。

使用方式：
    py -3 train_model.py                # 用已有生成数据训练并保存模型
    py -3 train_model.py --count 2000   # 重新生成 2000 条再训练
    py -3 train_model.py --test "你懂个屁"  # 加载已保存模型测试单句

识别链路：detector.classify() 优先采信模型结果（置信度达标时），
否则回退到关键词规则，保证未见过的新句式也不会被模型盲目误判。
"""

import json
import math
import os

from . import data_dir

MODEL_PATH = os.path.join(data_dir(), "classifier.json")

# 置信度阈值：模型预测概率低于该值时不采信，交给关键词规则兜底。
# 均衡先验下各类概率较分散，0.40 在「采信率」与「准确率」间平衡较好
#（交叉验证：阈值 0.40 时模型采信 66%、整体准确率约 94%）。
CONF_THRESHOLD = 0.40

CLASSES = ["ad_hominem", "threat", "label", "doubt", "provocation"]


def char_ngrams(text, max_n=2):
    """把文本拆成字符 1~2 gram 频次向量（短中文文本分类效果好、无需分词）。"""
    feats = {}
    text = (text or "").strip()
    if not text:
        return feats
    for ch in text:
        feats[ch] = feats.get(ch, 0) + 1
    for i in range(len(text) - 1):
        bigram = text[i:i + 2]
        feats[bigram] = feats.get(bigram, 0) + 1
    return feats


def _feature_counts(texts, labels, alpha=1.0):
    """统计各类别下每个特征的频次，返回模型原始参数。"""
    classes = sorted(set(labels))
    # 全局词表（出现频次 >= min_df 才保留，压缩模型体积）
    global_counts = {}
    for t in texts:
        for w in char_ngrams(t):
            global_counts[w] = global_counts.get(w, 0) + 1
    vocab = [w for w, n in global_counts.items() if n >= 2]

    class_total = {c: 0 for c in classes}
    per_class = {c: {w: 0 for w in vocab} for c in classes}
    for t, lab in zip(texts, labels):
        for w, cnt in char_ngrams(t).items():
            if w in global_counts and global_counts[w] >= 2:
                per_class[lab][w] += cnt
                class_total[lab] += cnt
    return classes, vocab, class_total, per_class, alpha


def train(texts, labels, alpha=1.0, balanced_prior=True):
    """训练多项式朴素贝叶斯分类器。

    texts: [str]，labels: [str]（长度一致，类别在 CLASSES 范围内）。
    balanced_prior=True 时各类别先验取均匀分布，防止样本多的类别
    （如人身攻击）吞没样本少的类别（如贴标签）。
    返回 model 字典：
      classes / vocab / n_train / alpha /
      class_count（各类样本数）/ log_prior（各类先验对数）/
      log_prob（各类特征对数概率，{class: {feature: float}}）
    """
    classes, vocab, class_total, per_class, a = _feature_counts(texts, labels, alpha)
    n_train = len(texts)
    class_count = {}
    for lab in labels:
        class_count[lab] = class_count.get(lab, 0) + 1

    # log_prior：均匀先验（防大类吞小类）或按样本数平滑
    if balanced_prior:
        log_prior = {c: math.log(1.0 / len(classes)) for c in classes}
    else:
        denom = n_train + len(classes) * a
        log_prior = {c: math.log((class_count.get(c, 0) + a) / denom) for c in classes}

    # log_prob = log((count(w, c) + alpha) / (total_c + alpha * |V|))
    log_prob = {}
    for c in classes:
        norm = class_total[c] + a * len(vocab)
        log_prob[c] = {w: math.log((per_class[c].get(w, 0) + a) / norm) for w in vocab}

    return {
        "classes": classes,
        "vocab": vocab,
        "n_train": n_train,
        "alpha": alpha,
        "class_count": class_count,
        "log_prior": log_prior,
        "log_prob": log_prob,
    }


def save_model(model, path=MODEL_PATH):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(model, f, ensure_ascii=False)
    return path


def load_model(path=MODEL_PATH):
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def predict_proba(text, model=None):
    """返回 {类别: 概率}。文本为空或未加载模型时返回空 dict。"""
    if model is None:
        model = load_model()
    if not model or not text:
        return {}
    feats = char_ngrams(text)
    if not feats:
        return {}
    total = sum(feats.values())  # 归一化，避免长文本放大得分

    log_alpha_vocab = math.log(
        model.get("alpha", 1.0) / (model.get("alpha", 1.0) * len(model["vocab"]))
    )
    scores = {}
    for c in model["classes"]:
        lp = model["log_prior"][c]
        lp += sum(
            model["log_prob"][c].get(w, log_alpha_vocab) * (cnt / total)
            for w, cnt in feats.items()
        )
        scores[c] = lp

    mx = max(scores.values())
    exps = {c: math.exp(s - mx) for c, s in scores.items()}
    z = sum(exps.values())
    return {c: e / z for c, e in exps.items()}


def predict(text, model=None):
    """返回 (类别, 置信度)。模型未加载或文本为空时返回 (None, 0.0)。"""
    probs = predict_proba(text, model)
    if not probs:
        return None, 0.0
    cat = max(probs, key=probs.get)
    return cat, probs[cat]


def predict_category(text, model=None, threshold=CONF_THRESHOLD):
    """置信度达标时返回模型预测类别，否则返回 None（由规则兜底）。"""
    cat, conf = predict(text, model)
    if cat is not None and conf >= threshold:
        return cat
    return None


if __name__ == "__main__":
    # 自检：用内置少量样本验证流程可用
    demo_texts = [
        "你这种垃圾废物，滚出论坛吧！",
        "信不信我人肉你，把你老底翻出来。",
        "你就是典型的键盘侠，一事无成。",
        "你算老几？轮得到你指手画脚？",
        "不服来辩啊，就这水平还敢开麦？",
    ]
    demo_labels = ["ad_hominem", "threat", "label", "doubt", "provocation"]
    m = train(demo_texts, demo_labels)
    for t, e in zip(demo_texts, demo_labels):
        c, conf = predict(t, m)
        print("期望=%-12s 模型=%-12s 置信=%.2f  %s" % (e, c, conf, t))
