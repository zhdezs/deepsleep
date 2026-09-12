# -*- coding: utf-8 -*-
"""
键盘侠言论生成器（脚本批量生成训练数据）

用法：
    py -3 generate_rants.py                 # 默认生成 500 条，写入 data/train_rants.json
    py -3 generate_rants.py --count 1000    # 指定数量
    py -3 generate_rants.py --show 10       # 打印 10 条示例
    py -3 generate_rants.py --seed          # 生成后导入「待处理言论队列」（配合 fetch_rants.py --duel）
    py -3 generate_rants.py --train         # 生成后直接进入训练：批量输入系统回怼，输出训练报告
    py -3 generate_rants.py --train --db data/train.db   # 训练结果入库到指定库

原理：用「主语 + 攻击词 + 动作 + 语气」等槽位与模板随机组合，
自动拼出大量多样、无重复的键盘侠言论，覆盖六类攻击手法
（人身攻击/威胁恫吓/贴标签/贬低质疑/挑衅拉踩/阴阳怪气），
每条样本同时记录其「期望攻击类别」，用于训练时的识别准确率评估。
"""

import argparse
import json
import os
import random
import re
import sys

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
TRAIN_PATH = os.path.join(BASE_DIR, "data", "train_rants.json")
DEFAULT_COUNT = 500


def _try_utf8():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass


# ----------------------------------------------------------------------
# 槽位
# ----------------------------------------------------------------------
YOU = ["你", "你这种人", "您", "你这号人", "阁下", "您老人家", "你这货", "你小子"]
BAD = ["垃圾", "废物", "饭桶", "蠢货", "键盘侠", "喷子", "水军", "loser",
       "杠精", "嘴炮", "网络喷子", "键盘选手", "无脑黑", "搅屎棍"]
STRONG_BAD = ["菜鸡", "弱鸡", "辣鸡", "智障", "脑残", "白痴", "蠢蛋", "弱智", "二货"]

