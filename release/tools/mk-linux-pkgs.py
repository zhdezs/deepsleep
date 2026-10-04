# -*- coding: utf-8 -*-
r"""deepsleep Linux 安装包打包器：把发布目录打成 .deb（Debian / Ubuntu）和 .rpm（Fedora / RHEL / openSUSE）。

本机是 Windows，没有 dpkg-deb / rpmbuild，所以这里**按格式手写**。格式细节不是猜的，是对着真实软件包
（rpm-software-management/rpm 的 tests/data/RPMS 里几个包 + Debian 官方 hello.deb）逐字节核对过的：
  · .deb = ar 归档（debian-binary + control.tar.gz + data.tar.gz），成员名按 dpkg-deb 的写法用空格补齐；
  · .rpm = lead(96B) + signature header + main header + gzip(cpio newc) 载荷。头里的关键约定：
      - 区域标签（签名头 62 / 主头 63）必须是索引第 0 项、数据放数据区最后，
        值 = (标签号, 7, -(索引项数 * 16), 16)；
      - 签名头的 SHA1(269) / SHA256(273) = 主头**未补 8 字节对齐**的原始字节；
      - 签名头的 MD5(1004) 与 SIZE(1000) = 主头**补 8 字节对齐后** + 压缩载荷；
      - PAYLOADDIGEST(5092) = 压缩载荷的 SHA256；PAYLOADSIZE(1007) = 解压后长度；
      - 文件摘要用 SHA256（FILEDIGESTALGO=8），声明 rpmlib(FileDigests) / rpmlib(CompressedFileNames)。
写完会把自己产出的包**重新解析一遍**自检（verify_* 段），头里的文件表、cpio 顺序、摘要、大小全都对得上才算过。

用法（一般由 publish.py 调）：
  python mk-linux-pkgs.py --src <发布目录> --out <输出目录> --version 3.0.3 --arch x64 --icon <ico>
"""
import argparse
import gzip
import hashlib
import io
import os
import struct
import sys
import tarfile

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


# ======================================================================
# 公共小工具
# ======================================================================

def _files(root):
    out = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        for fn in sorted(filenames):
            full = os.path.join(dirpath, fn)
            out.append((os.path.relpath(full, root).replace("\\", "/"), full))
    out.sort()
    return out


def _dirs_of(root):
    seen = []
    for rel, _full in _files(root):
        parts = rel.split("/")[:-1]
        for i in range(1, len(parts) + 1):
            d = "/".join(parts[:i])
            if d and d not in seen:
                seen.append(d)
    return seen


def _tar_gz(entries, out_path):
    """entries = [(arcname, full|None, data|None, mode)]；目录给 full=data=None。"""
    with open(out_path, "wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", compresslevel=9, mtime=0) as gz:
            with tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as t:
                for arcname, full, data, mode in entries:
                    ti = tarfile.TarInfo(arcname)
                    ti.mtime = 0
                    ti.uid = ti.gid = 0
                    ti.uname = ti.gname = "root"
                    ti.mode = mode
                    if full is None and data is None:
                        ti.type = tarfile.DIRTYPE
                        ti.size = 0
                        t.addfile(ti)
                    elif data is not None:
                        ti.type = tarfile.REGTYPE
                        ti.size = len(data)
                        t.addfile(ti, io.BytesIO(data))
                    else:
                        ti.type = tarfile.REGTYPE
                        ti.size = os.path.getsize(full)
                        with open(full, "rb") as f:
                            t.addfile(ti, f)


def _gzip_bytes(data, level=9):
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode="wb", compresslevel=level, mtime=0) as gz:
        gz.write(data)
    return buf.getvalue()


def _sha256b(data):
    return hashlib.sha256(data).hexdigest()


def _sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _data_of(item):
    """item = (path, mode, disk|None, data|None) -> 文件字节。"""
    _p, _mode, disk, data = item
    if data is not None:
        return data
    with open(disk, "rb") as f:
        return f.read()


def _is_dir(item):
    return item[2] is None and item[3] is None


