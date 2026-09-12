# -*- coding: utf-8 -*-
"""以理服人 · 键盘侠反制助手"""

import os
import sys

__version__ = "1.0.0"
__app_name__ = "以理服人"


def is_frozen():
    """是否运行在 PyInstaller 打包后的环境。"""
    return getattr(sys, "frozen", False)


def base_dir():
    """返回「可写」的根目录。

    - 打包后：exe 所在目录（数据文件放 exe 旁边，双击即用、可持久化）；
    - 源码运行：项目根目录。
    """
    if is_frozen():
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def data_dir():
    d = os.path.join(base_dir(), "data")
    os.makedirs(d, exist_ok=True)
    return d


def resource_dir():
    """返回「只读」资源目录（前端页面等）。打包后为 sys._MEIPASS。"""
    if is_frozen():
        return getattr(sys, "_MEIPASS", base_dir())
    return base_dir()
