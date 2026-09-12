# -*- coding: utf-8 -*-
"""
攻击类型分类模型训练脚本：把 generate_rants.py 生成的键盘侠言论
（data/train_rants.json）真正用于训练一个机器学习分类模型
（纯 Python 多项式朴素贝叶斯，零第三方依赖）。

用法：
    py -3 train_model.py                     # 用已有生成数据训练并保存模型
    py -3 train_model.py --count 2000        # 重新生成 2000 条再训练
    py -3 train_model.py --data my.json      # 指定训练数据文件
    py -3 train_model.py --test "你懂个屁"   # 加载已保存模型测试单句
    py -3 train_model.py --no-save           # 只评估不保存
    py -3 train_model.py --learn-phrases     # 从历史对线记录学习实战话术（话术库）
    py -3 train_model.py --train-lm          # 从头训练本地回怼语言模型（字符 n-gram）
    py -3 train_model.py --train-lm --db data/app.db  # 训练时纳入历史对线语料

输出：5 折交叉验证的准确率、各类别精确率/召回率/F1、混淆矩阵，
以及「模型 vs 关键词规则」的对比，随后把训练好的模型写入
data/classifier.json，供 detector 识别链路采信。

--train-lm：把内置策略语料 + 实战话术库 + 历史对线回复汇总为训练集，
从头训练一个字符级 n-gram 语言模型（几十 KB、纯 CPU 瞬时运行），
写入 data/lang_model.json，供 engine 本地生成实时回怼。
"""

import argparse
import os
import random
import sys
from collections import defaultdict

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, BASE_DIR)

from app.classifier import (
    CONF_THRESHOLD,
    MODEL_PATH,
    load_model,
    predict,
    predict_category,
    save_model,
    train,
)
from app.detector import CATEGORY_NAMES, classify as rule_classify

DEFAULT_DATA = os.path.join(BASE_DIR, "data", "train_rants.json")


def _try_utf8():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass


def load_samples(path=DEFAULT_DATA):
    """读取生成数据。返回 [(text, expected_category), ...]"""
    if not os.path.exists(path):
        return []
    import json

    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    items = []
    for it in data.get("generated", []):
        text = (it.get("text") or "").strip()
        if text:
            items.append((text, it.get("category", "general")))
    return items


def stratified_kfold(samples, k=5, seed=42):
    """分层 K 折：每类样本均分到各折，返回 (train_idx, test_idx) 迭代器。"""
    by_cat = defaultdict(list)
    for i, (_, c) in enumerate(samples):
        by_cat[c].append(i)
    folds = [[] for _ in range(k)]
    rng = random.Random(seed)
    for idxs in by_cat.values():
        rng.shuffle(idxs)
        for f in range(k):
            folds[f].extend(idxs[f::k])
    for f in range(k):
        test = set(folds[f])
        train_idx = [i for i in range(len(samples)) if i not in test]
        yield train_idx, sorted(test)


