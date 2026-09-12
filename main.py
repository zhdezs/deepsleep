# -*- coding: utf-8 -*-
"""以理服人 · 键盘侠反制助手 —— 程序入口。

用法：
    py -3 main.py          启动桌面应用
    py -3 main.py --demo   无界面演示模式（模拟一场完整对线并展示统计）
    py -3 main.py --chat   交互式连续对线（实时组装·零脏话）
    py -3 main.py --llm    与 --demo / --chat 联用：接入 DeepSeek 大模型实时生成回怼
                           （需配置 API Key，失败自动回退本地组装）
    py -3 main.py --train  脚本生成键盘侠言论并批量训练回怼模型
    py -3 main.py --train-model  用生成数据训练攻击分类模型（等价于 train_model.py）
    py -3 main.py --train-lm  从头训练本地回怼语言模型（等价于 train_model.py --train-lm）
    py -3 main.py --web  启动 Web 界面（微信聊天框风格，浏览器访问）
    py -3 main.py --web --port 8000  指定端口启动 Web 界面
"""

import sys
import os
import tempfile


def main():
    from app.storage import Storage
    from app.engine import ResponseEngine
    from app.ui import TrollWranglerApp

    storage = Storage()
    engine = ResponseEngine()
    app = TrollWranglerApp(storage, engine)
    app.mainloop()


def demo():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
    from app.storage import Storage
    from app.engine import ResponseEngine
    from app.analyzer import learn_from_session, report

    use_llm = "--llm" in sys.argv
    db = os.path.join(tempfile.gettempdir(), "kb_wrangler_demo.db")
    if os.path.exists(db):
        os.remove(db)
    storage = Storage(db)
    engine = ResponseEngine()

    script = [
        "你懂个屁，就你还配发表意见？",
        "你这种键盘侠就是网络蛀虫，滚吧！",
        "呵呵，就你这水平还敢来对线？垃圾",
        "不服来辩啊，你倒是说说你懂什么",
        "你以为你谁啊，装什么大尾巴狼",
        "算了，懒得跟你这种人说，没意思",
    ]

    sid = storage.create_session("演示键盘侠")
    engine.new_session(sid)
    print("=" * 60)
    print("演示模式：一场完整的键盘侠对线")
    print("=" * 60)
    for msg in script:
        r = engine.generate(msg, sid, use_llm=use_llm)
        storage.add_message(sid, "troll", r["troll_text"],
                            category=r["category"], aggression=r["aggression"])
        storage.add_message(sid, "ai", r["response"], strategy=r["strategy"])
        src = {"deepseek": "DeepSeek", "local-lm": "本地语言模型"}.get(
            r.get("source"), "本地实时组装")
        print(f"\n[对方 · {r['category_name']} · 攻击强度{r['aggression']}] {r['troll_text']}")
        print(f"[我方 · {r['strategy_name']}（{src}）] {r['response']}")
    storage.end_session(sid)
    win = learn_from_session(storage, sid)
    print("\n" + "=" * 60)
    print(f"学习结果：本场策略{'有效（对方退场时强度下降）' if win else '待优化'}")
    print("-" * 60)
    print(report(storage))
    print("=" * 60)


def train():
    """命令行训练：脚本生成键盘侠言论，批量输入系统训练回怼模型并输出报告。"""
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
    import generate_rants
    from app.storage import Storage
    from app.engine import ResponseEngine
    from app import trainer

    generated = generate_rants.generate(500)
    generate_rants.save(generate_rants.TRAIN_PATH, generated)
    samples = [(t, c) for t, c in generated.items()]

    storage = Storage()
    engine = ResponseEngine()
    sid, results, report = trainer.train(storage, engine, samples)
    print("=" * 64)
    print("训练完成：共 %d 条键盘侠言论已输入系统并归档学习（会话 #%s）" % (len(samples), sid))
    print("=" * 64)
    print(report)


def chat():
    """交互式连续对线（实时组装·多行长文·零脏话），输入 q 退出并归档学习。"""
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
    from app.storage import Storage
    from app.engine import ResponseEngine
    from app.duel import Duel

    storage = Storage()
    engine = ResponseEngine()
    use_llm = "--llm" in sys.argv
    duel = Duel(storage, engine, tone="live", use_llm=use_llm)
    sid, win = duel.chat()
    print("\n" + duel.battle_report(sid))
    print("学习结果：本场策略%s（已写入辩论知识库）" % ("有效" if win else "待优化"))


def train_model_cmd():
    """用生成数据训练攻击类型分类模型（委托给 train_model.py）。"""
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
    import train_model
    argv = [a for a in sys.argv[1:] if a != "--train-model"]
    sys.exit(train_model.main(argv))


def train_lm_cmd():
    """从头训练本地回怼语言模型（委托给 train_model.py --train-lm）。"""
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
    import train_model
    argv = [a for a in sys.argv[1:] if a != "--train-lm"]
    if "--train-lm" not in argv:
        argv.append("--train-lm")
    sys.exit(train_model.main(argv))


def web_cmd():
    """启动 Web 界面（微信聊天框风格，浏览器访问）。"""
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
    from app.web import serve, DEFAULT_PORT

    port = DEFAULT_PORT
    if "--port" in sys.argv:
        idx = sys.argv.index("--port")
        if idx + 1 < len(sys.argv):
            try:
                port = int(sys.argv[idx + 1])
            except ValueError:
                pass
    serve(port=port, open_browser=True)


if __name__ == "__main__":
    if "--web" in sys.argv:
        web_cmd()
    elif "--demo" in sys.argv:
        demo()
    elif "--chat" in sys.argv:
        chat()
    elif "--train" in sys.argv:
        train()
    elif "--train-model" in sys.argv:
        train_model_cmd()
    elif "--train-lm" in sys.argv:
        train_lm_cmd()
    else:
        main()
