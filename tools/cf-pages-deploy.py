# -*- coding: utf-8 -*-
r"""把 web/ 目录直接部署到 Cloudflare Pages（Direct Upload）。

为什么要它：GitHub Pages 一个站点只能绑一个域名，绑了自定义域名就会把 github.io 301 过去。
想让「旧域名 github.io 直出 + 新域名 liziserver.site 直出」两边都独立可用，
就得把新域名放到 Cloudflare Pages 上托管。

用法：
    set CF_API_TOKEN=xxxx            # Windows cmd
    export CF_API_TOKEN=xxxx         # bash
    python tools\cf-pages-deploy.py                    # 只部署，返回 pages.dev 地址
    python tools\cf-pages-deploy.py --domain deepsleep.liziserver.site   # 顺便绑自定义域名

需要的 Token 权限（Cloudflare Dashboard → 我的个人资料 → API 令牌 → 创建令牌）：
    · 账户 → Cloudflare Pages → 编辑
    · 区域 (liziserver.site) → DNS → 编辑     （绑自定义域名时自动建记录用）
    · 区域 → 区域 → 读取                      （列 zone 用）
"""
import argparse, json, mimetypes, os, sys, urllib.error, urllib.parse, urllib.request

API = "https://api.cloudflare.com/client/v4"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SITE = os.path.join(ROOT, "web")

# Cloudflare 的 100MB 单文件/2500 文件上限跟我们这个小站无关，但排除掉明显不该上的东西
SKIP = {".DS_Store", "Thumbs.db"}


def call(token, method, path, body=None, raw=None, ctype=None):
    url = API + path
    data = None
    headers = {"Authorization": "Bearer " + token}
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    elif raw is not None:
        data = raw
        headers["Content-Type"] = ctype
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.status, json.loads(r.read().decode() or "{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read().decode() or "{}")
        except Exception:
            return e.code, {}


def files_of(dirpath):
    out = []
    for dp, _, fns in os.walk(dirpath):
        for fn in fns:
            if fn in SKIP:
                continue
            full = os.path.join(dp, fn)
            rel = os.path.relpath(full, dirpath).replace("\\", "/")
            out.append((rel, full))
    return sorted(out)


def multipart(files, branch):
    boundary = "----deepsleeppages%d" % os.getpid()
    chunks = []

    def field(name, value):
        chunks.append(("--%s\r\nContent-Disposition: form-data; name=\"%s\"\r\n\r\n%s\r\n"
                       % (boundary, name, value)).encode())

    field("branch", branch)
    for rel, full in files:
        mime = mimetypes.guess_type(full)[0] or "application/octet-stream"
        chunks.append(("--%s\r\nContent-Disposition: form-data; name=\"%s\"; filename=\"%s\"\r\n"
                       "Content-Type: %s\r\n\r\n"
                       % (boundary, rel, os.path.basename(rel), mime)).encode())
        with open(full, "rb") as f:
            chunks.append(f.read())
        chunks.append(b"\r\n")
    chunks.append(("--%s--\r\n" % boundary).encode())
    return b"".join(chunks), "multipart/form-data; boundary=" + boundary


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--token", default=os.environ.get("CF_API_TOKEN", ""))
    ap.add_argument("--account", default=os.environ.get("CF_ACCOUNT_ID", ""))
    ap.add_argument("--project", default="deepsleep")
    ap.add_argument("--dir", default=SITE)
    ap.add_argument("--domain", default="", help="要绑的自定义域名，例如 deepsleep.liziserver.site")
    ap.add_argument("--branch", default="main")
    a = ap.parse_args()

    if not a.token:
        print("缺少 CF_API_TOKEN。去 Cloudflare → 我的个人资料 → API 令牌 建一个，"
              "权限要 Account/Cloudflare Pages:Edit（绑域名时再加 Zone/DNS:Edit）。")
        return 2
    if not os.path.isdir(a.dir):
        print("站点目录不存在：" + a.dir)
        return 2

    acct = a.account
    if not acct:
        st, res = call(a.token, "GET", "/accounts?per_page=50")
        if st != 200 or not res.get("success"):
            print("取账户失败 HTTP %s：%s" % (st, str(res)[:300]))
            return 1
        items = res.get("result") or []
        if not items:
            print("这个 Token 看不到任何账户（权限不足？）")
            return 1
        acct = items[0]["id"]
        print("账户 %s (%s)" % (items[0].get("name"), acct))

    # 项目不存在就建
    st, res = call(a.token, "GET", "/accounts/%s/pages/projects/%s" % (acct, a.project))
    if st == 404:
        st2, res2 = call(a.token, "POST", "/accounts/%s/pages/projects" % acct,
                         body={"name": a.project, "production_branch": a.branch})
        if st2 not in (200, 201):
            print("建项目失败 HTTP %s：%s" % (st2, str(res2)[:400]))
            return 1
        print("已创建项目 %s" % a.project)
    elif st != 200:
        print("查项目失败 HTTP %s：%s" % (st, str(res)[:300]))
        return 1

    files = files_of(a.dir)
    total = sum(os.path.getsize(f) for _, f in files)
    print("上传 %d 个文件，共 %.1f KB …" % (len(files), total / 1024.0))
    payload, ctype = multipart(files, a.branch)
    st, res = call(a.token, "POST",
                   "/accounts/%s/pages/projects/%s/deployments" % (acct, a.project),
                   raw=payload, ctype=ctype)
    if st not in (200, 201) or not res.get("success"):
        print("部署失败 HTTP %s：%s" % (st, str(res)[:500]))
        return 1
    dep = res["result"]
    print("✓ 部署完成：%s" % dep.get("url"))
    print("  预览地址：%s" % (dep.get("aliases") or ["-"])[0])

    if a.domain:
        st, res = call(a.token, "POST",
                       "/accounts/%s/pages/projects/%s/domains" % (acct, a.project),
                       body={"name": a.domain})
        if st in (200, 201):
            print("✓ 已绑定自定义域名：https://%s/" % a.domain)
        elif st == 409 or "already" in str(res).lower():
            print("· 域名已绑定过：https://%s/" % a.domain)
        else:
            print("绑域名失败 HTTP %s：%s" % (st, str(res)[:400]))
            print("  可去 Cloudflare Dashboard → Pages → %s → 自定义域 手动加。" % a.project)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