def evaluate(samples, k=5):
    """5 折交叉验证：分别用模型（含阈值采信）与关键词规则识别，输出报告文本。"""
    n = len(samples)
    texts = [t for t, _ in samples]
    labels = [c for _, c in samples]

    # 模型（无阈值）与规则（兜底）的混淆矩阵
    model_cm = defaultdict(lambda: defaultdict(int))
    rule_cm = defaultdict(lambda: defaultdict(int))
    model_hit = rule_hit = 0
    conf_sum = conf_n = 0
    fold_reports = []

    for tr, te in stratified_kfold(samples, k=k):
        m = train([texts[i] for i in tr], [labels[i] for i in tr])
        fold_ok = 0
        for i in te:
            cat, conf = predict(texts[i], m)
            conf_sum += conf
            conf_n += 1
            model_cm[labels[i]][cat] += 1
            if cat == labels[i]:
                model_hit += 1
                fold_ok += 1
            rc = rule_classify(texts[i], use_model=False)
            rule_cm[labels[i]][rc] += 1
            if rc == labels[i]:
                rule_hit += 1
        fold_reports.append(fold_ok / len(te))

    lines = []
    lines.append("交叉验证（%d 折，样本 %d 条）" % (k, n))
    lines.append("  模型各折准确率：%s" % " / ".join("%.1f%%" % (x * 100) for x in fold_reports))
    lines.append("  模型平均准确率：%.1f%%   （关键词规则：%.1f%%）"
                 % (100.0 * model_hit / n, 100.0 * rule_hit / n))
    lines.append("  模型平均置信度：%.2f（阈值 %.2f）" % (conf_sum / conf_n, CONF_THRESHOLD))
    lines.append("")

    # 各类别 精确率/召回率/F1
    classes = sorted({c for _, c in samples})
    lines.append("分类型评估（模型）")
    lines.append("  %-8s %6s %7s %7s %7s %7s" % ("类别", "样本", "准确率", "精确率", "召回率", "F1"))
    for c in classes:
        name = CATEGORY_NAMES.get(c, c)
        tp = model_cm[c][c]
        fp = sum(model_cm[o][c] for o in classes if o != c)
        fn = sum(model_cm[c][o] for o in classes if o != c)
        acc = tp / (tp + fp + fn) if (tp + fp + fn) else 0
        prec = tp / (tp + fp) if (tp + fp) else 0
        rec = tp / (tp + fn) if (tp + fn) else 0
        f1 = 2 * prec * rec / (prec + rec) if (prec + rec) else 0
        lines.append("  %-8s %6d %7.1f%% %7.1f%% %7.1f%% %7.1f%%"
                     % (name, tp + fn, acc * 100, prec * 100, rec * 100, f1 * 100))
    lines.append("")

    # 混淆矩阵
    lines.append("混淆矩阵（模型预测 → 行=真实 列=预测）")
    lines.append("  %-8s %s" % ("类别", "".join("%9s" % CATEGORY_NAMES.get(c, c)[:4] for c in classes)))
    for r in classes:
        row = "  %-8s " % CATEGORY_NAMES.get(r, r)[:4]
        row += "".join("%9d" % model_cm[r][c] for c in classes)
        lines.append(row)
    lines.append("")

    # 阈值敏感性：不同置信度阈值下「模型采信率 + 规则兜底后的整体准确率」
    lines.append("阈值敏感性（采信模型，否则回退关键词规则）")
    hits_by_conf = defaultdict(lambda: [0, 0])  # conf -> [采信且正确, 采信数]
    for tr, te in stratified_kfold(samples, k=k):
        m = train([texts[i] for i in tr], [labels[i] for i in tr])
        for i in te:
            cat, conf = predict(texts[i], m)
            hits_by_conf[conf][1] += 1
            if cat == labels[i]:
                hits_by_conf[conf][0] += 1
    rule_acc = rule_hit / n  # 规则兜底部分按规则全样本准确率近似
    for t in (0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.70):
        ok_model = sum(h[0] for c, h in hits_by_conf.items() if c >= t)
        n_model = sum(h[1] for c, h in hits_by_conf.items() if c >= t)
        n_rule = n - n_model
        overall = (ok_model + rule_acc * n_rule) / n
        lines.append("  阈值 %.2f → 模型采信 %d/%d 条（%.0f%%），整体准确率约 %.1f%%"
                     % (t, n_model, n, 100.0 * n_model / n, overall * 100))
    return "\n".join(lines), model_hit / n, rule_hit / n


def cmd_test(text):
    model = load_model()
    if not model:
        print("未找到已保存的模型（%s），请先运行 py -3 train_model.py" % MODEL_PATH)
        return 1
    from app.classifier import predict_proba

    probs = predict_proba(text, model)
    cat, conf = predict(text, model)
    name = CATEGORY_NAMES.get(cat, cat)
    rule = rule_classify(text, use_model=False)
    rule_name = CATEGORY_NAMES.get(rule, rule)
    print("输入：%s" % text)
    print("模型预测：%s（置信度 %.2f%s）" % (
        name, conf, "" if conf >= CONF_THRESHOLD else "，低于阈值不采信"))
    print("规则兜底：%s" % rule_name)
    print("各类别概率：" + "，".join(
        "%s %.0f%%" % (CATEGORY_NAMES.get(c, c), p * 100)
        for c, p in sorted(probs.items(), key=lambda kv: -kv[1])))
    return 0