# 模板：类型 -> [(模板, 攻击词槽位列表), ...]
TEMPLATES = {
    "ad_hominem": [
        ("{you}就是典型的{bad}，{act}吧！", ["act"]),
        ("{you}是不是吃错药了，说话这么冲。", []),
        ("脑子是个好东西，可惜{you}没有。", []),
        ("{you}连打字都打不明白，还敢来发言？", []),
        ("{you}这水平也配混这个圈子？", []),
        ("{you}现实生活是有多失败，才在网上找存在感。", []),
        ("多读点书再来说话，不然{you}丢人现眼。", []),
        ("{you}就是欠教育，键盘敲得倒是挺响。", []),
        ("你写的这都是什么玩意，狗屁不通。", []),
        ("{you}说话前能不能先过过脑子？", []),
        ("{you}是不是觉得全世界就你最强？", []),
        ("你这种{bad}，在网上也就只能喷两句了。", []),
        ("{you}是不是没照过镜子，哪来的自信。", []),
        ("{you}这脑子，怕是连转都不会转。", []),
        ("就{you}这德行，也敢在网上大放厥词？", []),
        ("{you}上过学吗？说的话跟文盲一样。", []),
        ("{you}是现实里多没人理，才跑网上刷存在感。", []),
        ("{you}这素质，回炉重造都嫌晚。", []),
        ("{you}除了会打字喷人，还会点啥？", []),
        ("{you}这种人，也就配在网上逞能。", []),
        ("{you}脑子进水了吧，说话都不带逻辑。", []),
    ],
    "threat": [
        ("信不信我人肉{you}，把你老底都翻出来。", []),
        ("{you}等着瞧，我会让你好看的。", []),
        ("有本事报地址，我线下找{you}聊聊。", []),
        ("再让我看到{you}发言，我天天举报你账号。", []),
        ("我已经截图了，{you}等着被全网挂吧。", []),
        ("别让我在现实中碰到{you}。", []),
        ("{you}号没了，我认识管理，分分钟封你。", []),
        ("小心点，我已经记下{you}了。", []),
        ("{you}再蹦跶，我让你在这圈子里混不下去。", []),
        ("你最好祈祷别让我查到你是谁。", []),
        ("信不信我曝光{you}的聊天记录，让全网都看看。", []),
        ("{you}再嘴硬，我让你在圈里身败名裂。", []),
        ("我已经把{you}的发言都存了，等着被收拾吧。", []),
        ("别嚣张，{you}这种人迟早有人收拾。", []),
        ("{you}敢留个联系方式试试，我让你吃不了兜着走。", []),
        ("我劝{you}赶紧删帖，不然有你好看。", []),
        ("{you}这号我盯上了，见一次举报一次。", []),
    ],
    "label": [
        ("{you}就是典型的键盘侠，一事无成。", []),
        ("这是哪家的水军，五毛一条？", []),
        ("{you}肯定是收钱洗地的，不然不会这么卖力。", []),
        ("喷子就是喷子，说不过就撒泼。", []),
        ("{you}这种带节奏的，早晚被举报。", []),
        ("理中客是吧，装得可真像。", []),
        ("{you}也就敢在网上口嗨，现实里啥也不是。", []),
        ("{you}是不是收钱了，这么卖力替他说话。", []),
        ("又一只水军出来洗地了，散了吧。", []),
        ("{you}这套带节奏的话术，都是老套路了。", []),
        ("{you}这种无脑黑，除了抹黑还会什么。", []),
        ("{you}就是个职业黑子，专业带节奏。", []),
        ("{you}这种脑残粉，无脑护主。", []),
        ("{you}就是那种见谁咬谁的疯狗。", []),
        ("{you}这种道德绑架的圣母，装什么清高。", []),
        ("{you}这种键盘道德家，最会指点江山。", []),
        ("{you}就是那种只会在网上口嗨的嘴强王者。", []),
        ("{you}这种理中客，屁股早就歪了。", []),
        ("{you}是不是收了黑钱，这么卖力带节奏。", []),
        ("{you}这洗地水平，工资怕是不够发。", []),
        ("{you}这种双标狗，别人说不行你说就行。", []),
    ],
    "doubt": [
        ("{you}懂什么？你有资格评论这件事吗？", []),
        ("{you}算老几？轮得到你说话？", []),
        ("就你也配谈这个话题？", []),
        ("{you}行你上啊，不行别瞎指挥。", []),
        ("{you}懂个屁，外行就别装了。", []),
        ("先掂量掂量自己几斤几两再开口。", []),
        ("{you}连基本常识都没有，还来教育别人。", []),
        ("{you}这种水平，也就骗骗外行了。", []),
        ("{you}有什么资格在这里指手画脚？", []),
        ("你读过书吗？没读过就别发表意见。", []),
        ("{you}有什么资本在这评头论足？", []),
        ("{you}连门都没入，还敢大言不惭。", []),
        ("{you}懂点皮毛就敢出来卖弄了？", []),
        ("{you}有什么拿得出手的东西吗？没有就闭嘴。", []),
        ("{you}先看看自己什么档次，再来教训别人。", []),
        ("{you}一个外行，就别在这装内行了。", []),
        ("{you}哪来的底气说这种话？", []),
        ("{you}不照照自己几斤几两，也配质疑别人？", []),
        ("{you}说得头头是道，实际一窍不通。", []),
        ("{you}这半吊子水平，还敢指点江山？", []),
        ("{you}以为自己是谁？轮得到你下结论？", []),
    ],
    "provocation": [
        ("不服来辩啊，我等着{you}。", []),
        ("就这？就这水平还敢开麦？", []),
        ("笑死我了，{you}这说法经不起推敲。", []),
        ("有本事{you}找出证据来，找不出来就别说了。", []),
        ("来啊，我正好闲得慌，陪{you}玩玩。", []),
        ("{you}倒是反驳我啊，哑巴了？", []),
        ("谁怕谁啊，对线到天亮都行。", []),
        ("{you}也就这点能耐，还敢吹。", []),
        ("不敢接招就别装，趁早认输。", []),
        ("你继续编，我看着呢。", []),
        ("来啊，正面刚，别怂。", []),
        ("{you}不是挺能说吗？怎么不吱声了？", []),
        ("就这口才，还好意思出来对线？", []),
        ("{you}再反驳一句试试，我看你能说出什么花来。", []),
        ("有胆量就把话说清楚，别只会阴阳怪气。", []),
        ("{you}这点本事也敢叫板，笑掉大牙。", []),
        ("你倒是拿出点真东西来，光嘴硬有用吗？", []),
        ("{you}不是很有理吗？来，说给我听听。", []),
        ("别怂，继续啊，我陪你到底。", []),
    ],
    "sarcasm": [
        ("呵呵，{you}说的都对，你说的全对。", []),
        ("对对对，全世界就{you}最懂行。", []),
        ("{you}这种高人，我们凡人理解不了。", []),
        ("哎呀，可把{you}给聪明坏了。", []),
        ("{you}这么厉害，怎么还在网上跟人较劲呢？", []),
        ("行行行，{you}最牛，可以了吧？", []),
        ("{you}可真是抬杠冠军，去工地一定很受欢迎。", []),
        ("你说的每个字我都信，毕竟{you}的嘴不是嘴。", []),
        ("{you}这么懂，怎么不去当专家呢？", []),
        ("哇，好有道理哦，我都要哭了。", []),
        ("{you}可真是个大聪明，啥都懂。", []),
        ("好厉害呢，{you}不去拿诺贝尔奖可惜了。", []),
        ("{you}这逻辑，诺贝尔欠你一个奖。", []),
        ("佩服佩服，{you}的脸皮城墙都比不上。", []),
        ("{you}说得太对了，毕竟嘴长在你身上。", []),
        ("原来如此，{you}的脑回路果然清奇。", []),
        ("{you}这么博学，怎么没去开坛讲法呢？", []),
        ("是是是，{you}永远是对的，行了吧。", []),
        ("{you}这智商，我愿称之为天花板。", []),
    ],
}

