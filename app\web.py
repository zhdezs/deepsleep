# -*- coding: utf-8 -*-
"""Web 界面后端：用标准库 http.server 提供 JSON API + 静态前端。

后端逻辑完全复用现有 engine / storage / analyzer，不引入任何第三方依赖；
前端是微信聊天框风格的单页应用（见 app/web/index.html）。

API 一览：
    GET  /                    → 返回前端页面 index.html
    GET  /api/state           → {deepseek_configured, session_id, mode}
    POST /api/session/new     → 新建会话，返回 {session_id}
    GET  /api/messages        → 当前会话全部消息
    POST /api/reply           → {text, mode} 生成回复并入库，返回结构化结果
    POST /api/alternatives    → {text, n} 生成备选回复（换一招）
    POST /api/archive         → 归档当前会话并学习
    GET  /api/stats           → 统计报告文本

mode 语义（对应 engine.generate 的 use_llm）：
    "auto"  → DeepSeek 智能开启（强度高/威胁类/持续升级才调用，省 token）
    "on"    → DeepSeek 强制开启
    "off"   → 纯本地（语言模型 → 实时组装）

启动：
    py -3 main.py --web [--port 8000]
"""

import json
import os
import sys
import threading
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import resource_dir

STATIC_DIR = os.path.join(resource_dir(), "web")
INDEX_PATH = os.path.join(STATIC_DIR, "index.html")

DEFAULT_PORT = 8000

# 供退出端点调用：在另一个线程里 shutdown 服务器
_HTTPD = None


class WebApp:
    """封装引擎与存储的共享状态，供 HTTP handler 线程安全地访问。"""

    def __init__(self):
        from .engine import ResponseEngine
        from .storage import Storage
        from . import llm

        self.storage = Storage()
        self.engine = ResponseEngine()
        self.lock = threading.Lock()
        self.session_id = None
        self.mode = "auto"   # auto / on / off
        self.llm = llm
        self._ensure_session()

    def _ensure_session(self):
        if self.session_id is None:
            self.session_id = self.storage.create_session("网络键盘侠")
            self.engine.new_session(self.session_id)

    def state(self):
        return {
            "deepseek_configured": self.llm.is_configured(),
            "session_id": self.session_id,
            "mode": self.mode,
        }

    def new_session(self):
        with self.lock:
            self._ensure_session()
            self.storage.end_session(self.session_id)
            self.session_id = self.storage.create_session("网络键盘侠")
            self.engine.new_session(self.session_id)
            return self.session_id

    def messages(self):
        with self.lock:
            self._ensure_session()
            return self.storage.session_messages(self.session_id)

    def _map_mode(self, mode):
        m = (mode or self.mode).lower()
        if m == "on":
            return True
        if m == "off":
            return False
        return "auto"

    def reply(self, text, mode=None):
        text = (text or "").strip()
        if not text:
            return None
        use_llm = self._map_mode(mode)
        with self.lock:
            self._ensure_session()
            r = self.engine.generate(text, self.session_id, tone="live", use_llm=use_llm)
            self.storage.add_message(
                self.session_id, "troll", r["troll_text"],
                category=r["category"], aggression=r["aggression"],
            )
            self.storage.add_message(
                self.session_id, "ai", r["response"], strategy=r["strategy"],
            )
            return r

    def alternatives(self, text, n=3):
        text = (text or "").strip()
        if not text:
            return []
        with self.lock:
            return self.engine.alternatives(text, n=n, tone="live")

    def archive(self):
        from .analyzer import learn_from_session
        with self.lock:
            self._ensure_session()
            sid = self.session_id
            self.storage.end_session(sid)
            win = learn_from_session(self.storage, sid)
            self.session_id = self.storage.create_session("网络键盘侠")
            self.engine.new_session(self.session_id)
            return {"archived": sid, "win": win}

    def stats(self):
        from .analyzer import report
        with self.lock:
            return report(self.storage)


_APP = None
_APP_LOCK = threading.Lock()


def get_app():
    global _APP
    if _APP is None:
        with _APP_LOCK:
            if _APP is None:
                _APP = WebApp()
    return _APP


def _read_index():
    if os.path.exists(INDEX_PATH):
        with open(INDEX_PATH, encoding="utf-8") as f:
            return f.read()
    return "<html><body><h1>前端缺失</h1><p>请确认 app/web/index.html 存在。</p></body></html>"


class Handler(BaseHTTPRequestHandler):
    server_version = "TrollWrangler/1.0"

    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        if isinstance(body, (dict, list)):
            body = json.dumps(body, ensure_ascii=False)
        data = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _json_err(self, code, msg):
        self._send(code, {"ok": False, "error": msg})

    def _body(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return {}
        try:
            return json.loads(self.rfile.read(length).decode("utf-8"))
        except Exception:
            return {}

    def log_message(self, fmt, *args):  # 静默访问日志
        pass

    def do_GET(self):
        app = get_app()
        path = self.path.split("?", 1)[0]
        if path in ("/", "/index.html"):
            self._send(200, _read_index(), "text/html; charset=utf-8")
        elif path == "/api/state":
            self._send(200, {"ok": True, "data": app.state()})
        elif path == "/api/messages":
            self._send(200, {"ok": True, "data": app.messages()})
        elif path == "/api/stats":
            self._send(200, {"ok": True, "data": app.stats()})
        else:
            self._json_err(404, "not found: " + path)

    def do_POST(self):
        app = get_app()
        path = self.path.split("?", 1)[0]
        data = self._body()

        if path == "/api/session/new":
            self._send(200, {"ok": True, "data": {"session_id": app.new_session()}})
        elif path == "/api/reply":
            r = app.reply(data.get("text"), data.get("mode"))
            if r is None:
                self._json_err(400, "empty text")
            else:
                self._send(200, {"ok": True, "data": r})
        elif path == "/api/alternatives":
            out = app.alternatives(data.get("text"), int(data.get("n") or 3))
            self._send(200, {"ok": True, "data": out})
        elif path == "/api/archive":
            self._send(200, {"ok": True, "data": app.archive()})
        elif path == "/api/mode":
            m = (data.get("mode") or "auto").lower()
            if m in ("auto", "on", "off"):
                app.mode = m
                self._send(200, {"ok": True, "data": {"mode": app.mode}})
            else:
                self._json_err(400, "invalid mode")
        elif path == "/api/quit":
            self._send(200, {"ok": True, "data": {"message": "bye"}})
            if _HTTPD is not None:
                threading.Timer(0.3, _HTTPD.shutdown).start()
        else:
            self._json_err(404, "not found: " + path)


def serve(port=DEFAULT_PORT, open_browser=True, host="127.0.0.1"):
    """启动 Web 服务。open_browser=True 时自动打开浏览器。"""
    global _HTTPD
    httpd = ThreadingHTTPServer((host, int(port)), Handler)
    _HTTPD = httpd
    url = "http://%s:%d/" % (host, int(port))
    if open_browser:
        threading.Timer(0.5, lambda: webbrowser.open(url)).start()
    print("以理服人 · Web 界面已启动：%s" % url)
    print("按 Ctrl+C 或点击界面右上角「退出」停止服务。")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n服务已停止。")
    finally:
        httpd.server_close()
        _HTTPD = None


if __name__ == "__main__":
    import sys
    port = DEFAULT_PORT
    if len(sys.argv) > 1:
        port = int(sys.argv[1])
    serve(port)
