# -*- coding: utf-8 -*-
r"""deepsleep 版本发布（走 GitHub REST API；沙箱里 git/schannel TLS 不可用，所以用 Python 的 HTTPS）

用法：
  python publish-via-api.py --dry-run                     # 只列出要传什么
  python publish-via-api.py --all                         # 建 Release + 传包 + 同步源码
  python publish-via-api.py --release --notes "更新说明"
  python publish-via-api.py --source
令牌来源：环境变量 DS_GH_TOKEN（推荐，配合 .secrets\publish.ps1 用），或 --token / --token-env。
版本号自动读 src\TrollWrangler.csproj；资产取 release\deepsleep-Setup.exe 与 release\deepsleep-<版本>-win-x64.zip。
"""
import argparse, base64, hashlib, http.client, json, os, re, ssl, sys, time, urllib.error, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))          # <root>\release\tools
ROOT = os.path.dirname(os.path.dirname(HERE))              # <root>
REPO_DEFAULT = "zhdezs/deepsleep"
API = "https://api.github.com"
TOKEN = ""
REPO = REPO_DEFAULT
SKIP_DIRS = {"bin", "obj", "dist", "publish", "package", ".git", "data", "__pycache__"}
SKIP_NAMES = {"payload.zip"}


def current_version():
    """版本号来源：src/Directory.Build.props（外壳与内核共用同一份，改一处即可）。
    老仓库没有这个文件时回退到 TrollWrangler.csproj。"""
    for rel in (os.path.join("src", "Directory.Build.props"),
                os.path.join("src", "TrollWrangler.csproj")):
        p = os.path.join(ROOT, rel)
        if not os.path.isfile(p):
            continue
        with open(p, encoding="utf-8") as f:
            m = re.search(r"<Version>([^<]+)</Version>", f.read())
        if m:
            return m.group(1).strip()
    return "0.0.0"


def api(method, url, body=None):
    headers = {"User-Agent": "deepsleep-publish", "Accept": "application/vnd.github+json"}
    if TOKEN:
        headers["Authorization"] = "Bearer " + TOKEN
    data = json.dumps(body).encode("utf-8") if body is not None else None
    # 跨境链路偶尔整段连不上（WinError 10060 / 连接被重置），重试几次再判失败。
    last = (0, "未发起")
    for attempt in range(4):
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=300) as r:
                payload = r.read()
                return r.status, (json.loads(payload) if payload else None)
        except urllib.error.HTTPError as e:
            code, text = e.code, e.read().decode("utf-8", "replace")
            if code >= 500 and attempt < 3:
                last = (code, text)
                print("  GitHub 接口 %s %s（HTTP %s），%d 秒后重试" % (method, url.split("?")[0], code, 4 * (attempt + 1)))
                time.sleep(4 * (attempt + 1))
                continue
            return code, text
        except Exception as e:
            last = (0, "网络错误：" + repr(e))
            if attempt < 3:
                print("  GitHub 接口 %s %s 连不上（%s），%d 秒后重试" % (method, url.split("?")[0], e, 4 * (attempt + 1)))
                time.sleep(4 * (attempt + 1))
                continue
    return last


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def source_files():
    """按仓库范围收集源码：src / ui / installer / release + 根目录 README.md 与 .gitignore。

    注意：AGENTS.md **不进仓库**（只留本地，属于 AI 的开发约定），别加回来。
    web/ 是官网 + 网页版（GitHub Pages 直接用），根目录 index.html 负责跳转到 web/；
    superlink/ 是 zhdezs.github.io/deepsleep/superlink/?type=配对码 的跳转页（转给 web/superlink/）。
    """
    out = {}
    for rel in ("README.md", ".gitignore", "index.html"):
        p = os.path.join(ROOT, rel)
        if os.path.isfile(p):
            out[rel] = p
    for top in ("src", "ui", "installer", "release", "web", "superlink"):
        for dirpath, dirnames, filenames in os.walk(os.path.join(ROOT, top)):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for fn in filenames:
                if fn in SKIP_NAMES or os.path.splitext(fn)[1].lower() in (".zip", ".exe", ".pdb", ".dll", ".gz", ".tar"):
                    continue
                full = os.path.join(dirpath, fn)
                out[os.path.relpath(full, ROOT).replace("\\", "/")] = full
    return out


