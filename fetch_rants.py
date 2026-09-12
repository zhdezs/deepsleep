# -*- coding: utf-8 -*-
"""键盘侠言论获取与连续对线脚本。

用法（在项目根目录执行）：
    py -3 fetch_rants.py --seed                     导入内置网上键盘侠言论样本
    py -3 fetch_rants.py --url <网页地址>           抓取公开网页并提取攻击性言论
    py -3 fetch_rants.py --file <文本文件>          从本地文本文件导入言论
    py -3 fetch_rants.py --list                     查看待处理言论队列
    py -3 fetch_rants.py --duel                     把队列言论全部输入系统，实时组装多行长文连续对线
    py -3 fetch_rants.py --chat                     交互式连续对话（每行输入对方言论）
    py -3 fetch_rants.py --duel --tone sharp        改用尖锐语料（单句）
    py -3 fetch_rants.py --duel --tone default      改用温和语料

说明：
- 抓取结果累积在 data/rants.json（待处理队列），--duel 时全部输入系统并入库学习；
- 默认 live 模式实时组装：引用对方原话逐条拆解、多段多行、字数多，拒绝预制菜；
- 所有模式回复输出前统一脱敏，保证零脏话；
- 仅抓取你有权访问的公开页面，遵守目标站点规则，勿用于骚扰他人。
"""

import argparse
import os
import random
import sys

APP_DIR = os.path.dirname(os.path.abspath(__file__))


def _setup_console():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass


def _load_runtime():
    sys.path.insert(0, APP_DIR)
    from app.storage import Storage
    from app.engine import ResponseEngine
    from app.duel import Duel
    from app import fetcher
    return Storage, ResponseEngine, Duel, fetcher


def cmd_seed(args, fetcher):
    from app.rant_samples import RANT_SAMPLES, SAMPLE_SOURCES
    items = [{"text": s, "source": random.choice(SAMPLE_SOURCES)} for s in RANT_SAMPLES]
    added = fetcher.add_to_queue(items)
    print("内置样本 %d 条，新增入队 %d 条（data/rants.json）。" % (len(items), added))
    print("提示：运行 py -3 fetch_rants.py --duel 把队列全部输入系统进行连续对线。")


def cmd_url(args, fetcher):
    total = 0
    for url in args.url:
        try:
            rants = fetcher.rants_from_url(url, timeout=args.timeout)
        except Exception as e:
            print("抓取失败：%s（%s）" % (url, e))
            continue
        items = [{"text": r["text"], "source": url} for r in rants]
        added = fetcher.add_to_queue(items)
        total += added
        print("从 %s 提取攻击性言论 %d 条，新增入队 %d 条。" % (url, len(rants), added))
    print("本次共新增 %d 条。运行 py -3 fetch_rants.py --list 查看队列。" % total)


def cmd_file(args, fetcher):
    if not os.path.isfile(args.file):
        print("文件不存在：%s" % args.file)
        sys.exit(1)
    raw = None
    for enc in ("utf-8", "gbk", "utf-16"):
        try:
            with open(args.file, "r", encoding=enc) as f:
                raw = f.read()
            break
        except (UnicodeDecodeError, UnicodeError):
            continue
    if raw is None:
        print("无法识别文件编码，请另存为 UTF-8。")
        sys.exit(1)
    sentences = fetcher.split_sentences(raw)
    rants = fetcher.filter_rants(sentences)
    items = [{"text": r["text"], "source": os.path.basename(args.file)} for r in rants]
    added = fetcher.add_to_queue(items)
    print("文件共切出句子 %d 句，其中攻击性言论 %d 条，新增入队 %d 条。" % (len(sentences), len(rants), added))


def cmd_list(args, fetcher):
    queue = fetcher.load_queue()
    if not queue:
        print("待处理队列为空。先用 --seed / --url / --file 导入言论。")
        return
    print("待处理言论队列共 %d 条：\n" % len(queue))
    for i, it in enumerate(queue, 1):
        src = ("  [%s]" % it["source"]) if it.get("source") else ""
        print("%3d. [%s 强度%d]%s %s" % (
            i,
            (it.get("category") or "general"),
            it.get("aggression", 0),
            src,
            it["text"],
        ))


