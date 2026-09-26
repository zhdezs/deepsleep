# -*- coding: utf-8 -*-
r"""deepsleep 发布到 Gitee（zhdezs/deepsleep）：源码同步 + Release 附件

和 GitHub 那套（publish-via-api.py）的差别，以下都是实测结论：
  · Gitee **没有** git/blobs、git/trees、git/commits 的写接口，也没有批量提交
    （POST /contents 批量表单返回 405）→ 源码只能一个文件一个文件地
    POST / PUT / DELETE /contents/<path>，所以先读 `/git/trees/master?recursive=1`
    比对 git blob sha，只传有变化的文件
  · 单个附件不能超过 100MB → 132MB 的安装包在 Gitee 上必须切开上传
    （deepsleep-Setup.exe.part1 / .part2 …），客户端会逐片下载后拼回整包再校验
  · Release 附件走 multipart/form-data（字段名 file），源码/接口用 access_token
  · 默认分支是 master（不是 main）

用法：
  python publish-gitee.py --dry-run                    # 只列出要传什么、怎么切片
  python publish-gitee.py --all                        # 同步源码 + 建 Release + 传附件
  python publish-gitee.py --release --notes "更新说明"
  python publish-gitee.py --source
令牌：环境变量 DS_GITEE_TOKEN（推荐，配合 .secrets\publish-gitee.ps1），或 --token / --token-env。
版本号自动读 src\TrollWrangler.csproj；附件取 release\deepsleep-Setup.exe（按需切片）与
release\deepsleep-<版本>-win-x64.zip。
"""
import argparse, base64, hashlib, http.client, json, os, re, shutil, ssl, sys, tempfile, time, urllib.error, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))          # <root>\release\tools
ROOT = os.path.dirname(os.path.dirname(HERE))              # <root>
REPO_DEFAULT = "zhdezs/deepsleep"
HOST = "gitee.com"
API = "https://gitee.com/api/v5"
BRANCH = "master"
SKIP_DIRS = {"bin", "obj", "dist", "publish", "package", ".git", "data", "__pycache__"}
SKIP_NAMES = {"payload.zip"}
SKIP_EXT = (".zip", ".exe", ".pdb", ".dll")
PART_SIZE = 90 * 1024 * 1024        # 单个附件上限 100MB，留足余量
TOKEN = ""
REPO = REPO_DEFAULT


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