def _png_from_ico(ico_path, out_path):
    """从 .ico 里挑最大的那张 PNG（本项目 ico 里存的都是 PNG）另存成 .png。"""
    with open(ico_path, "rb") as f:
        b = f.read()
    if len(b) < 6:
        return False
    _res, _typ, n = struct.unpack("<HHH", b[:6])
    best = None
    for i in range(n):
        w, _h, _c, _r, _p, _bpp, sz, off = struct.unpack("<BBBBHHII", b[6 + 16 * i:22 + 16 * i])
        px = w or 256
        if b[off:off + 8] != b"\x89PNG\r\n\x1a\n":
            continue
        if best is None or px > best[0]:
            best = (px, off, sz)
    if not best:
        return False
    _px, off, sz = best
    with open(out_path, "wb") as f:
        f.write(b[off:off + sz])
    return True


DESKTOP_FILE = """[Desktop Entry]
Type=Application
Name=deepsleep
Name[zh_CN]=deepsleep
GenericName=AI assistant
Comment=Cross-platform AI assistant that can edit files, run commands and search the web
Comment[zh_CN]=能读写文件、跑命令、搜网络、做深度研究的跨平台 AI 助手
Exec=/opt/deepsleep/deepsleep.sh %U
Icon=deepsleep
Terminal=false
Categories=Utility;Development;
Keywords=AI;assistant;agent;
StartupWMClass=deepsleep
"""

LAUNCH_SH = """#!/usr/bin/env bash
# deepsleep 桌面版（Linux）启动脚本；加 --headless 即内核模式（只跑服务，用浏览器连）
# 双击或在终端执行：./deepsleep.sh           默认打开原生桌面窗口
# 服务器 / SSH 用：./deepsleep.sh --headless  （只跑内核服务，浏览器打开 http://127.0.0.1:8756/web/core/）
# 系统装在 /opt/deepsleep（root 所有）写不进去，OTA 的新版本落在用户目录，这里优先拉起它
_USER_APP="${XDG_DATA_HOME:-$HOME/.local/share}/deepsleep/app"
if [ -x "$_USER_APP/deepsleep" ]; then
  cd "$_USER_APP" || exit 1
  exec ./deepsleep "$@"
fi
cd "$(dirname "$0")" || exit 1
chmod +x ./deepsleep 2>/dev/null
exec ./deepsleep "$@"
"""

EXEC_NAMES = ("deepsleep", "deepsleep.sh", "websearch.py")


def _stage(root, icon_png):
    """把发布目录整理成安装清单：[(安装绝对路径, 权限, 磁盘文件|None, 内联字节|None)]。"""
    items = [("/opt/deepsleep", 0o755, None, None)]
    for d in _dirs_of(root):
        items.append(("/opt/deepsleep/" + d, 0o755, None, None))
    for rel, full in _files(root):
        if rel == "deepsleep.sh" or rel.lower().endswith(".pdb"):
            continue          # 调试符号不进安装包（与 Windows 发布保持一致）
        name = rel.rsplit("/", 1)[-1]
        mode = 0o755 if name in EXEC_NAMES else 0o644
        items.append(("/opt/deepsleep/" + rel, mode, full, None))
    items.append(("/opt/deepsleep/deepsleep.sh", 0o755, None, LAUNCH_SH.encode("utf-8")))
    items.append(("/usr/bin/deepsleep", 0o755, None,
                  b"#!/bin/sh\n# deepsleep: exec the launcher in /opt/deepsleep\nexec /opt/deepsleep/deepsleep.sh \"$@\"\n"))
    for d in ("/usr/share/applications", "/usr/share/icons/hicolor",
              "/usr/share/icons/hicolor/256x256", "/usr/share/icons/hicolor/256x256/apps"):
        items.append((d, 0o755, None, None))
    items.append(("/usr/share/applications/deepsleep.desktop", 0o644, None, DESKTOP_FILE.encode("utf-8")))
    items.append(("/usr/share/icons/hicolor/256x256/apps/deepsleep.png", 0o644, None, icon_png))
    items.sort(key=lambda it: (it[0].strip("/").split("/"), 0 if _is_dir(it) else 1))
    # 按路径分段字典序排 -> 父目录必然排在子项之前；目录排在同名文件之前
    return items


# ======================================================================
# .deb
# ======================================================================

def _ar_member(name, data):
    head = (name + " " * (16 - len(name))).encode("ascii")
    head += b"%-12d" % 0
    head += b"%-6d" % 0
    head += b"%-6d" % 0
    head += b"%-8s" % b"100644"
    head += b"%-10d" % len(data)
    head += b"\x60\x0a"          # ar 成员的结尾魔数：0x60 0x0a
    assert len(head) == 60, len(head)
    out = bytearray(head + data)
    if len(data) % 2:
        out += b"\n"             # ar 成员按 2 字节对齐
    return bytes(out)


