# -*- coding: utf-8 -*-
"""打包入口：双击 exe 后直接打开独立桌面应用（tkinter 微信聊天框）。

PyInstaller 打包时以本文件为入口（--windowed 无控制台窗口）。
不依赖浏览器，纯本地 tkinter 界面。
"""

import sys

# 无控制台窗口运行时，标准输出可能为 None，做兼容
if sys.stdout is None:
    sys.stdout = open("nul", "w", encoding="utf-8")
if sys.stderr is None:
    sys.stderr = sys.stdout


def main():
    from app.storage import Storage
    from app.engine import ResponseEngine
    from app.ui import TrollWranglerApp

    storage = Storage()
    engine = ResponseEngine()
    app = TrollWranglerApp(storage, engine)
    app.mainloop()


if __name__ == "__main__":
    main()