# 跨平台桌面版包（Linux / macOS，同一个包 --headless 即内核版）：整包一起推，和 Windows 三个产物走同一套上传
UNIX_RIDS = ["linux-x64", "linux-arm64", "osx-arm64", "osx-x64"]


def assets_for(version):
    cands = [os.path.join(ROOT, "release", "deepsleep-Setup.exe"),
             os.path.join(ROOT, "release", "deepsleep-%s-win-x64.zip" % version),
             os.path.join(ROOT, "release", "deepsleep-core-%s-win-x64.zip" % version)]
    for rid in UNIX_RIDS:
        cands.append(os.path.join(ROOT, "release", "deepsleep-%s-%s.tar.gz" % (version, rid)))
    return [p for p in cands if os.path.isfile(p)]


# ----------------------------------------------------------------- Release
def upload_asset(rel, path):
    """流式上传：Content-Length + 分块 send，跨境慢链路也不会整体超时。"""
    name = os.path.basename(path)
    size = os.path.getsize(path)
    local = sha256(path)
    st, assets = api("GET", "%s/repos/%s/releases/%d/assets" % (API, REPO, rel["id"]))
    if st == 200:
        for a in assets:
            if a["name"] == name:
                digest = (a.get("digest") or "").split(":")[-1].lower()
                if digest and digest == local.lower() and a.get("size") == size:
                    print("✓ %s 已在 Release 上、SHA256 也一致，跳过重复上传（%.1f MB）"
                          % (name, size / 1048576.0))
                    return True
                print("  删除同名旧资产：%s" % name)
                api("DELETE", "%s/repos/%s/releases/assets/%d" % (API, REPO, a["id"]))

    url = "/repos/%s/releases/%d/assets?name=%s" % (REPO, rel["id"], urllib.parse.quote(name))

    def _send():
        conn = http.client.HTTPSConnection("uploads.github.com", timeout=1800,
                                           context=ssl.create_default_context())
        conn.putrequest("POST", url)
        conn.putheader("Authorization", "Bearer " + TOKEN)
        conn.putheader("User-Agent", "deepsleep-publish")
        conn.putheader("Accept", "application/vnd.github+json")
        conn.putheader("Content-Type", "application/octet-stream")
        conn.putheader("Content-Length", str(size))
        conn.endheaders()
        sent, started, last = 0, time.time(), 0.0
        with open(path, "rb") as f:
            while True:
                chunk = f.read(1 << 20)
                if not chunk:
                    break
                conn.send(chunk)
                sent += len(chunk)
                now = time.time()
                if now - last > 5:
                    last = now
                    print("    %s %.1f%%  (%.1f/%.1f MB)" % (name, sent * 100.0 / size,
                                                             sent / 1048576.0, size / 1048576.0))
        resp = conn.getresponse()
        body = resp.read().decode("utf-8", "replace")
        conn.close()
        return resp.status, body

    status, body = 0, ""
    for attempt in range(3):
        try:
            status, body = _send()
            if status not in (200, 201) and status >= 500 and attempt < 2:
                print("  上传 %s 返回 HTTP %s，%d 秒后重试" % (name, status, 6 * (attempt + 1)))
                time.sleep(6 * (attempt + 1))
                continue
            break
        except Exception as e:
            print("  上传 %s 第 %d 次中断：%s" % (name, attempt + 1, e))
            if attempt == 2:
                print("上传失败 %s：网络一直不通" % name)
                return False
            time.sleep(6 * (attempt + 1))
    if status not in (200, 201):
        print("上传失败 %s：HTTP %s %s" % (name, status, body[:400]))
        return False
    res = json.loads(body)
    local = sha256(path)
    digest = (res.get("digest") or "").split(":")[-1]
    same = digest.lower() == local.lower()
    print("✓ %s  %.1f MB" % (name, size / 1048576.0))
    print("  本地 SHA256  %s" % local)
    print("  GitHub 返回  %s  →  %s" % (digest or "(无 digest)", "一致 ✓" if same else "★ 不一致 ★"))
    return same