def pack_deb(items, out_path, version, arch_deb, depends, summary, description, tmpdir):
    os.makedirs(tmpdir, exist_ok=True)
    total = 0
    data_entries = []
    md5lines = []
    for p, mode, disk, data in items:
        arc = "./" + p.strip("/")
        if _is_dir((p, mode, disk, data)):
            arc += "/"
            data_entries.append((arc, None, None, mode))
            continue
        blob = _data_of((p, mode, disk, data))
        total += len(blob)
        data_entries.append((arc, None, blob, mode))
        md5lines.append("%s  %s" % (hashlib.md5(blob).hexdigest(), p.strip("/")))

    desc_lines = description.strip().split("\n")
    ctrl = []
    ctrl.append("Package: deepsleep")
    ctrl.append("Version: " + version)
    ctrl.append("Architecture: " + arch_deb)
    ctrl.append("Maintainer: zhdezs <zhdezs@users.noreply.github.com>")
    ctrl.append("Installed-Size: %d" % (total // 1024))
    ctrl.append("Depends: " + depends)
    ctrl.append("Section: utils")
    ctrl.append("Priority: optional")
    ctrl.append("Homepage: https://zhdezs.github.io/deepsleep/")
    ctrl.append("Description: " + desc_lines[0])
    for line in desc_lines[1:]:
        ctrl.append(" " + line if line.strip() else " .")
    ctrl.append("")
    control = "\n".join(ctrl)

    ctar = os.path.join(tmpdir, "control.tar.gz")
    dtar = os.path.join(tmpdir, "data.tar.gz")
    _tar_gz([(".", None, None, 0o755),
             ("./control", None, control.encode("utf-8"), 0o644),
             ("./md5sums", None, ("\n".join(md5lines) + "\n").encode("utf-8"), 0o644)], ctar)
    _tar_gz([(".", None, None, 0o755)] + data_entries, dtar)
    with open(ctar, "rb") as f:
        cbytes = f.read()
    with open(dtar, "rb") as f:
        dbytes = f.read()

    with open(out_path, "wb") as f:
        f.write(b"!<arch>\n")
        f.write(_ar_member("debian-binary", b"2.0\n"))
        f.write(_ar_member("control.tar.gz", cbytes))
        f.write(_ar_member("data.tar.gz", dbytes))
    return {"files": len([1 for it in items if not _is_dir(it)]), "installed": total}


def _read_ar(blob):
    assert blob[:8] == b"!<arch>\n", "不是 ar 归档"
    off = 8
    out = []
    while off + 60 <= len(blob):
        head = blob[off:off + 60]
        if head[58:60] != b"\x60\x0a":
            break
        name = head[0:16].decode("ascii").strip().rstrip("/")
        size = int(head[48:58].decode("ascii").strip())
        data = blob[off + 60:off + 60 + size]
        out.append((name, data))
        off += 60 + size + (size % 2)
    return out


def verify_deb(path):
    with open(path, "rb") as f:
        blob = f.read()
    members = _read_ar(blob)
    names = [n for n, _d in members]
    assert names[:1] == ["debian-binary"], names
    assert "control.tar.gz" in names and "data.tar.gz" in names, names
    d = dict(members)
    assert d["debian-binary"] == b"2.0\n", d["debian-binary"]
    ctrl_tar = tarfile.open(fileobj=io.BytesIO(d["control.tar.gz"]), mode="r:gz")
    cnames = [m.name for m in ctrl_tar.getmembers()]
    assert "./control" in cnames and "./md5sums" in cnames, cnames
    control = ctrl_tar.extractfile("./control").read().decode("utf-8")
    md5sums = ctrl_tar.extractfile("./md5sums").read().decode("utf-8")
    pkg = {}
    for line in control.split("\n"):
        if ": " in line:
            k, v = line.split(": ", 1)
            pkg.setdefault(k, v)
    want = {}
    for line in md5sums.strip().split("\n"):
        h, pth = line.split("  ", 1)
        want[pth] = h
    data_tar = tarfile.open(fileobj=io.BytesIO(d["data.tar.gz"]), mode="r:gz")
    got = {}
    for m in data_tar.getmembers():
        if m.isfile():
            got[m.name.lstrip("./")] = hashlib.md5(data_tar.extractfile(m).read()).hexdigest()
    assert want == got, ("校验和/文件表不一致", sorted(set(want) ^ set(got))[:5])
    execs = [m.name for m in data_tar.getmembers() if m.isfile() and (m.mode & 0o111)]
    return {
        "package": pkg.get("Package"), "version": pkg.get("Version"), "arch": pkg.get("Architecture"),
        "files": len(got), "executables": sorted(execs),
        "has_opt": any(k.startswith("opt/deepsleep/") for k in got),
        "has_launcher": "usr/bin/deepsleep" in got,
    }


# ======================================================================
# .rpm
# ======================================================================

RPM_LEAD_MAGIC = b"\xed\xab\xee\xdb"
RPM_HDR_MAGIC = b"\x8e\xad\xe8\x01\x00\x00\x00\x00"

T_INT16, T_INT32, T_INT64 = 3, 4, 5
T_STRING, T_BIN, T_STRING_ARRAY, T_I18NSTRING = 6, 7, 8, 9

SIG_HEADERSIGNATURES = 62
SIG_SHA1, SIG_SHA256, SIG_SIZE, SIG_MD5, SIG_PAYLOADSIZE = 269, 273, 1000, 1004, 1007

T_HEADERI18NTABLE = 100
T_HEADERIMMUTABLE = 63
T_NAME, T_VERSION, T_RELEASE = 1000, 1001, 1002
T_SUMMARY, T_DESCRIPTION = 1004, 1005
T_BUILDTIME, T_BUILDHOST, T_SIZE = 1006, 1007, 1009
T_LICENSE, T_GROUP = 1014, 1016
T_URL, T_OS, T_ARCH = 1020, 1021, 1022
T_FILESIZES, T_FILEMODES, T_FILERDEVS, T_FILEMTIMES = 1028, 1030, 1033, 1034
T_FILEDIGESTS, T_FILELINKTOS, T_FILEFLAGS = 1035, 1036, 1037
T_FILEUSERNAME, T_FILEGROUPNAME = 1039, 1040
T_SOURCERPM, T_FILEVERIFYFLAGS = 1044, 1045
T_PROVIDENAME, T_REQUIREFLAGS, T_REQUIRENAME, T_REQUIREVERSION = 1047, 1048, 1049, 1050
T_RPMVERSION, T_FILEINODES = 1064, 1095
T_PROVIDEFLAGS, T_PROVIDEVERSION = 1112, 1113
T_DIRINDEXES, T_BASENAMES, T_DIRNAMES = 1116, 1117, 1118
T_PAYLOADFORMAT, T_PAYLOADCOMPRESSOR, T_PAYLOADFLAGS = 1124, 1125, 1126
T_PLATFORM, T_FILECOLORS, T_FILEDIGESTALGO = 1132, 1140, 5011
T_ENCODING, T_PAYLOADDIGEST, T_PAYLOADDIGESTALGO = 5062, 5092, 5093

_ALIGN = {T_INT16: 2, T_INT32: 4, T_INT64: 8}


def _s(text):
    return text.encode("utf-8") + b"\x00"


def _sa(items):
    return b"".join(_s(x) for x in items)


def _i32(vals):
    return struct.pack(">%di" % len(vals), *vals)


def _i16(vals):
    return struct.pack(">%dH" % len(vals), *[v & 0xFFFF for v in vals])


def _rpm_header(region_tag, entries):
    """entries = [(tag, type, count, raw)]；region_tag 自动排到索引第 0 项、数据放数据区最后。"""
    rest = sorted([e for e in entries if e[0] != region_tag], key=lambda e: e[0])
    n = len(rest) + 1
    store = bytearray()
    offs = {}
    for tag, typ, _cnt, raw in rest:
        a = _ALIGN.get(typ, 1)
        while len(store) % a:
            store.append(0)
        offs[tag] = len(store)
        store += raw
    roff = len(store)
    store += struct.pack(">IIiI", region_tag, T_BIN, -(n * 16), 16)
    idx = [(region_tag, T_BIN, roff, 16)] + [(t, ty, offs[t], c) for t, ty, c, _r in rest]
    out = bytearray(RPM_HDR_MAGIC)
    out += struct.pack(">II", len(idx), len(store))
    for t, ty, o, c in idx:
        out += struct.pack(">IIII", t, ty, o, c)
    out += store
    return bytes(out)


def _cpio_newc(entries, mtime=0):
    """entries = [(name, mode, data)]，目录给 data=None 且 name 以 / 结尾。"""
    out = bytearray()
    ino = 1
    for name, mode, data in entries:
        blob = data or b""
        nb = name.encode("utf-8") + b"\x00"
        fields = (ino, mode, 0, 0, 1, mtime, len(blob), 0, 0, 0, 0, len(nb), 0)
        out += b"070701" + b"".join(b"%08X" % f for f in fields) + nb
        out += b"\x00" * ((-len(out)) % 4)
        if blob:
            out += blob
            out += b"\x00" * ((-len(out)) % 4)
        ino += 1
    nb = b"TRAILER!!!\x00"
    fields = (0, 0, 0, 0, 1, mtime, 0, 0, 0, 0, 0, len(nb), 0)
    out += b"070701" + b"".join(b"%08X" % f for f in fields) + nb
    out += b"\x00" * ((-len(out)) % 4)
    return bytes(out)


def _rpm_lead(nvra, archnum):
    name = nvra.encode("ascii", "replace")[:65]
    return (RPM_LEAD_MAGIC + struct.pack(">BB", 3, 0) + struct.pack(">hh", 0, archnum)
            + name.ljust(66, b"\x00") + struct.pack(">hh", 1, 5) + b"\x00" * 16)


def pack_rpm(items, out_path, version, release, arch_rpm, archnum, summary, description,
             buildtime, tmpdir):
    os.makedirs(tmpdir, exist_ok=True)
    dirnames = sorted({p.rsplit("/", 1)[0] + "/" for p, *_r in items})
    didx = {d: i for i, d in enumerate(dirnames)}

    cpio, basenames, dirindexes = [], [], []
    sizes, modes, rdevs, mtimes, digests, linktos, flags = [], [], [], [], [], [], []
    users, groups, verifyflags, inodes, colors = [], [], [], [], []
    installed = 0
    for i, it in enumerate(items):
        p, mode, _disk, _data = it
        d = p.rsplit("/", 1)[0] + "/"
        base = p.rsplit("/", 1)[1]
        basenames.append(base)
        dirindexes.append(didx[d])
        if _is_dir(it):
            cpio.append(("./" + p.strip("/") + "/", 0o40755, None))
            sizes.append(4096)
            modes.append(0o40755)
            digests.append("")
        else:
            blob = _data_of(it)
            installed += len(blob)
            cpio.append(("./" + p.strip("/"), (0o100000 | mode) & 0xFFFF, blob))
            sizes.append(len(blob))
            modes.append((0o100000 | mode) & 0xFFFF)
            digests.append(_sha256b(blob))
        rdevs.append(0)
        mtimes.append(buildtime)
        linktos.append("")
        flags.append(0)
        users.append("root")
        groups.append("root")
        verifyflags.append(-1)
        inodes.append(i + 1)
        colors.append(0)

    raw_payload = _cpio_newc(cpio)
    payload = _gzip_bytes(raw_payload)
    name = "deepsleep"
    nvra = "%s-%s-%s" % (name, version, release)
    rpmlib = 0x01000000 | 0x02 | 0x08          # RPMLIB | LESS | EQUAL

    entries = [
        (T_HEADERI18NTABLE, T_STRING_ARRAY, 1, _s("C")),
        (T_NAME, T_STRING, 1, _s(name)),
        (T_VERSION, T_STRING, 1, _s(version)),
        (T_RELEASE, T_STRING, 1, _s(release)),
        (T_SUMMARY, T_I18NSTRING, 1, _s(summary)),
        (T_DESCRIPTION, T_I18NSTRING, 1, _s(description)),
        (T_BUILDTIME, T_INT32, 1, _i32([buildtime])),
        (T_BUILDHOST, T_STRING, 1, _s("deepsleep")),
        (T_SIZE, T_INT32, 1, _i32([installed])),
        (T_LICENSE, T_STRING, 1, _s("MIT")),
        (T_GROUP, T_I18NSTRING, 1, _s("Applications/System")),
        (T_URL, T_STRING, 1, _s("https://zhdezs.github.io/deepsleep/")),
        (T_OS, T_STRING, 1, _s("linux")),
        (T_ARCH, T_STRING, 1, _s(arch_rpm)),
        (T_SOURCERPM, T_STRING, 1, _s(nvra + ".src.rpm")),
        (T_RPMVERSION, T_STRING, 1, _s("4.14.0")),
        (T_PLATFORM, T_STRING, 1, _s(arch_rpm)),
        (T_ENCODING, T_STRING, 1, _s("utf-8")),
        (T_PAYLOADFORMAT, T_STRING, 1, _s("cpio")),
        (T_PAYLOADCOMPRESSOR, T_STRING, 1, _s("gzip")),
        (T_PAYLOADFLAGS, T_STRING, 1, _s("9")),
        (T_PAYLOADDIGEST, T_STRING_ARRAY, 1, _s(_sha256b(payload))),
        (T_PAYLOADDIGESTALGO, T_INT32, 1, _i32([8])),
        (T_FILEDIGESTALGO, T_INT32, 1, _i32([8])),
        (T_FILESIZES, T_INT32, len(sizes), _i32(sizes)),
        (T_FILEMODES, T_INT16, len(modes), _i16(modes)),
        (T_FILERDEVS, T_INT16, len(rdevs), _i16(rdevs)),
        (T_FILEMTIMES, T_INT32, len(mtimes), _i32(mtimes)),
        (T_FILEDIGESTS, T_STRING_ARRAY, len(digests), _sa(digests)),
        (T_FILELINKTOS, T_STRING_ARRAY, len(linktos), _sa(linktos)),
        (T_FILEFLAGS, T_INT32, len(flags), _i32(flags)),
        (T_FILEUSERNAME, T_STRING_ARRAY, len(users), _sa(users)),
        (T_FILEGROUPNAME, T_STRING_ARRAY, len(groups), _sa(groups)),
        (T_FILEVERIFYFLAGS, T_INT32, len(verifyflags), _i32(verifyflags)),
        (T_FILEINODES, T_INT32, len(inodes), _i32(inodes)),
        (T_FILECOLORS, T_INT32, len(colors), _i32(colors)),
        (T_DIRINDEXES, T_INT32, len(dirindexes), _i32(dirindexes)),
        (T_BASENAMES, T_STRING_ARRAY, len(basenames), _sa(basenames)),
        (T_DIRNAMES, T_STRING_ARRAY, len(dirnames), _sa(dirnames)),
        (T_PROVIDENAME, T_STRING_ARRAY, 1, _s(name)),
        (T_PROVIDEFLAGS, T_INT32, 1, _i32([0x08])),
        (T_PROVIDEVERSION, T_STRING_ARRAY, 1, _s("%s-%s" % (version, release))),
        (T_REQUIRENAME, T_STRING_ARRAY, 2, _sa(["rpmlib(CompressedFileNames)", "rpmlib(FileDigests)"])),
        (T_REQUIREFLAGS, T_INT32, 2, _i32([rpmlib, rpmlib])),
        (T_REQUIREVERSION, T_STRING_ARRAY, 2, _sa(["3.0.4-1", "4.6.0-1"])),
    ]

    main = _rpm_header(T_HEADERIMMUTABLE, entries)
    mainpad = main + b"\x00" * ((-len(main)) % 8)
    total = len(mainpad) + len(payload)

    sig = _rpm_header(SIG_HEADERSIGNATURES, [
        (SIG_SHA1, T_STRING, 1, _s(hashlib.sha1(main).hexdigest())),
        (SIG_SHA256, T_STRING, 1, _s(hashlib.sha256(main).hexdigest())),
        (SIG_SIZE, T_INT32, 1, _i32([total])),
        (SIG_MD5, T_BIN, 16, hashlib.md5(mainpad + payload).digest()),
        (SIG_PAYLOADSIZE, T_INT32, 1, _i32([len(raw_payload)])),
    ])
    sigpad = sig + b"\x00" * ((-len(sig)) % 8)

    with open(out_path, "wb") as f:
        f.write(_rpm_lead(nvra, archnum))
        f.write(sigpad)
        f.write(mainpad)
        f.write(payload)
    return {"files": len(items), "installed": installed, "payload": len(payload)}


def _read_rpm_header(blob, off):
    assert blob[off:off + 3] == b"\x8e\xad\xe8", "头 magic 不对"
    nindex, hsize = struct.unpack(">II", blob[off + 8:off + 16])
    idx = [struct.unpack(">IIII", blob[off + 16 + 16 * i:off + 32 + 16 * i]) for i in range(nindex)]
    store = blob[off + 16 + 16 * nindex: off + 16 + 16 * nindex + hsize]
    total = 16 + 16 * nindex + hsize
    return idx, store, off + total + ((-total) % 8), total


def _hdr_get(idx, store, tag, typ):
    for t, ty, o, c in idx:
        if t == tag:
            if typ == "s":
                end = store.index(b"\x00", o)
                return store[o:end].decode("utf-8")
            if typ == "sa":
                out, cur = [], o
                for _ in range(c):
                    end = store.index(b"\x00", cur)
                    out.append(store[cur:end].decode("utf-8"))
                    cur = end + 1
                return out
            if typ == "i32":
                return list(struct.unpack(">%di" % c, store[o:o + 4 * c]))
            if typ == "i16":
                return list(struct.unpack(">%dH" % c, store[o:o + 2 * c]))
            if typ == "bin":
                return store[o:o + c]
    raise KeyError(tag)


def _cpio_parse(blob):
    out = []
    off = 0
    while off + 110 <= len(blob):
        assert blob[off:off + 6] == b"070701", "cpio magic 不对 @%d" % off
        f = [int(blob[off + 6 + 8 * i:off + 14 + 8 * i], 16) for i in range(13)]
        nsize, size = f[11], f[6]
        name = blob[off + 110:off + 110 + nsize - 1].decode("utf-8")
        off += 110 + nsize
        off += (-off) % 4
        data = blob[off:off + size]
        off += size
        off += (-off) % 4
        if name == "TRAILER!!!":
            break
        out.append((name, f[1], size, data))
    return out


def verify_rpm(path):
    with open(path, "rb") as f:
        blob = f.read()
    assert blob[:4] == RPM_LEAD_MAGIC, "lead magic 不对"
    assert blob[4:6] == b"\x03\x00", "lead 版本不对"
    sidx, sstore, moff, _stot = _read_rpm_header(blob, 96)
    midx, mstore, poff, mtot = _read_rpm_header(blob, moff)
    main_blob = blob[moff:moff + mtot]
    mainpad_len = poff - moff
    payload = blob[poff:]
    assert _hdr_get(sidx, sstore, SIG_SHA1, "s") == hashlib.sha1(main_blob).hexdigest(), "SHA1 不符"
    assert _hdr_get(sidx, sstore, SIG_SHA256, "s") == hashlib.sha256(main_blob).hexdigest(), "SHA256 不符"
    assert _hdr_get(sidx, sstore, SIG_SIZE, "i32")[0] == mainpad_len + len(payload), "SIZE 不符"
    assert _hdr_get(sidx, sstore, SIG_MD5, "bin") == hashlib.md5(blob[moff:poff] + payload).digest(), "MD5 不符"
    assert _hdr_get(sidx, sstore, SIG_PAYLOADSIZE, "i32")[0] == len(gzip.decompress(payload)), "载荷长度不符"
    assert _hdr_get(midx, mstore, T_PAYLOADDIGEST, "sa")[0] == _sha256b(payload), "PAYLOADDIGEST 不符"

    assert midx[0][0] == T_HEADERIMMUTABLE, "主头第 0 项不是区域标签"
    mval = mstore[midx[0][2]:midx[0][2] + 16]
    assert struct.unpack(">IIiI", mval) == (T_HEADERIMMUTABLE, T_BIN, -(len(midx) * 16), 16), mval.hex()
    assert sidx[0][0] == SIG_HEADERSIGNATURES, "签名头第 0 项不是区域标签"

    names = _hdr_get(midx, mstore, T_BASENAMES, "sa")
    dirs = _hdr_get(midx, mstore, T_DIRNAMES, "sa")
    di = _hdr_get(midx, mstore, T_DIRINDEXES, "i32")
    sizes = _hdr_get(midx, mstore, T_FILESIZES, "i32")
    modes = _hdr_get(midx, mstore, T_FILEMODES, "i16")
    digs = _hdr_get(midx, mstore, T_FILEDIGESTS, "sa")
    paths = [dirs[di[i]] + names[i] for i in range(len(names))]

    cps = _cpio_parse(gzip.decompress(payload))
    assert len(cps) == len(paths), (len(cps), len(paths))
    for i, (cpname, _cpmode, cpsize, cpdata) in enumerate(cps):
        want = "./" + paths[i].strip("/")
        if modes[i] & 0o40000:
            want += "/"
            assert cpname == want, (i, cpname, want)
        else:
            assert cpname == want, (i, cpname, want)
            assert cpsize == sizes[i], (i, cpsize, sizes[i])
            assert _sha256b(cpdata) == digs[i], (i, cpname)
    return {
        "name": _hdr_get(midx, mstore, T_NAME, "s"),
        "version": _hdr_get(midx, mstore, T_VERSION, "s"),
        "release": _hdr_get(midx, mstore, T_RELEASE, "s"),
        "arch": _hdr_get(midx, mstore, T_ARCH, "s"),
        "files": len(paths), "first": paths[0], "last": paths[-1],
        "payload_uncompressed": len(gzip.decompress(payload)),
    }


# ======================================================================
# 主流程
# ======================================================================

DEB_ARCH = {"x64": "amd64", "arm64": "arm64"}
RPM_ARCH = {"x64": ("x86_64", 1), "arm64": ("aarch64", 19)}
DEPENDS = "libwebkit2gtk-4.1-0, libgtk-3-0, libnotify4"

SUMMARY = "deepsleep - AI assistant that can edit files, run commands and search the web"
DESCRIPTION = """deepsleep is a cross-platform AI assistant for your desktop.
It can read and write files, run shell commands, search the web, do deep
research, keep long term memory, load skills, and it ships a built-in
remote desktop called SuperLink (pair with a 6 digit code).
Linux builds need WebKitGTK: install libwebkit2gtk-4.1-0 and libgtk-3-0."""


def main():
    ap = argparse.ArgumentParser(description="deepsleep Linux 打包（deb / rpm）")
    ap.add_argument("--src", required=True, help="发布目录（里面是 deepsleep / ui / web / ...）")
    ap.add_argument("--out", required=True, help="输出目录")
    ap.add_argument("--version", required=True)
    ap.add_argument("--arch", required=True, choices=["x64", "arm64"])
    ap.add_argument("--release", default="1")
    ap.add_argument("--icon", default="")
    ap.add_argument("--keep-temp", action="store_true")
    args = ap.parse_args()

    if not os.path.isdir(args.src):
        print("× 找不到发布目录：" + args.src)
        return 1
    os.makedirs(args.out, exist_ok=True)
    tmp = os.path.join(args.out, ".mklinux-" + args.arch)
    os.makedirs(tmp, exist_ok=True)

    icon_png = b""
    if args.icon and os.path.isfile(args.icon):
        png_path = os.path.join(tmp, "deepsleep.png")
        if _png_from_ico(args.icon, png_path):
            with open(png_path, "rb") as f:
                icon_png = f.read()
    if not icon_png:
        print("！ 没能从 ico 里取到图标，包里不带图标（不影响使用）")

    items = _stage(args.src, icon_png)
    darch = DEB_ARCH[args.arch]
    rarch, archnum = RPM_ARCH[args.arch]
    deb = os.path.join(args.out, "deepsleep_%s_%s.deb" % (args.version, darch))
    rpm = os.path.join(args.out, "deepsleep-%s-%s.%s.rpm" % (args.version, args.release, rarch))

    pack_deb(items, deb, args.version, darch, DEPENDS, SUMMARY, DESCRIPTION, tmp)
    pack_rpm(items, rpm, args.version, args.release, rarch, archnum, SUMMARY, DESCRIPTION, 0, tmp)

    v1 = verify_deb(deb)
    v2 = verify_rpm(rpm)
    print("   deb %-38s %7.2f MB  %s" % (os.path.basename(deb), os.path.getsize(deb) / 1048576.0, _sha256_file(deb)[:16]))
    print("       包名 %s %s %s · %d 个文件 · 可执行：%s" % (v1["package"], v1["version"], v1["arch"], v1["files"], ", ".join(v1["executables"])))
    print("   rpm %-38s %7.2f MB  %s" % (os.path.basename(rpm), os.path.getsize(rpm) / 1048576.0, _sha256_file(rpm)[:16]))
    print("       NVRA %s-%s-%s.%s · %d 项 · %s .. %s" % (v2["name"], v2["version"], v2["release"], v2["arch"],
                                                          v2["files"], v2["first"], v2["last"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
