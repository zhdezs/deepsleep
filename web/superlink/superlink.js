/* 超级连接（浏览器端）：输入 6 位配对码 → 找到对方的 deepsleep → 打开远程桌面 / 控制台。
   信令走 ntfy.sh（只传几 KB 的握手消息），连上之后数据在两台设备之间。 */
(function () {
  'use strict';
  var SIG = 'https://ntfy.sh';
  var SELF = Math.random().toString(16).slice(2, 10);
  var $ = function (s) { return document.querySelector(s); };
  var ctrl = null, stopped = false;

  function now() { return Math.floor(Date.now() / 1000); }
  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  function topic(code) { return 'dslink-v2-' + code; }

  function setStatus(text, busy) {
    var el = $('#status');
    el.classList.remove('hidden');
    el.innerHTML = (busy ? '<span class="dot"></span>' : '') + String(text).replace(/</g, '&lt;');
  }
  function hideStatus() { $('#status').classList.add('hidden'); }

  async function subscribe(tp, onMsg, signal) {
    var resp = await fetch(SIG + '/' + tp + '/json?since=5m', { signal: signal, cache: 'no-store' });
    if (!resp.ok || !resp.body) throw new Error('signal ' + resp.status);
    var reader = resp.body.getReader(), dec = new TextDecoder(), buf = '';
    for (;;) {
      var r = await reader.read();
      if (r.done) break;
      buf += dec.decode(r.value, { stream: true });
      var i;
      while ((i = buf.indexOf('\n')) >= 0) {
        var line = buf.slice(0, i).trim();
        buf = buf.slice(i + 1);
        if (!line) continue;
        try {
          var m = JSON.parse(line);
          if (m.event === 'message' && m.message) onMsg(m.message);
        } catch (e) { }
      }
    }
  }

  function publish(tp, obj) {
    return fetch(SIG + '/' + tp, { method: 'POST', body: JSON.stringify(obj), cache: 'no-store' })
      .catch(function () { });
  }

  async function ping(base, token) {
    try {
      var r = await fetch(base.replace(/\/+$/, '') + '/api/ping?t=' + encodeURIComponent(token),
        { cache: 'no-store' });
      if (!r.ok) return false;
      var j = await r.json();
      return j && j.ok === true;
    } catch (e) { return false; }
  }

  function showDone(base, token, p2p, peer) {
    // 令牌放 # 片段：隧道商（serveo 免费版）会给浏览器导航插一个警告页，
    // 它的「Continue」是按 action="" 做 GET 提交 —— 会整条顶掉 ?查询串，令牌就丢了。
    var rdBase = base.replace(/\/+$/, '');
    // 把对端地址也塞进 # 里：从 GitHub Pages 的 rd.html 打开时，页面才知道该连谁的 /api/rd/*
    var rd = rdBase + '/web/core/rd.html#u=' + encodeURIComponent(rdBase) + '&t=' + encodeURIComponent(token);
    var full = base.replace(/\/+$/, '') + '/web/core/#u=' + encodeURIComponent(base) + '&t=' + encodeURIComponent(token);
    $('#peerName').textContent = peer ? '（' + peer + '）' : '';
    $('#modeHint').textContent = p2p
      ? '点对点直连，延迟最低。'
      : '通过对方的内置隧道连接（对方在别的网络时会走这条）。';
    $('#linkRd').href = rd;
    $('#linkFull').href = full;
    $('#pairBox').classList.add('hidden');
    hideStatus();
    $('#done').classList.remove('hidden');
  }

  async function join(code) {
    ctrl = new AbortController();
    var tp = topic(code), offers = [], seen = {}, best = null, asked = false;
    var httpsPage = location.protocol === 'https:';

    subscribe(tp, function (raw) {
      try {
        var m = JSON.parse(raw);
        if (m.t !== 'offer' || !m.token) return;
        var key = m.token + '|' + (m.tunnel || '');
        if (seen[key]) return;
        seen[key] = 1;
        offers.push(m);
      } catch (e) { }
    }, ctrl.signal).catch(function () { });

    await sleep(400);
    publish(tp, { t: 'hello', from: SELF, name: '网页端', ts: now() });
    setStatus('正在找这台设备…', true);

    for (var i = 0; i < 80 && !best && !stopped; i++) {   // 80 × 1.5s = 2 分钟，等对方把隧道建起来
      // 1) 局域网直连（https 页面会被浏览器拦，所以只在 http 页面里试）
      if (!httpsPage) {
        for (var a = 0; a < offers.length && !best; a++) {
          var lan = offers[a].lan || [];
          for (var b = 0; b < lan.length; b++) {
            var bu = 'http://' + lan[b];
            if (await ping(bu, offers[a].token)) { best = offers[a]; best._base = bu; best._p2p = true; break; }
          }
        }
      }
      // 2) 内置隧道（https，浏览器可用）
      if (!best) {
        for (var c = 0; c < offers.length; c++) {
          if (!offers[c].tunnel) continue;
          if (await ping(offers[c].tunnel, offers[c].token)) {
            best = offers[c]; best._base = offers[c].tunnel; best._p2p = false; break;
          }
        }
      }
      if (!best && !asked && i >= 2 && !offers.some(function (o) { return o.tunnel; })) {
        asked = true;
        publish(tp, { t: 'no-lan', from: SELF, ts: now() });
        setStatus('同一网络里没找到，正在让对方开一条内置隧道（不用下组件，约 10-40 秒）…', true);
      }
      if (!best) await sleep(1500);
    }

    if (stopped) return;
    if (!best) {
      setStatus('没连上：检查配对码对不对、对方是不是还开着「超级连接」。');
      $('#btnGo').disabled = false;
      return;
    }
    publish(tp, { t: 'p2p', from: SELF, ok: !!best._p2p, ts: now() });
    showDone(best._base, best.token, !!best._p2p, best.name || '');
  }

  function start(code) {
    code = String(code || '').replace(/\D/g, '').slice(0, 6);
    if (code.length !== 6) {
      setStatus('配对码是 6 位数字。');
      return;
    }
    $('#code').value = code;
    $('#btnGo').disabled = true;
    setStatus('正在连 ' + code + ' …', true);
    join(code).catch(function (e) {
      setStatus('连接出错：' + e.message);
      $('#btnGo').disabled = false;
    });
  }

  $('#code').addEventListener('input', function () {
    this.value = this.value.replace(/\D/g, '').slice(0, 6);
    if (this.value.length === 6) start(this.value);
  });
  $('#btnGo').addEventListener('click', function () { start($('#code').value); });

  var q = new URLSearchParams(location.search);
  var pre = q.get('type') || q.get('code') || q.get('t');
  if (pre) start(pre);
  else $('#code').focus();
})();