def cmd_learn_phrases(db_path=None):
    """从历史对线记录批量学习实战话术（沉淀到 data/phrase_memory.json）。

    对每个「完整」会话判定是否有效（对方攻击强度明显下降）：
    - 有效会话 → 我方回复段落按攻击类型沉淀进话术库（只学好招）；
    - 无效会话 → 跳过（失败经验不进话术库）。
    之后 composer 实时组装时会把学到的段落注入，用「实战检验过的招数」回怼。
    """
    from app import phrase_memory
    from app.storage import Storage

    if not db_path:
        db_path = os.path.join(BASE_DIR, "data", "app.db")
    if not os.path.exists(db_path):
        print("未找到对线数据库（%s）" % db_path)
        print("请先跑一场对线（py -3 main.py --chat 或 --duel），再执行 --learn-phrases。")
        return 1

    storage = Storage(db_path)
    sessions = storage.sessions()
    wins = losses = 0
    total_changed = 0
    for s in sessions:
        sid = s["id"]
        if len(storage.session_messages(sid)) < 2:
            continue
        win = phrase_memory.win_for_session(storage, sid)
        total_changed += phrase_memory.learn_from_session(storage, sid, win)
        wins += 1 if win else 0
        losses += 1 if not win else 0

    ph = phrase_memory.summary()
    print("=" * 60)
    print("话术学习完成：扫描 %d 场会话（有效 %d / 无效 %d），"
          "沉淀实战话术 %d 段" % (wins + losses, wins, losses, total_changed))
    print("话术库总量：%d 段 → %s" % (ph["n"], phrase_memory.PHRASE_PATH))
    for c, n in sorted(ph["categories"].items(), key=lambda kv: -kv[1]):
        from app.detector import CATEGORY_NAMES
        print("  · %s：%d 段" % (CATEGORY_NAMES.get(c, c), n))
    print("=" * 60)
    print("召回演示（本机无界面时验证）：")
    demo_texts = ["你懂个屁，键盘侠", "就这水平也敢来喷？", "你这人怎么这么垃圾"]
    for t in demo_texts:
        hits = phrase_memory.recall(t, "general", k=2)
        print("  输入：%s" % t)
        for h in hits:
            print("    召回：%s" % h)
    return 0


