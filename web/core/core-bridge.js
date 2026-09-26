/* deepsleep 内核版网页桥（core-bridge.js）
   作用：把桌面客户端的 window.chrome.webview 协议搬到 HTTP + SSE 上，
   于是同一份 ui/app.js 不用改一行就能在浏览器里操控本机的内核版（Core）。
   连不上时会在页面上给一个配对面板：填主机 / 端口 / 令牌即可。 */
(function () {
  'use strict';
  var LS = 'dsweb.core.v1';
  var qs = new URLSearchParams(location.search);
  var hs = new URLSearchParams((location.hash || '').replace(/^#/, ''));
  var saved = {};
  try { saved = JSON.parse(localStorage.getItem(LS) || '{}') || {}; } catch (e) { saved = {}; }

  var cfg = {
    host: qs.get('h') || hs.get('h') || saved.host || '127.0.0.1',
    port: qs.get('p') || hs.get('p') || saved.port || '8756',
    token: qs.get('t') || hs.get('t') || saved.token || ''
  };
  var base = function () { return 'http://' + cfg.host + ':' + cfg.port; };
  var listeners = [];
  var pending = new Map();
  var seq = 0;
  var es = null;
  var connected = false;

  function saveCfg() {
    try { localStorage.setItem(LS, JSON.stringify(cfg)); } catch (e) { }
    try { if (location.hash) history.replaceState(null, '', location.pathname + location.search); } catch (e) { }
  }

  /* ---------- 事件派发（等价于外壳的 PostWebMessageAsJson） ---------- */
  function dispatch(obj) {
    var json = typeof obj === 'string' ? obj : JSON.stringify(obj);
    if (json.indexOf('https://data.local/') >= 0) json = json.split('https://data.local/').join(base() + '/data/');
    for (var i = 0; i < listeners.length; i++) {
      try { listeners[i]({ data: json }); } catch (e) { console.error(e); }
    }
  }

  /* ---------- 请求（等价于 postMessage → InvokeAsync） ---------- */
  function invoke(msg) {
    var cmd = msg && msg.cmd;
    if (cmd === 'pickFile') return pickFile(msg);
    if (cmd === 'pickFolder') { toastLine('内核版网页不支持选目录，直接把路径告诉 AI 就行'); respond(msg, true); return; }
    return fetch(base() + '/api/invoke', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-DS-Token': cfg.token },
      body: JSON.stringify(msg)
    }).then(function (r) { return r.text(); })
      .then(function (t) { var o = null; try { o = JSON.parse(t); } catch (e) { o = { ok: false, err: t }; } respond(msg, !!(o && o.ok), o); })
      .catch(function (e) { respond(msg, false, { err: String(e) }); });
  }
  function respond(msg, ok, extra) {
    var id = msg && msg.id;
    if (id == null) return;
    var cb = pending.get(id);
    if (!cb) return;
    pending.delete(id);
    cb(Object.assign({ id: id, ok: ok }, extra || {}));
  }

  function pickFile(msg) {
    var input = document.createElement('input');
    input.type = 'file';
    input.style.display = 'none';
    document.body.appendChild(input);
    input.onchange = function () {
      var f = input.files && input.files[0];
      document.body.removeChild(input);
      if (!f) { respond(msg, true); return; }
      toastLine('正在把 ' + f.name + ' 传给内核…');
      fetch(base() + '/api/upload?name=' + encodeURIComponent(f.name), {
        method: 'POST', headers: { 'X-DS-Token': cfg.token }, body: f
      }).then(function (r) { return r.json(); })
        .then(function (o) {
          if (!o || !o.ok) throw new Error(o && o.err || '上传失败');
          return fetch(base() + '/api/invoke', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'X-DS-Token': cfg.token },
            body: JSON.stringify({ id: ++seq, cmd: 'attach', kind: msg.kind || 0, path: o.path })
          });
        })
        .then(function (r) { return r.text(); })
        .then(function () { respond(msg, true); toastLine('已附加：' + f.name); })
        .catch(function (e) { respond(msg, false, { err: String(e && e.message || e) }); toastLine('上传失败：' + e, true); });
    };
    input.click();
  }

  /* ---------- 页面上的小提示条 ---------- */
  var badge = null;
  function ensureBadge() {
    if (badge) return badge;
    badge = document.createElement('div');
    badge.style.cssText = 'position:fixed;left:10px;bottom:10px;z-index:99;font:11.5px/1.6 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif;' +
      'padding:5px 10px;border-radius:9px;background:rgba(0,0,0,.62);color:#fff;cursor:pointer;opacity:.55;backdrop-filter:blur(6px)';
    badge.onclick = function () { badge.style.opacity = '0'; if (confirm('配置内核连接？')) openPair(); };
    document.body.appendChild(badge);
    return badge;
  }
  function setBadge(text, warn) {
    var b = ensureBadge();
    b.textContent = '⚡ ' + text;
    b.style.background = warn ? 'rgba(180,40,30,.78)' : 'rgba(0,0,0,.62)';
    b.style.opacity = '.55';
  }
  function toastLine(text) {
    var t = document.createElement('div');
    t.textContent = text;
    t.style.cssText = 'position:fixed;left:50%;bottom:46px;transform:translateX(-50%);z-index:9999;background:rgba(0,0,0,.8);color:#fff;' +
      'font:12.5px/1.6 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif;padding:7px 14px;border-radius:10px';
    document.body.appendChild(t);
    setTimeout(function () { t.style.opacity = '0'; }, 2200);
    setTimeout(function () { t.remove(); }, 2800);
  }

  /* ---------- 配对面板 ---------- */
  function openPair() {
    var old = document.getElementById('corePair');
    if (old) old.remove();
    var wrap = document.createElement('div');
    wrap.id = 'corePair';
    wrap.style.cssText = 'position:fixed;inset:0;z-index:9998;background:rgba(0,0,0,.45);display:flex;align-items:center;justify-content:center';
    wrap.innerHTML =
      '<div style="width:520px;max-width:92vw;background:var(--panel,#fff);color:var(--text,#111);border:1px solid var(--border,#ddd);' +
      'border-radius:16px;padding:22px 24px;font:13.5px/1.7 -apple-system,\'Segoe UI\',\'Microsoft YaHei\',sans-serif;box-shadow:0 20px 60px rgba(0,0,0,.35)">' +
      '<h3 style="margin:0 0 6px;font-size:16px">连接本机内核（Core）</h3>' +
      '<div style="color:var(--muted,#777);font-size:12px;margin-bottom:14px">' +
      '  在这台电脑上运行 <b>deepsleep-core.exe</b>，它会把端口和<b>配对令牌</b>打印在窗口里，填到下面即可（令牌也会写进 data\\core-token.txt）。' +
      '</div>' +
      '<div class="field"><label>主机</label><input id="cpHost" type="text"></div>' +
      '<div class="field"><label>端口</label><input id="cpPort" type="text"></div>' +
      '<div class="field"><label>配对令牌</label><input id="cpToken" type="text"></div>' +
      '<div style="display:flex;gap:8px;justify-content:flex-end;margin-top:16px">' +
      '  <a class="btn" style="text-decoration:none;padding:8px 14px" target="_blank" href="https://zhdezs.github.io/deepsleep/#core">还没有 Core？去下载</a>' +
      '  <button class="btn" id="cpCancel">取消</button>' +
      '  <button class="btn primary" id="cpGo">连接</button>' +
      '</div></div>';
    document.body.appendChild(wrap);
    var iHost = wrap.querySelector('#cpHost'), iPort = wrap.querySelector('#cpPort'), iTok = wrap.querySelector('#cpToken');
    iHost.value = cfg.host; iPort.value = cfg.port; iTok.value = cfg.token;
    wrap.querySelector('#cpCancel').onclick = function () { wrap.remove(); };
    wrap.querySelector('#cpGo').onclick = function () {
      cfg.host = iHost.value.trim() || '127.0.0.1';
      cfg.port = iPort.value.trim() || '8756';
      cfg.token = iTok.value.trim();
      wrap.remove();
      connect(false);
    };
  }

  /* ---------- 连接与事件流 ---------- */
  function connect(quiet) {
    setBadge('正在连接 ' + cfg.host + ':' + cfg.port + ' …');
    fetch(base() + '/api/ping', { cache: 'no-store' })
      .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function (info) {
        if (!cfg.token) { setBadge('需要配对令牌', true); openPair(); return; }
        saveCfg();
        setBadge('内核 ' + (info.version || '') + ' · ' + cfg.host + ':' + cfg.port);
        startSse();
      })
      .catch(function () {
        setBadge('没连上内核（' + cfg.host + ':' + cfg.port + '）', true);
        if (!quiet) openPair();
      });
  }

  function startSse() {
    if (es) { try { es.close(); } catch (e) { } }
    connected = false;
    es = new EventSource(base() + '/api/events?token=' + encodeURIComponent(cfg.token));
    es.onopen = function () {
      connected = true;
      setBadge('内核已连接 · ' + cfg.host + ':' + cfg.port);
      setTimeout(function () { dispatch({ ev: 'uiReady' }); }, 260);
    };
    es.onmessage = function (ev) { dispatch(ev.data); };
    es.onerror = function () {
      if (connected) { connected = false; setBadge('内核连接断了，正在重连…', true); }
    };
  }

  /* ---------- data.local 的图片要带上令牌 ---------- */
  function fixSrc(el) {
    var s = el.getAttribute && el.getAttribute('src');
    if (!s || s.indexOf(base() + '/data/') !== 0 || s.indexOf('token=') >= 0) return;
    el.setAttribute('src', s + (s.indexOf('?') < 0 ? '?' : '&') + 'token=' + encodeURIComponent(cfg.token));
  }
  function watchImages() {
    var apply = function (root) {
      if (!root || !root.querySelectorAll) return;
      var list = root.querySelectorAll('img[src]');
      for (var i = 0; i < list.length; i++) fixSrc(list[i]);
    };
    new MutationObserver(function (muts) {
      for (var i = 0; i < muts.length; i++) {
        var t = muts[i].target;
        if (t && t.nodeName === 'IMG') fixSrc(t);
        else if (t) apply(t);
      }
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['src'] });
  }

  /* ---------- 伪装成 WebView2 ---------- */
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: function (obj) {
      var o = typeof obj === 'string' ? JSON.parse(obj) : obj;
      if (!connected && o && o.id != null) { }
      return invoke(o);
    },
    addEventListener: function (type, fn) {
      if (type === 'message') listeners.push(fn);
      else if (type === 'uiReady') listeners.push(fn);
    }
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();

  function start() {
    document.documentElement.setAttribute('data-ds-core', '1');
    watchImages();
    if (!cfg.token) { connect(true); }
    else connect(true);
  }
})();