ACT = ["滚出论坛", "闭嘴", "别丢人现眼", "一边凉快去", "别在这撒野",
       "赶紧滚蛋", "别在这刷存在感", "哪凉快哪待着去", "别污染评论区", "回你的圈子里去"]

# sarcasm 在检测器中无独立类别，训练时按「挑衅拉踩」评估
EXPECTED_MAP = {
    "ad_hominem": "ad_hominem",
    "threat": "threat",
    "label": "label",
    "doubt": "doubt",
    "provocation": "provocation",
    "sarcasm": "provocation",
}

# 全部「(模板类别, 模板, 槽位列表)」池，供 generate / generate_balanced 共用
_ALL_POOL = [
    (cat, tpl, slots)
    for cat, templates in TEMPLATES.items()
    for tpl, slots in templates
]


def generate(count=DEFAULT_COUNT, seed=None):
    """按模板+槽位随机组合，生成 count 条互不重复的键盘侠言论。

    返回：{言论文本: 期望攻击类别}
    """
    rng = random.Random(seed)
    pool = _ALL_POOL

    out = {}
    max_tries = max(2000, count * 80)
    for _ in range(max_tries):
        if len(out) >= count:
            break
        cat, tpl, slots = rng.choice(pool)
        text = tpl
        text = text.replace("{you}", rng.choice(YOU))
        if "{bad}" in text:
            text = text.replace("{bad}", rng.choice(BAD))
        for slot in slots:
            if slot == "act":
                text = text.replace("{act}", rng.choice(ACT))
        text = re.sub(r"\s+", " ", text).strip()
        if len(text) < 6 or text in out:
            continue
        out[text] = EXPECTED_MAP[cat]
    return out


