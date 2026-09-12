# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller 打包配置：单文件 exe，双击即启动独立桌面应用（tkinter）。

打包命令：
    py -3 -m PyInstaller troll_wrangler.spec
产物在 dist/以理服人.exe
"""

import os

block_cipher = None

a = Analysis(
    ["launcher.py"],
    pathex=[os.path.abspath(".")],
    binaries=[],
    datas=[],  # 前端资源改为纯 tkinter，无需打包网页
    hiddenimports=[
        "app", "app.ui", "app.engine", "app.storage", "app.detector",
        "app.analyzer", "app.composer", "app.classifier", "app.llm",
        "app.lang_model", "app.phrase_memory", "app.strategies", "app.sharp",
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)
pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name="以理服人",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,          # 无控制台窗口，双击即用
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=None,
)