def cmd_train_lm(db_path=None, out=None, demo_temps=(0.7, 0.9, 1.1)):
    """从头训练本地回怼语言模型（字符级 n-gram，零第三方依赖）。

    语料 = 内置策略/尖锐语料 + 实时组装骨架 + 实战话术库 + 历史对线回复 + 常识金句。
    模型 = 统计字符转移概率（插值回退平滑），几十 KB JSON，纯 CPU 毫秒级生成。
    """
    from app import lang_model

    if not out:
        out = lang_model.LM_PATH
    print("正在收集回怼语料（--db %s）……" % (db_path or "未指定，仅用内置语料"))
    lines = lang_model.collect_corpus(db_path=db_path)
    if len(lines) < 20:
        print("语料不足（%d 行），先跑一场对线或多积累话术再训练。" % len(lines))
        return 1

    print("语料 %d 行 → 开始训练字符 n-gram 语言模型……" % len(lines))
    model = lang_model.train(lines)
    ppl = lang_model.perplexity(model, lines)
    print("=" * 60)
    print("本地回怼语言模型训练完成")
    print("  语料行数：%d 行" % len(lines))
    print("  字符词表：%d 个" % len(model["vocab"]))
    print("  训练困惑度：%.2f（越低越贴合回怼语料）" % ppl)
    print("  生成演示（带策略种子，温度 0.7 / 0.9 / 1.1）：")
    for t in demo_temps:
        print("    T=%.1f  %s" % (t, lang_model.sample(
            model, seed="你这段话", temperature=t)))
    print("=" * 60)
    path = lang_model.save(model, out)
    print("模型已保存 → %s（%d KB）" % (path, model["meta"]["model_bytes"] // 1024 + 1))
    print("提示：engine 本地生成将自动使用该模型；删除模型文件即可回退到实时组装。")
    return 0


def main(argv=None):
    _try_utf8()
    parser = argparse.ArgumentParser(
        description="把 generate_rants.py 生成的数据用于训练攻击类型分类模型",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="示例：\n"
               "  py -3 train_model.py\n"
               "  py -3 train_model.py --count 2000\n"
               "  py -3 train_model.py --test \"你懂个屁\"\n"
               "  py -3 train_model.py --data data/train_rants.json --no-save\n"
               "  py -3 train_model.py --learn-phrases   # 从历史对线学习实战话术\n"
               "  py -3 train_model.py --train-lm        # 从头训练本地回怼语言模型",
    )
    parser.add_argument("--count", type=int, default=None,
                        help="重新生成 N 条训练数据再训练（默认使用现有 train_rants.json）")
    parser.add_argument("--data", default=DEFAULT_DATA, help="训练数据文件（默认 data/train_rants.json）")
    parser.add_argument("--test", default=None, help="加载已保存模型测试单句")
    parser.add_argument("--cv", type=int, default=5, help="交叉验证折数（默认 5）")
    parser.add_argument("--no-save", action="store_true", help="只评估，不保存模型")
    parser.add_argument("--out", default=MODEL_PATH, help="模型输出路径（默认 data/classifier.json）")
    parser.add_argument("--learn-phrases", dest="learn_phrases", action="store_true",
                        help="从历史对线记录学习实战话术（沉淀到 data/phrase_memory.json）")
    parser.add_argument("--train-lm", dest="train_lm", action="store_true",
                        help="从头训练本地回怼语言模型（字符 n-gram，写入 data/lang_model.json）")
    parser.add_argument("--lm-out", default=None, help="语言模型输出路径（--train-lm 用，默认 data/lang_model.json）")
    parser.add_argument("--db", default=None, help="对线数据库路径（--learn-phrases / --train-lm 用，默认 data/app.db）")
    args = parser.parse_args(argv)

    if args.train_lm:
        return cmd_train_lm(args.db, args.lm_out)

    if args.learn_phrases:
        return cmd_learn_phrases(args.db)

    if args.test:
        return cmd_test(args.test)

    if args.count:
        import generate_rants
        print("正在按类别等量生成 %d 条训练数据（避免样本不均衡）……" % args.count)
        generated = generate_rants.generate_balanced(args.count)
        samples = [(t, c) for t, c in generated.items()]
        save_path = args.data if args.data != DEFAULT_DATA else DEFAULT_DATA
        generate_rants.save(save_path, generated)
        print("已生成 %d 条 → %s" % (len(samples), save_path))
    else:
        samples = load_samples(args.data)
        if not samples:
            print("未找到训练数据（%s）。请先运行：py -3 generate_rants.py，"
                  "或使用 --count 重新生成。" % args.data)
            return 1
        print("加载训练数据 %d 条 → %s" % (len(samples), args.data))

    print("=" * 68)
    report, model_acc, rule_acc = evaluate(samples, k=args.cv)
    print(report)
    print("=" * 68)

    if args.no_save:
        print("评估完成（未保存模型）")
        return 0

    texts = [t for t, _ in samples]
    labels = [c for _, c in samples]
    model = train(texts, labels)
    out = save_model(model, args.out)
    print("模型训练完成 → %s（样本 %d 条，类别 %d 类，词表 %d 项）"
          % (out, model["n_train"], len(model["classes"]), len(model["vocab"])))
    print("模型平均准确率 %.1f%%（关键词规则 %.1f%%）——detector 将优先采信模型识别"
          % (model_acc * 100, rule_acc * 100))
    return 0


if __name__ == "__main__":
    sys.exit(main())