def generate_balanced(count=DEFAULT_COUNT, seed=None):
    """按攻击类别等量生成，避免样本不均衡让分类模型偏向大类。

    用法同 generate()，返回 {言论文本: 期望攻击类别}；
    用于训练分类模型（train_model.py --count）时优先调用本函数。
    """
    rng = random.Random(seed)
    cats = list(TEMPLATES.keys())
    per_cat = max(1, count // len(cats))
    out = {}
    made = {c: 0 for c in EXPECTED_MAP.values()}  # 已产出各期望类别的条数

    def _make(cat):
        """按模板生成一条 text，返回该条（不判期望类别配额）。"""
        pool = [p for p in _ALL_POOL if p[0] == cat]
        for _ in range(200):
            c, tpl, slots = rng.choice(pool)
            text = tpl
            text = text.replace("{you}", rng.choice(YOU))
            if "{bad}" in text:
                text = text.replace("{bad}", rng.choice(BAD))
            for slot in slots:
                if slot == "act":
                    text = text.replace("{act}", rng.choice(ACT))
            text = re.sub(r"\s+", " ", text).strip()
            if len(text) < 6 or text in out:
                continue
            return text
        return None

    # 每类等量生成（sarcasm 与 provocation 期望类别同为 provocation，
    # 两个模板池都会往 provocation 配额里填充）
    for cat in cats:
        quota = per_cat
        exp = EXPECTED_MAP[cat]
        while made[exp] < quota and len(out) < count * 4:
            text = _make(cat)
            if text is None:
                break
            out[text] = exp
            made[exp] += 1

    # 剩余额度按模板池随机补齐（只填充期望类别还不足 2×per_cat 的小类优先）
    quota = count - len(out)
    stall = 0
    while quota > 0 and len(out) < count * 4:
        exp_pool = [p for p in _ALL_POOL
                    if made[EXPECTED_MAP[p[0]]] < per_cat] or _ALL_POOL
        cat, tpl, slots = rng.choice(exp_pool)
        text = tpl
        text = text.replace("{you}", rng.choice(YOU))
        if "{bad}" in text:
            text = text.replace("{bad}", rng.choice(BAD))
        for slot in slots:
            if slot == "act":
                text = text.replace("{act}", rng.choice(ACT))
        text = re.sub(r"\s+", " ", text).strip()
        if len(text) < 6 or text in out:
            # 模板槽位组合已耗尽时不再生成新句：加停顿计数，避免死循环
            stall += 1
            if stall > 2000:
                break
            continue
        out[text] = EXPECTED_MAP[cat]
        made[EXPECTED_MAP[cat]] += 1
        quota -= 1
        stall = 0
    return out


def load(path=TRAIN_PATH):
    """读取已生成的训练数据。返回 [(text, expected_category), ...]"""
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    items = []
    for it in data.get("generated", []):
        items.append((it["text"], it.get("category", "general")))
    return items


def save(path, generated):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(
            {"generated": [{"text": t, "category": c} for t, c in generated.items()]},
            f,
            ensure_ascii=False,
            indent=2,
        )


def import_to_queue(generated):
    """把生成的言论写入 data/rants.json 待处理队列（与 fetch_rants.py 共用）。"""
    queue_path = os.path.join(BASE_DIR, "data", "rants.json")
    items = []
    if os.path.exists(queue_path):
        with open(queue_path, encoding="utf-8") as f:
            items = json.load(f)
    existing = {it["text"] for it in items}
    for text in generated:
        if text not in existing:
            items.append({"text": text, "source": "训练生成器"})
            existing.add(text)
    with open(queue_path, "w", encoding="utf-8") as f:
        json.dump(items, f, ensure_ascii=False, indent=2)
    return len(items)


def cmd_train(args):
    """生成数据并直接进入训练（批量输入系统回怼 + 训练报告）。"""
    from app.storage import Storage
    from app.engine import ResponseEngine
    from app import trainer

    generated = generate(args.count, seed=getattr(args, "seed", None))
    save(TRAIN_PATH, generated)
    samples = [(t, c) for t, c in generated.items()]

    storage = Storage(os.path.abspath(args.db)) if args.db else Storage()
    engine = ResponseEngine()
    sid, results, report = trainer.train(storage, engine, samples, tone=args.tone)
    print("=" * 64)
    print("训练完成（共 %d 条样本入库学习，会话 #%s）" % (len(samples), sid))
    print("=" * 64)
    print(report)


def main():
    _try_utf8()
    parser = argparse.ArgumentParser(
        description="键盘侠言论生成器：脚本批量生成训练数据",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="示例：\n"
               "  py -3 generate_rants.py\n"
               "  py -3 generate_rants.py --count 1000 --show 5\n"
               "  py -3 generate_rants.py --seed\n"
               "  py -3 generate_rants.py --train\n"
               "  py -3 generate_rants.py --train --db data/train.db",
    )
    parser.add_argument("--count", type=int, default=DEFAULT_COUNT,
                        help="生成条数（默认 %d）" % DEFAULT_COUNT)
    parser.add_argument("--show", type=int, default=0,
                        help="打印示例条数（不写入文件）")
    parser.add_argument("--seed", type=int, default=None,
                        help="随机种子（可复现）")
    parser.add_argument("--out", default=TRAIN_PATH,
                        help="训练数据输出路径（默认 data/train_rants.json）")
    parser.add_argument("--seed-queue", dest="seed_queue", action="store_true",
                        help="生成后导入待处理言论队列（配合 fetch_rants.py --duel）")
    parser.add_argument("--train", action="store_true",
                        help="生成后直接进入训练：批量输入系统回怼并输出报告")
    parser.add_argument("--db", default=None,
                        help="训练结果数据库路径（默认 data/app.db）")
    parser.add_argument("--tone", choices=["live", "sharp", "default"], default="live",
                        help="回怼语气（默认 live 实时组装）")
    args = parser.parse_args()

    generated = generate(args.count, seed=args.seed)

    if args.show:
        items = list(generated.items())[: args.show]
        from app.detector import CATEGORY_NAMES
        for i, (text, cat) in enumerate(items, 1):
            print("[%02d] %s  (期望:%s)" % (i, text, CATEGORY_NAMES.get(cat, cat)))
        return

    save(args.out, generated)
    print("已生成 %d 条键盘侠言论 → %s" % (len(generated), args.out))

    if args.seed_queue:
        total = import_to_queue(generated)
        print("已导入待处理队列，当前队列共 %d 条（可执行 py -3 fetch_rants.py --duel）" % total)

    if args.train:
        cmd_train(args)


if __name__ == "__main__":
    main()