def cmd_duel(args, Storage, ResponseEngine, Duel, fetcher):
    queue = fetcher.load_queue()
    if not queue:
        print("待处理队列为空。先用 --seed / --url / --file 导入言论，再运行 --duel。")
        return
    items = queue[: args.max] if args.max else queue
    storage = Storage(args.db) if args.db else Storage()
    engine = ResponseEngine()
    duel = Duel(storage, engine, tone=args.tone, troll_name=args.troll_name,
                use_llm=args.llm)
    print("开始连续对线：%d 条言论将逐条输入系统（%s%s）。\n" % (
        len(items), Duel._tone_label(args.tone),
        " · DeepSeek 增强" if args.llm else ""))
    sid, turns, win = duel.run_batch(items)
    for r in turns:
        src = "DeepSeek" if r.get("source") == "deepseek" else "本地"
        print("[第%d轮 · %s · 强度%d] %s" % (r["turns"], r["category_name"], r["aggression"], r["troll_text"]))
        print("[我方 · %s（%s）] %s" % (r["strategy_name"], src, r["response"]))
    print("\n" + duel.battle_report(sid))
    print("学习结果：本场策略%s（已写入辩论知识库）" % ("有效" if win else "待优化"))
    if not args.keep:
        fetcher.clear_queue()
        print("（已处理的言论已全部输入系统，队列已清空）")


def cmd_chat(args, Storage, ResponseEngine, Duel):
    storage = Storage(args.db) if args.db else Storage()
    engine = ResponseEngine()
    duel = Duel(storage, engine, tone=args.tone, troll_name=args.troll_name,
                use_llm=args.llm)
    sid, win = duel.chat()
    print("\n" + duel.battle_report(sid))
    print("学习结果：本场策略%s（已写入辩论知识库）" % ("有效" if win else "待优化"))


def main():
    _setup_console()
    parser = argparse.ArgumentParser(
        prog="fetch_rants.py", description="获取网上键盘侠言论并输入系统进行连续对线"
    )
    parser.add_argument("--seed", action="store_true", help="导入内置网上键盘侠言论样本")
    parser.add_argument("--url", action="append", metavar="URL", help="抓取公开网页并提取攻击性言论（可多次）")
    parser.add_argument("--file", metavar="PATH", help="从本地文本文件导入言论")
    parser.add_argument("--list", action="store_true", help="查看待处理言论队列")
    parser.add_argument("--duel", action="store_true", help="把队列言论全部输入系统，连续对线")
    parser.add_argument("--chat", action="store_true", help="交互式连续对话")
    parser.add_argument("--tone", choices=["live", "sharp", "default"], default="live",
                        help="回复语气：live=实时组装多行长文（默认），sharp=尖锐零脏话，default=温和")
    parser.add_argument("--troll-name", default="网络键盘侠", help="对方称呼（用于会话记录）")
    parser.add_argument("--llm", action="store_true",
                        help="接入 DeepSeek 大模型实时生成回怼（需配置 API Key，失败自动回退本地）")
    parser.add_argument("--db", metavar="PATH", help="指定 SQLite 数据库路径（默认 data/app.db）")
    parser.add_argument("--max", type=int, default=0, help="--duel 时最多处理前 N 条言论（0=全部）")
    parser.add_argument("--timeout", type=int, default=15, help="抓取网页超时秒数")
    parser.add_argument("--keep", action="store_true", help="--duel 后保留队列（默认清空）")
    args = parser.parse_args()

    Storage, ResponseEngine, Duel, fetcher = _load_runtime()

    if args.seed:
        cmd_seed(args, fetcher)
    elif args.url:
        cmd_url(args, fetcher)
    elif args.file:
        cmd_file(args, fetcher)
    elif args.list:
        cmd_list(args, fetcher)
    elif args.duel:
        cmd_duel(args, Storage, ResponseEngine, Duel, fetcher)
    elif args.chat:
        cmd_chat(args, Storage, ResponseEngine, Duel)
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