def api(method, path, body=None, query=None, raw_body=None, content_type=None):
    """调 Gitee OpenAPI（令牌只走 query，不落日志）。"""
    q = dict(query or {})
    q["access_token"] = TOKEN
    url = "%s/repos/%s%s?%s" % (API, REPO, path, urllib.parse.urlencode(q))
    data = raw_body if raw_body is not None else (
        json.dumps(body).encode("utf-8") if body is not None else None)
    headers = {"User-Agent": "deepsleep-publish"}
    if content_type:
        headers["Content-Type"] = content_type
    elif data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            payload = r.read()
            return r.status, (json.loads(payload) if payload.strip() else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")
    except Exception as e:
        return "ERR", str(e)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def git_blob_sha(data):
    """本地算 git blob sha，用来和远端树的 sha 比差异（Gitee 返回的就是这个）。"""
    h = hashlib.sha1()
    h.update(b"blob %d\0" % len(data))
    h.update(data)
    return h.hexdigest()


def source_files():
    """仓库范围：src / ui / installer / release + 根目录 README.md、.gitignore。

    注意：AGENTS.md **不进仓库**（只留本地，属于 AI 的开发约定），别加回来。
    """
    out = {}
    for rel in ("README.md", ".gitignore", "index.html"):
        p = os.path.join(ROOT, rel)
        if os.path.isfile(p):
            out[rel] = p
    for top in ("src", "ui", "installer", "release", "web"):
        for dirpath, dirnames, filenames in os.walk(os.path.join(ROOT, top)):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for fn in filenames:
                if fn in SKIP_NAMES or os.path.splitext(fn)[1].lower() in SKIP_EXT:
                    continue
                full = os.path.join(dirpath, fn)
                out[os.path.relpath(full, ROOT).replace("\\", "/")] = full
    return out


def tracked(rel):
    return (rel in ("README.md", ".gitignore", "index.html")
            or rel.split("/")[0] in ("src", "ui", "installer", "release", "web"))


def split_installer(path):
    """把安装包切成 <= PART_SIZE 的片（Gitee 单附件上限 100MB）；返回 [(名字, 路径, 字节数)]。"""
    size = os.path.getsize(path)
    base = os.path.basename(path)
    if size <= PART_SIZE:
        return [(base, path, size)]
    tmp = tempfile.mkdtemp(prefix="deepsleep-gitee-parts-")
    parts, index, left = [], 1, size
    with open(path, "rb") as src:
        while left > 0:
            name = "%s.part%d" % (base, index)
            target = os.path.join(tmp, name)
            take = min(PART_SIZE, left)
            with open(target, "wb") as dst:
                while take > 0:
                    chunk = src.read(min(1 << 20, take))
                    if not chunk:
                        break
                    dst.write(chunk)
                    take -= len(chunk)
            parts.append((name, target, os.path.getsize(target)))
            left -= os.path.getsize(target)
            index += 1
    return parts


def planned_assets(version):
    cands = [os.path.join(ROOT, "release", "deepsleep-Setup.exe"),
             os.path.join(ROOT, "release", "deepsleep-%s-win-x64.zip" % version),
             os.path.join(ROOT, "release", "deepsleep-core-%s-win-x64.zip" % version)]
    plan = []
    for p in cands:
        if os.path.isfile(p):
            plan.append((os.path.basename(p), p, os.path.getsize(p)))   # 整包：和切片一起上传
            parts = split_installer(p)
            if len(parts) > 1:
                plan.extend(parts)                                      # 超限时再补切片（客户端优先用整包）
    return plan


# --------------------------------------------------------------- 源码同步
def remote_tree():
    st, d = api("GET", "/git/trees/" + BRANCH, None, {"recursive": 1})
    if st != 200 or not isinstance(d, dict):
        if st != 404:
            print("读远端树失败：HTTP %s %s" % (st, str(d)[:200]))
        return {}
    out = {}
    for e in d.get("tree", []) or []:
        if e.get("type") in (None, "blob") and e.get("mode") != "040000" and e.get("type") != "tree":
            out[e["path"]] = e.get("sha") or ""
    return out


def do_source(version):
    remote = remote_tree()
    files = source_files()
    uploads, deletes, local = [], [], {}
    for rel, full in sorted(files.items()):
        with open(full, "rb") as f:
            data = f.read()
        digest = git_blob_sha(data)
        local[rel] = digest
        if remote.get(rel) != digest:
            uploads.append((rel, full, data))
    for rel in sorted(remote):
        if rel not in local and tracked(rel):
            deletes.append(rel)

    if not remote and len(local) > 5:
        print("（提示：远端树读回来是空的，如果仓库里其实有文件，八成是 git/trees 调用出问题了）")
    print("源码：本地 %d 个文件，远端 %d 个；要传 %d、要删 %d"
          % (len(local), len(remote), len(uploads), len(deletes)))
    if not uploads and not deletes:
        print("源码已是最新，跳过")
        return True

    done = 0
    for rel, full, data in uploads:
        body = {
            "content": base64.b64encode(data).decode("ascii"),
            "message": "release: %s (%s)" % (version, rel),
            "branch": BRANCH,
        }
        method = "POST"
        if rel in remote:
            method, body["sha"] = "PUT", remote[rel]
        st, res = api(method, "/contents/" + urllib.parse.quote(rel), body)
        if st == 400 and method == "POST" and "已存在" in str(res):
            # 树没读全 / 别处刚推过 → 取远端 sha 改成 PUT 更新
            st2, cur = api("GET", "/contents/" + urllib.parse.quote(rel), None, {"ref": BRANCH})
            sha = cur.get("sha") if isinstance(cur, dict) else None
            if sha:
                body["sha"] = sha
                st, res = api("PUT", "/contents/" + urllib.parse.quote(rel), body)
        if st not in (200, 201):
            print("  上传失败 %s：HTTP %s %s" % (rel, st, str(res)[:200]))
            return False
        done += 1
        if done % 20 == 0 or done == len(uploads):
            print("  已传 %d/%d" % (done, len(uploads)))

    for rel in deletes:
        st, res = api("DELETE", "/contents/" + urllib.parse.quote(rel), None,
                      {"sha": remote.get(rel, ""), "branch": BRANCH,
                       "message": "release: %s (删掉已废弃的 %s)" % (version, rel)})
        if st not in (200, 201):
            print("  删除失败 %s：HTTP %s %s" % (rel, st, str(res)[:200]))
            return False
        print("  已删除远端多余文件 %s" % rel)
    print("源码已同步到 Gitee（%s 分支）" % BRANCH)
    return True


# ----------------------------------------------------------------- Release
def reset_release(release_id):
    """Gitee 的附件 JSON 里**没有 id**（只有 name + 下载地址），删不掉单个附件，
    而且同名附件不会覆盖、只会并存（重发会出现两份 part1，客户端可能拿到旧的）。
    所以重发时的办法是：把整个 Release 删掉再重建，标签 vX.Y.Z 会保留。"""
    st, res = api("DELETE", "/releases/%s" % release_id)
    print("  删掉旧 Release 以便重发（HTTP %s）%s" % (st, "" if st in (200, 204) else str(res)[:200]))
    return st in (200, 204)


def upload_asset(release_id, path, name):
    """multipart/form-data 流式上传（132MB 不能先读进内存）。"""
    boundary = "----deepsleepgitee%d" % int(time.time() * 1000)
    head = ("--%s\r\nContent-Disposition: form-data; name=\"file\"; filename=\"%s\"\r\n"
            "Content-Type: application/octet-stream\r\n\r\n" % (boundary, name)).encode("utf-8")
    tail = ("\r\n--%s--\r\n" % boundary).encode("utf-8")
    size = os.path.getsize(path)
    url = "/api/v5/repos/%s/releases/%s/attach_files?%s" % (
        REPO, release_id, urllib.parse.urlencode({"access_token": TOKEN}))
    conn = http.client.HTTPSConnection(HOST, timeout=1800, context=ssl.create_default_context())
    conn.putrequest("POST", url)
    conn.putheader("User-Agent", "deepsleep-publish")
    conn.putheader("Content-Type", "multipart/form-data; boundary=%s" % boundary)
    conn.putheader("Content-Length", str(len(head) + size + len(tail)))
    conn.endheaders()
    conn.send(head)
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
                print("    %s %.1f%%  (%.1f/%.1f MB, %.1f MB/s)"
                      % (name, sent * 100.0 / size, sent / 1048576.0, size / 1048576.0,
                         sent / 1048576.0 / max(0.001, now - started)))
    conn.send(tail)
    resp = conn.getresponse()
    body = resp.read().decode("utf-8", "replace")
    conn.close()
    if resp.status not in (200, 201):
        print("上传失败 %s：HTTP %s %s" % (name, resp.status, body[:300]))
        return False
    try:
        res = json.loads(body)
    except Exception:
        res = {}
    got = res.get("size")
    url_out = res.get("browser_download_url") or res.get("download_url") or ""
    print("✓ %s  %.1f MB  本地字节 %d / Gitee 记录 %s  %s"
          % (name, size / 1048576.0, size, got, "一致 ✓" if got in (None, size) else "★ 不一致 ★"))
    print("  本地 SHA256 %s" % sha256(path))
    if url_out:
        print("  地址 %s" % url_out)
    return got in (None, size)


def do_release(version, notes):
    tag = "v" + version
    plan = planned_assets(version)
    if not plan:
        print("release\\ 下没找到安装包（先编译再发布）")
        return False
    want = {name for name, _p, _s in plan}

    # 把安装包的 SHA256 写进 Gitee Release 说明：客户端"强制走 Gitee"时靠它自校验，
    # 整条更新链就不依赖 GitHub API 了（Gitee 的附件 JSON 里没有 size/digest 字段）。
    installer = os.path.join(ROOT, "release", "deepsleep-Setup.exe")
    if os.path.isfile(installer):
        notes = (notes or "").rstrip() + "\n\nSHA256: " + sha256(installer)
        print("  已把安装包 SHA256 写进 Release 说明（供客户端校验）")

    st, rel = api("GET", "/releases/tags/" + tag)
    if st == 200 and isinstance(rel, dict) and rel.get("id"):
        have = [a.get("name") for a in (rel.get("assets") or [])]
        if want & set(have):
            print("Gitee Release %s 上已经有同名附件了，删掉重建（避免新旧两份并存）" % tag)
            reset_release(rel["id"])
            rel = None
        else:
            print("复用已有 Gitee Release：%s" % rel.get("html_url", tag))
            api("PATCH", "/releases/%s" % rel["id"],
                {"name": "deepsleep %s" % version, "body": notes, "target_commitish": BRANCH})
    else:
        rel = None
    if rel is None:
        body = {"tag_name": tag, "name": "deepsleep %s" % version, "body": notes,
                "target_commitish": BRANCH, "prerelease": False}
        st, rel = api("POST", "/releases", body)
        if st not in (200, 201):
            st, rel = api("POST", "/releases", None,
                          raw_body=urllib.parse.urlencode(body).encode("utf-8"),
                          content_type="application/x-www-form-urlencoded")
        if st not in (200, 201) or not isinstance(rel, dict):
            print("建 Release 失败：HTTP %s %s" % (st, str(rel)[:300]))
            return False
        print("Gitee Release 已创建：%s" % rel.get("html_url", tag))

    ok = True
    for name, path, size in plan:
        if not upload_asset(rel["id"], path, name):
            if size > PART_SIZE and ".part" not in name:
                # Gitee 单附件上限 100MB：整包可能被拒，切片在就还能更新，不算致命
                print("  警告：整包 %s（%.1f MB）上传失败，已保留切片" % (name, size / 1048576.0))
                continue
            ok = False
    return ok


def main():
    global TOKEN, REPO
    ap = argparse.ArgumentParser()
    ap.add_argument("--token")
    ap.add_argument("--token-env", default="DS_GITEE_TOKEN")
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
        print("版本 %s → Gitee 仓库 %s（分支 %s）" % (version, REPO, BRANCH))
        for name, path, size in planned_assets(version):
            print("  附件 %-34s %8.1f MB" % (name, size / 1048576.0))
        print("  源码 %d 个文件" % len(source_files()))
        return 0

    TOKEN = (a.token or os.environ.get(a.token_env) or "").strip()
    if not TOKEN:
        print("没有令牌：用 --token，或先把令牌放进环境变量 %s（推荐 .secrets\\publish-gitee.ps1）" % a.token_env)
        return 2
    ok = True
    if a.all or a.source:
        ok = do_source(version) and ok
    if a.all or a.release:
        ok = do_release(version, notes) and ok
    print("完成" if ok else "有失败项，见上面输出")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())