def do_release(version, notes):
    tag = "v" + version
    st, rel = api("GET", "%s/repos/%s/releases/tags/%s" % (API, REPO, tag))
    if st == 200:
        print("复用已有 Release：%s" % rel["html_url"])
        api("PATCH", "%s/repos/%s/releases/%d" % (API, REPO, rel["id"]),
            {"name": "deepsleep %s" % version, "body": notes, "draft": False, "prerelease": False})
    else:
        st, rel = api("POST", "%s/repos/%s/releases" % (API, REPO),
                      {"tag_name": tag, "name": "deepsleep %s" % version, "body": notes,
                       "draft": False, "prerelease": False, "target_commitish": "main"})
        if st not in (201, 200):
            print("创建 Release 失败：HTTP %s %s" % (st, rel))
            return False
        print("Release 已创建：%s" % rel["html_url"])

    files = assets_for(version)
    if not files:
        print("release\\ 下没找到安装包（先编译再发布）")
        return False
    ok = True
    for p in files:
        ok = upload_asset(rel, p) and ok
    return ok


# ----------------------------------------------------------------- 源码同步
def do_source(version):
    st, ref = api("GET", "%s/repos/%s/git/ref/heads/main" % (API, REPO))
    if st != 200:
        print("读 main 分支失败：HTTP %s %s" % (st, ref))
        return False
    parent = ref["object"]["sha"]
    files = source_files()
    print("待同步 %d 个文件（父提交 %s）" % (len(files), parent[:8]))
    tree = []
    for rel, full in sorted(files.items()):
        with open(full, "rb") as f:
            data = f.read()
        st, blob = api("POST", "%s/repos/%s/git/blobs" % (API, REPO),
                       {"content": base64.b64encode(data).decode("ascii"), "encoding": "base64"})
        if st != 201:
            print("上传 blob 失败 %s：HTTP %s %s" % (rel, st, str(blob)[:300]))
            return False
        tree.append({"path": rel, "mode": "100644", "type": "blob", "sha": blob["sha"]})
    st, new_tree = api("POST", "%s/repos/%s/git/trees" % (API, REPO), {"tree": tree})
    if st != 201:
        print("建 tree 失败：HTTP %s %s" % (st, str(new_tree)[:300]))
        return False
    st, newc = api("POST", "%s/repos/%s/git/commits" % (API, REPO),
                   {"message": "release: %s" % version, "tree": new_tree["sha"], "parents": [parent]})
    if st != 201:
        print("建 commit 失败：HTTP %s %s" % (st, str(newc)[:300]))
        return False
    st, res = api("PATCH", "%s/repos/%s/git/refs/heads/main" % (API, REPO),
                  {"sha": newc["sha"], "force": False})
    if st not in (200, 201):
        print("更新 main 失败：HTTP %s %s" % (st, str(res)[:300]))
        return False
    print("源码已推送：%s" % newc["html_url"])
    return True


def main():
    global TOKEN, REPO
    ap = argparse.ArgumentParser()
    ap.add_argument("--token")
    ap.add_argument("--token-env", default="DS_GH_TOKEN")
    ap.add_argument("--repo", default=REPO_DEFAULT)
    ap.add_argument("--notes", default="")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--release", action="store_true")
    ap.add_argument("--source", action="store_true")
    ap.add_argument("--all", action="store_true")
    a = ap.parse_args()
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")   # Windows 管道下默认 GBK，会崩
    except Exception:
        pass
    REPO = a.repo
    version = current_version()
    notes = a.notes or "deepsleep %s" % version

    if a.dry_run:
        files = source_files()
        print("版本 %s → 仓库 %s" % (version, REPO))
        for p in assets_for(version):
            print("  资产 %s  %.1f MB" % (os.path.basename(p), os.path.getsize(p) / 1048576.0))
        print("  源码 %d 个文件" % len(files))
        return 0

    TOKEN = (a.token or os.environ.get(a.token_env) or "").strip()
    if not TOKEN:
        print("没有令牌：用 --token，或先把令牌放进环境变量 %s（推荐 .secrets\\publish.ps1）" % a.token_env)
        return 2
    ok = True
    if a.all or a.release:
        ok = do_release(version, notes) and ok
    if a.all or a.source:
        ok = do_source(version) and ok
    print("完成" if ok else "有失败项，见上面输出")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
