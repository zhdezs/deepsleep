'use strict';
/* deepsleep 界面层：只负责画面与交互，逻辑全在内核（通过 JSON 协议调用） */

const $ = s => document.querySelector(s);
const petMode = new URLSearchParams(location.search).has('pet');
if (petMode) document.body.classList.add('pet');

/* ---------------- 与内核的通道 ---------------- */
let seq = 0;
const waiting = new Map();

/* 外壳有两种：
   · Windows 桌面版 = WPF + WebView2 → window.chrome.webview
   · Linux / macOS 桌面版 = Photino（系统自带 WebView）→ window.external
   两边协议完全一样（JSON），所以这里只换通道，界面代码一行不用改。 */
const bridge = (() => {
  const wv = window.chrome && window.chrome.webview;
  if (wv && typeof wv.postMessage === 'function') {
    return {
      kind: 'webview2',
      send: o => { try { wv.postMessage(o); } catch (e) { } },
      on: f => wv.addEventListener('message', e => {
        f(typeof e.data === 'string' ? JSON.parse(e.data) : e.data);
      })
    };
  }
  const ex = window.external;
  if (ex && typeof ex.sendMessage === 'function' && typeof ex.receiveMessage === 'function') {
    return {
      kind: 'photino',
      send: o => { try { ex.sendMessage(JSON.stringify(o)); } catch (e) { } },
      on: f => { try { ex.receiveMessage(s => { try { f(JSON.parse(s)); } catch (e) { } }); } catch (e) { } }
    };
  }
  return { kind: 'none', send: () => { }, on: () => { } };
})();

function host(obj) { bridge.send(obj); }
function call(cmd, args) {
  return new Promise(res => {
    const id = ++seq;
    waiting.set(id, res);
    host(Object.assign({ id, cmd }, args || {}));
  });
}
bridge.on(m => {
  if (!m) return;
  if (m.ev) handleEvent(m);
  else if (m.id != null) { const r = waiting.get(m.id); if (r) { waiting.delete(m.id); r(m); } }
});

/* ---------------- 状态 ---------------- */
const S = {
  tab: 0, conv: null, items: [], convs: [], settings: {},
  prompts: [], clusterTemplates: [], skills: [], state: 'idle', agents: [],
  update: null, pet: true, version: '', host: null
};
const kind = () => (S.tab === 1 ? 2 : 0);

/* ---------------- Markdown（轻量渲染，够用） ---------------- */
function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"]/g,
    c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}
function inline(t) {
  const keep = [];
  const stash = html => { keep.push(html); return '\u0001' + (keep.length - 1) + '\u0001'; };
  let s = t.replace(/`([^`]+)`/g, (m, c) => stash('<code>' + c + '</code>'));
  s = s.replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g,
    (m, txt, url) => stash('<a href="' + url + '">' + txt + '</a>'));
  s = s.replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>')
    .replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<i>$2</i>');
  s = s.replace(/https?:\/\/[^\s<>"'\u0001]+/g, m => {
    const tail = (m.match(/[.,;:!?)\]}，。；：！？）】]+$/) || [''])[0];
    const url = tail ? m.slice(0, -tail.length) : m;
    return '<a href="' + url + '">' + url + '</a>' + tail;
  });
  return s.replace(/\u0001(\d+)\u0001/g, (m, i) => keep[+i]);
}
function mdText(t) {
  const lines = esc(t).split('\n');
  const out = [];
  let ul = false, ol = false, para = [];
  const closeLists = () => { if (ul) { out.push('</ul>'); ul = false; } if (ol) { out.push('</ol>'); ol = false; } };
  const flush = () => { if (para.length) { out.push('<p>' + inline(para.join('<br>')) + '</p>'); para = []; } };
  for (const raw of lines) {
    const ln = raw.trimEnd();
    let m;
    if (!ln.trim()) { flush(); closeLists(); continue; }
    if ((m = ln.match(/^(#{1,6})\s+(.*)$/))) {
      flush(); closeLists();
      const n = Math.min(3, m[1].length);
      out.push('<h' + n + '>' + inline(m[2]) + '</h' + n + '>');
      continue;
    }
    if (/^\s*(-{3,}|\*{3,})\s*$/.test(ln)) { flush(); closeLists(); out.push('<hr>'); continue; }
    if ((m = ln.match(/^&gt;\s?(.*)$/))) { flush(); closeLists(); out.push('<blockquote>' + inline(m[1]) + '</blockquote>'); continue; }
    if ((m = ln.match(/^\s*[-*+]\s+(.*)$/))) {
      flush(); if (ol) { out.push('</ol>'); ol = false; }
      if (!ul) { out.push('<ul>'); ul = true; }
      out.push('<li>' + inline(m[1]) + '</li>');
      continue;
    }
    if ((m = ln.match(/^\s*\d+[.)]\s+(.*)$/))) {
      flush(); if (ul) { out.push('</ul>'); ul = false; }
      if (!ol) { out.push('<ol>'); ol = true; }
      out.push('<li>' + inline(m[1]) + '</li>');
      continue;
    }
    closeLists();
    para.push(ln);
  }
  flush(); closeLists();
  return out.join('');
}
function md(src) {
  if (!src) return '';
  const parts = String(src).split('```');
  let html = '';
  for (let i = 0; i < parts.length; i++) {
    if (i % 2 === 1) {
      let code = parts[i], lang = '';
      const nl = code.indexOf('\n');
      if (nl >= 0 && !code.slice(0, nl).includes('`')) { lang = code.slice(0, nl).trim(); code = code.slice(nl + 1); }
      html += '<pre><button class="copycode" data-copy>复制</button><code>' +
        esc(code.replace(/\n$/, '')) + '</code></pre>';
    } else {
      html += mdText(parts[i]);
    }
  }
  return html;
}
/* ---------------- 渲染 ---------------- */
function renderConvs() {
  const box = $('#convList');
  box.innerHTML = '';
  if (!S.convs.length) {
    const e = document.createElement('div');
    e.className = 'sysbar'; e.style.margin = '10px auto'; e.textContent = '还没有对话';
    box.appendChild(e);
  }
  for (const c of S.convs) {
    const d = document.createElement('div');
    d.className = 'conv' + (S.conv && c.sid === S.conv.sid ? ' on' : '');
    d.dataset.sid = c.sid;
    const t = document.createElement('span');
    t.className = 't'; t.textContent = c.title;
    d.appendChild(t);
    if (c.pinned) { const p = document.createElement('span'); p.className = 'p'; p.textContent = '📌'; d.appendChild(p); }
    d.onclick = () => call('selectConv', { kind: kind(), sid: c.sid });
    d.oncontextmenu = ev => { ev.preventDefault(); convMenu(ev, c); };
    box.appendChild(d);
  }
}

function itemNodes(it) {
  const nodes = [];
  if (it.showTime && !it.thinking) {
    const tl = document.createElement('div');
    tl.className = 'timeline';
    tl.textContent = it.time;
    nodes.push(tl);
  }
  const row = document.createElement('div');
  row.className = 'row' + (it.self ? ' self' : '') + (it.sys ? ' sys' : '');
  row.dataset.id = it.id;
  if (it.sys) {
    const bar = document.createElement('div');
    bar.className = 'sysbar';
    bar.textContent = it.text;
    row.appendChild(bar);
  } else {
    const av = document.createElement('div');
    av.className = 'avatar';
    av.style.background = it.self ? '#007AFF' : (it.color || '#4A90D9');
    av.textContent = it.self ? (it.selfAvatar || '你') : (it.avatar || 'AI');
    row.appendChild(av);
    const b = document.createElement('div');
    b.className = 'bubble' + (it.tool ? ' tool' : '');
    row.appendChild(b);
    const acts = document.createElement('div');
    acts.className = 'acts';
    const cp = document.createElement('button');
    cp.textContent = '复制'; cp.title = '复制';
    cp.onclick = () => navigator.clipboard.writeText(it.text || '');
    const del = document.createElement('button');
    del.textContent = '删除'; del.title = '删除这条消息';
    del.onclick = () => call('deleteMsg', { kind: kind(), item: it.id });
    acts.append(cp, del);
    row.appendChild(acts);
  }
  paintBubble(row, it);
  nodes.push(row);
  return nodes;
}

function paintBubble(row, it) {
  if (!it.sys) {
    const b = row.querySelector('.bubble');
    if (!b) return;
    if (it.tool) { paintTool(b, it); return; }
    if (it.thinking) {
      b.classList.add('thinking');
      if (b.dataset.think !== it.text) {
        b.dataset.think = it.text;
        b.innerHTML = '<span>' + esc(it.text) + '</span><span class="dots"><i></i><i></i><i></i></span>';
      }
      b.style.opacity = it.opacity == null ? '' : it.opacity;
      return;
    }
    b.classList.remove('thinking');
    b.style.opacity = '';
    // 流式过程中正文每次增量都会重刷这个气泡，<details> 的展开状态会跟着丢。
    // 思考内容是边生成边刷的，用户想一直看着它，所以这里把展开状态接住。
    const prevThink = b.querySelector('details.thinkbox');
    const thinkOpen = !!(prevThink && prevThink.open);
    let html = '';
    if (it.speaker) html += '<div class="who">' + esc(it.speaker) + '</div>';
    else if (it.meta && it.meta !== 'AI' && it.meta !== '你') html += '<div class="who">' + esc(it.meta) + '</div>';
    if (it.thought) html += thinkBox(it.thought, !it.text, thinkOpen);
    if (it.image) html += '<img src="' + esc(it.image) + '" alt="图片">';
    if (it.attName) html += '<div class="who">📎 ' + esc(it.attName) + '　' + esc(it.attSize) + '</div>';
    html += md(it.text);
    b.innerHTML = html;
  } else {
    const bar = row.querySelector('.sysbar');
    if (bar) bar.textContent = it.text;
  }
}

/* 思考内容（模型 reasoning）：默认折叠在正文上方，不混进正文；
   模型若用英文思考，就在标题里说明，避免用户以为 AI「突然冒出一大段英文」。
   streaming=true 表示正文还没开始（思考还在往外吐）→ 标题写「正在思考…」。 */
function thoughtIsEnglish(t) {
  const s = String(t || '');
  const cjk = (s.match(/[\u4e00-\u9fff]/g) || []).length;
  const lat = (s.match(/[A-Za-z]/g) || []).length;
  return lat > 40 && cjk < lat / 6;
}
function thinkBox(t, streaming, open) {
  const label = streaming ? '正在思考…'
    : thoughtIsEnglish(t) ? '已思考（模型用英文思考，已折叠）' : '已思考';
  return '<details class="thinkbox"' + (open ? ' open' : '') + '><summary>' + esc(label) +
    '</summary><div class="thinkbody">' + esc(t) + '</div></details>';
}

function toolSig(it) {
  const detail = it.toolDetail || it.text || '';
  return (it.tool || '') + '::' + (it.toolSummary || '') + '::' + detail.length + '::' + detail.slice(0, 48);
}

function paintTool(b, it) {
  const sig = toolSig(it);
  if (b.dataset.tsig === sig) return;
  const prev = b.querySelector('details.toolcard');
  const open = !!(prev && prev.open);
  b.dataset.tsig = sig;
  const det = document.createElement('details');
  det.className = 'toolcard';
  det.open = open;
  const sum = document.createElement('summary');
  const nm = document.createElement('span');  nm.className = 'tname'; nm.textContent = it.tool || 'tool';
  const tx = document.createElement('span');  tx.className = 'tsum';  tx.textContent = it.toolSummary || '';
  sum.append(nm, tx);
  const body = document.createElement('div'); body.className = 'tbody';
  body.innerHTML = md(it.toolDetail || it.text || '');
  det.append(sum, body);
  b.innerHTML = '';
  b.appendChild(det);
}

/* ---- 命令 / 文件 / 技能聚合卡：同一轮里各并成一张，点开看全部 ----
   技能也走同一套：调用链默认收起，不糊在对话里。 */
function groupKind(it) {
  if (!it || !it.tool || it.thinking) return '';
  const k = it.toolKind;
  return (k === 'cmd' || k === 'file' || k === 'skill' || k === 'split') ? k : '';
}

function groupEntries(items) {
  const out = [];
  for (const it of items) {
    const k = groupKind(it);
    const last = out[out.length - 1];
    if (k && last && last.kind === k) last.items.push(it);
    else if (k) out.push({ g: true, kind: k, items: [it] });
    else out.push({ it });
  }
  return out;
}

function fileNameOf(it) {
  let p = String(it.toolSummary || '');
  p = p.replace(/^已写入[：:]\s*/, '').replace(/[（(][^（()）]*[)）]\s*$/, '').trim();
  const seg = p.split(/[\\/]/).pop();
  return (seg && seg.trim()) ? seg.trim() : (it.tool || '文件');
}

function groupTitle(kind) {
  if (kind === 'cmd') return '运行的命令';
  if (kind === 'skill') return '调用的技能';
  if (kind === 'split') return '拆分的子任务';
  return '编辑的文件';
}

function groupSummary(kind, items) {
  if (kind === 'cmd') return items.length + ' 条（点开看全部命令与输出）';
  if (kind === 'skill' || kind === 'split') {
    const names = items.map(it => String(it.toolSummary || it.tool || ''));
    return items.length + ' 个：' + names.slice(0, 4).join('、') + (names.length > 4 ? ' 等' : '');
  }
  const names = items.map(fileNameOf);
  return items.length + ' 个：' + names.slice(0, 4).join('、') + (names.length > 4 ? ' 等' : '');
}

function groupRowItems(row) {
  const ids = String(row.dataset.gids || '').split(' ').filter(Boolean);
  return ids.map(id => S.items.find(x => x.id === id)).filter(Boolean);
}

function paintGroup(row, kind, items) {
  const b = row.querySelector('.bubble');
  if (!b || !items.length) return;
  const sig = kind + '::' + items.length + '::' + items.map(toolSig).join('|');
  if (b.dataset.tsig === sig) return;
  const prev = b.querySelector('details.toolcard');
  const open = !!(prev && prev.open);
  b.dataset.tsig = sig;
  row.dataset.gkind = kind;
  row.dataset.gids = items.map(x => x.id).join(' ');
  const det = document.createElement('details');
  det.className = 'toolcard grp';
  det.open = open;
  const sum = document.createElement('summary');
  const nm = document.createElement('span'); nm.className = 'tname';
  nm.textContent = groupTitle(kind);
  const tx = document.createElement('span'); tx.className = 'tsum';
  tx.textContent = groupSummary(kind, items);
  sum.append(nm, tx);
  const body = document.createElement('div'); body.className = 'tbody';
  items.forEach((it, i) => {
    const sub = document.createElement('div'); sub.className = 'titem';
    const h = document.createElement('div'); h.className = 'titem-h';
    h.textContent = (i + 1) + '. ' + (it.toolSummary || it.tool || '');
    const c = document.createElement('div'); c.className = 'titem-b';
    c.innerHTML = md(it.toolDetail || it.text || '');
    sub.append(h, c);
    body.appendChild(sub);
  });
  det.append(sum, body);
  b.innerHTML = '';
  b.appendChild(det);
}

function groupNodes(entry) {
  const nodes = [];
  const first = entry.items[0];
  if (first.showTime) {
    const tl = document.createElement('div');
    tl.className = 'timeline'; tl.textContent = first.time;
    nodes.push(tl);
  }
  const row = document.createElement('div');
  row.className = 'row';
  row.dataset.gkey = 'g:' + entry.kind + ':' + first.id;
  const av = document.createElement('div');
  av.className = 'avatar';
  av.style.background = first.color || '#4A90D9';
  av.textContent = first.avatar || 'AI';
  row.appendChild(av);
  const b = document.createElement('div'); b.className = 'bubble tool'; row.appendChild(b);
  const acts = document.createElement('div'); acts.className = 'acts';
  const del = document.createElement('button');
  del.textContent = '删除'; del.title = '删除这一组工具结果';
  del.onclick = () => entry.items.forEach(x => call('deleteMsg', { kind: kind(), item: x.id }));
  acts.append(del); row.appendChild(acts);
  row.dataset.gids = entry.items.map(x => x.id).join(' ');
  row.dataset.gkind = entry.kind;
  paintGroup(row, entry.kind, entry.items);
  row.dataset.gids = entry.items.map(x => x.id).join(' ');
  nodes.push(row);
  return nodes;
}

function captureOpen() {
  const m = {};
  for (const d of document.querySelectorAll('#msgs details.toolcard')) {
    const row = d.closest('.row');
    const k = row && (row.dataset.gkey || (row.dataset.id ? 't:' + row.dataset.id : ''));
    if (k) m[k] = d.open;
  }
  return m;
}

function restoreOpen(m) {
  for (const d of document.querySelectorAll('#msgs details.toolcard')) {
    const row = d.closest('.row');
    const k = row && (row.dataset.gkey || (row.dataset.id ? 't:' + row.dataset.id : ''));
    if (k && k in m) d.open = m[k];
  }
}

// 老版本存下来的「调用了工具「X」」气泡：不再显示（工具卡片本身就说明了一切）
function isToolCallNotice(it) {
  return !it.self && !it.sys && !it.tool && /^调用了工具「[^」]*」$/.test(String(it.text || '').trim());
}
function renderItems(force) {
  const box = $('#msgs');
  const open = captureOpen();
  box.innerHTML = '';
  for (const e of groupEntries(S.items.filter(x => !isToolCallNotice(x))))
    for (const n of (e.g ? groupNodes(e) : itemNodes(e.it))) box.appendChild(n);
  restoreOpen(open);
  scrollBottom(force === undefined ? true : force);
}

function addItem(it) {
  if (!S.conv || it.sessionId !== S.conv.sid) return;
  if (isToolCallNotice(it)) return;
  S.items.push(it);
  renderItems(!!it.self);
}

function findRow(conv, id) {
  if (!S.conv || conv !== S.conv.sid) return null;
  const box = $('#msgs');
  return box.querySelector('[data-id="' + id + '"]') || box.querySelector('[data-gids~="' + id + '"]');
}

function updateText(conv, id, text) {
  if (!S.conv || conv !== S.conv.sid) return;
  const it = S.items.find(x => x.id === id);
  if (it) { it.text = text; it.thinking = false; }
  const row = findRow(conv, id);
  if (!row) return;
  if (row.dataset.gkind) paintGroup(row, row.dataset.gkind, groupRowItems(row));
  else paintBubble(row, it || { text: text });
  scrollBottom();
}

function patchItem(conv, m) {
  const it = S.items.find(x => x.id === m.id);
  if (it) {
    if (m.text != null) it.text = m.text;
    if (m.thinking != null) it.thinking = m.thinking;
    if (m.opacity != null) it.opacity = m.opacity;
    if (m.thought != null) it.thought = m.thought;
    // 拆分任务的子任务卡片是先落一张空的、跑完再回填产出的，所以这两项必须能更新
    if (m.toolDetail != null) it.toolDetail = m.toolDetail;
    if (m.toolSummary != null) it.toolSummary = m.toolSummary;
  }
  const row = findRow(conv, m.id);
  if (!row) return;
  if (row.dataset.gkind) paintGroup(row, row.dataset.gkind, groupRowItems(row));
  else paintBubble(row, it || m);
}

function removeItem(conv, id) {
  if (!S.conv || conv !== S.conv.sid) return;
  S.items = S.items.filter(x => x.id !== id);
  renderItems(false);
}

/* 贴底跟随：只有「用户自己没往上翻」时才自动滚到底。
   以前滚动监听无条件回弹，导致划到底后再往上滑会被立刻拉回去（用户实测的 bug）。 */
let stickBottom = true;    // 是否跟随新内容自动到底
let selfScroll = false;    // 这次 scroll 事件是不是我们自己设 scrollTop 触发的

function nearBottom(pad) {
  const box = $('#msgs');
  return box.scrollHeight - box.scrollTop - box.clientHeight < pad;
}

function paintBottomBtn() {
  $('#btnBottom').classList.toggle('hidden', nearBottom(40));
}

function scrollBottom(force) {
  const box = $('#msgs');
  if (force) stickBottom = true;
  if (!stickBottom) { paintBottomBtn(); return; }   // 用户在翻历史，别抢他的滚动条
  const jump = () => {
    if (!stickBottom) return;
    const max = box.scrollHeight - box.clientHeight;
    if (max - box.scrollTop > 1) { selfScroll = true; box.scrollTop = max; }
    paintBottomBtn();
  };
  jump();
  requestAnimationFrame(jump);   // 图片/字体晚一步把高度撑开时再补一次
}

function renderStatus(st) {
  if (!st) return;
  S.state = st.state;
  $('#busy').textContent = st.busy || '';
  $('#statusLine').textContent = st.status || '';
  const b = $('#btnSend');
  b.textContent = st.sendLabel || '发送';
  b.className = 'send' + (st.state === 'running' ? ' stop' : st.state === 'stopped' ? ' resume' : '');
  $('#btnRegen').classList.toggle('off', !st.canRegenerate);
  $('#statusLine').title = st.status || '';
}

function setTabUI(t) {
  // 集群标签页已撤掉，界面永远停在「AI 助手」；内核里 kind=2 的历史会话仍能正常读取
  S.tab = 0;
  document.querySelectorAll('#seg button').forEach(b =>
    b.classList.toggle('on', +b.dataset.tab === S.tab));
  $('#btnClusterTpl').classList.add('hidden');
  renderAgents();
  $('#mode').classList.remove('hidden');
  $('#input').placeholder = '说点什么…（Enter 发送 / Shift+Enter 换行 / Esc 停止）';
  document.querySelector('#composer').dataset.tab = S.tab;
}

function setTheme(t) {
  document.body.classList.toggle('dark', t === 'dark');
  document.body.classList.toggle('light', t !== 'dark');
  $('#btnTheme').textContent = t === 'dark' ? '☀️' : '🌙';
}
/* ---------------- Agent 名片（集群） ---------------- */
let agentCardSid = 0;
const stCls = s => s === '运行中' ? 'run' : s === '完成' ? 'done' : s === '失败' ? 'bad' :
  s === '已停止' ? 'stop' : 'idle';
const stText = a => a.activity || (a.status === '运行中' ? '正在干活…' :
  a.status === '待命' ? '本轮没上场' : a.status === '空闲' ? '还没派过活' : a.status);

function renderAgents() {
  const box = $('#agents');
  if (!box) return;
  const list = S.tab === 1 ? (S.agents || []) : [];
  if (!list.length) { box.classList.add('hidden'); box.innerHTML = ''; return; }
  box.classList.remove('hidden');
  const running = list.filter(a => a.status === '运行中').length;
  let html = '<div class="ag-head"><span class="ag-title">👥 本场名册 ' + list.length + ' 人</span>' +
    (running ? '<span class="ag-run">● ' + running + ' 人正在干活</span>' : '') +
    '<span class="ag-hint">点名片看它在干什么</span></div><div class="ag-list">';
  for (const a of list) {
    html += '<button class="ag-card' + (a.active ? '' : ' off') + '" data-sid="' + a.sid + '">' +
      '<i class="ag-av" style="background:' + esc(a.color || '#4A90D9') + '">' + esc(a.avatar || 'A') + '</i>' +
      '<span class="ag-main"><span class="ag-name">' + esc(a.name) + '</span>' +
      '<span class="ag-act">' + esc(stText(a)) + '</span></span>' +
      '<em class="ag-st ' + stCls(a.status) + '">' + esc(a.status) + '</em></button>';
  }
  box.innerHTML = html + '</div>';
  box.querySelectorAll('.ag-card').forEach(el => {
    el.onclick = () => {
      const a = (S.agents || []).find(x => String(x.sid) === el.dataset.sid);
      if (a) openAgentCard(a);
    };
  });
}

function agentSec(title, text, id, cls) {
  const d = document.createElement('div');
  d.className = 'ac-sec';
  const h = document.createElement('div');
  h.className = 'ac-h'; h.textContent = title;
  const b = document.createElement('div');
  b.className = 'ac-b' + (cls ? ' ' + cls : '');
  if (id) b.id = id;
  b.textContent = text || '（暂无）';
  d.append(h, b);
  return d;
}

function openAgentCard(a) {
  agentCardSid = a.sid;
  const body = openModal('agent', 'Agent 名片 · ' + a.name, [{ t: '关闭' }]);
  const box = document.createElement('div');
  box.className = 'acard';
  const head = document.createElement('div');
  head.className = 'ac-head';
  const av = document.createElement('i');
  av.className = 'ag-av big';
  av.style.background = a.color || '#4A90D9';
  av.textContent = a.avatar || 'A';
  const meta = document.createElement('div');
  meta.className = 'ac-meta';
  const n = document.createElement('div');
  n.className = 'ac-name';
  n.innerHTML = esc(a.name) + ' <em id="acSt" class="ag-st ' + stCls(a.status) + '">' + esc(a.status) + '</em>' +
    (a.active ? '' : ' <em class="ag-st idle">本轮待命</em>');
  const now = document.createElement('div');
  now.className = 'ac-now';
  now.id = 'acNow';
  now.textContent = stText(a);
  meta.append(n, now);
  head.append(av, meta);
  box.appendChild(head);
  if (a.task) box.appendChild(agentSec('🎯 分工', a.task, 'acTask'));
  if (a.result) box.appendChild(agentSec('📦 产出（这一轮交给指挥官的）', a.result, 'acRes', 'md'));
  if (a.text && a.text !== a.result) box.appendChild(agentSec('📝 输出', a.text, 'acOut', 'md'));
  box.appendChild(agentSec('🔧 过程（工具调用）', a.log, 'acLog', 'log'));
  body.appendChild(box);
}

/* 详情开着的时候跟着实时刷新（不重建，免得打断阅读） */
function refreshAgentCard() {
  if (modalName !== 'agent' || !agentCardSid) return;
  const a = (S.agents || []).find(x => x.sid === agentCardSid);
  if (!a) return;
  const st = $('#acSt');
  if (st) { st.textContent = a.status; st.className = 'ag-st ' + stCls(a.status); }
  const now = $('#acNow'); if (now) now.textContent = stText(a);
  const set = (id, v) => { const e = $('#' + id); if (e && v) e.textContent = v; };
  set('acRes', a.result);
  set('acOut', a.text && a.text !== a.result ? a.text : '');
  set('acLog', a.log);
}

/* ---------------- 事件分发 ---------------- */
function handleEvent(m) {
  switch (m.ev) {
    case 'uiReady':
      call('boot');
      break;
    case 'boot':
      S.version = m.version || ''; S.theme = m.theme;
      S.settings = m.settings || {}; S.prompts = m.prompts || [];
      S.clusterTemplates = m.clusterTemplates || []; S.skills = m.skills || [];
      S.pet = m.pet !== false; S.update = m.update || null;
      applySettings();
      setTheme(m.theme);
      setTabUI(m.tab || 0);
      S.convs = m.convs || []; S.conv = m.conv; S.items = m.items || [];
      S.agents = m.agents || [];
      renderConvs(); renderItems(); renderAgents(); renderStatus(m.status);
      if (S.update) showUpdatePill(S.update);
      document.title = 'deepsleep v' + S.version;
      break;
    case 'tab':
      setTabUI(m.tab || 0);
      S.convs = m.convs || []; S.conv = m.conv; S.items = m.items || [];
      S.agents = m.agents || [];
      renderConvs(); renderItems(); renderAgents(); renderStatus(m.status);
      break;
    case 'convs':
      if (m.tab != null && m.tab !== S.tab) break;
      S.convs = m.convs || []; renderConvs();
      break;
    case 'conv':
      if (m.tab != null) setTabUI(m.tab);
      S.convs = m.convs || []; S.conv = m.conv; S.items = m.items || [];
      S.agents = m.agents || [];
      renderConvs(); renderItems(); renderAgents();
      if (m.status) renderStatus(m.status);
      break;
    case 'agents':
      if (m.kind !== 2) break;
      if (m.conv != null && S.conv && m.conv !== S.conv.sid) break;
      S.agents = m.agents || [];
      renderAgents();
      refreshAgentCard();
      break;
    case 'add': if (m.kind === kind()) addItem(m.item); break;
    case 'delta': updateText(m.conv, m.id, m.text); break;
    case 'msgUpdate': patchItem(m.conv, m); break;
    case 'msgRemove': removeItem(m.conv, m.id); break;
    case 'status': if (m.kind === kind() && (m.conv == null || !S.conv || m.conv === S.conv.sid)) renderStatus(m); break;
    case 'theme': setTheme(m.theme); break;
    case 'mode': $('#mode').value = m.mode; break;
    case 'fast': $('#tgFast').checked = !!m.on; break;
    case 'local': $('#tgLocal').checked = !!m.on; break;
    case 'search': $('#tgSearch').checked = !!m.on; break;
    case 'toast': toast(m.text, m.level); break;
    case 'insert': setInput(m.text); break;
    case 'memory': openMemory(m); break;
    case 'skills': S.skills = m.list || []; if (modalName === 'skills') openSkills(m); break;
    case 'settings': S.settings = m.settings || {}; applySettings(); if (modalName === 'settings') openSettings(m.settings); break;
    case 'apiModels': {
      apiModelList = m.ok ? (m.models || []) : null;
      const st = $('#apiModelsStatus');
      if (st) {
        st.textContent = m.ok
          ? ('✅ 拉到 ' + m.count + ' 个模型'
             + (m.freeCount ? '，其中 ' + m.freeCount + ' 个确认免费（🆓 标出）' : '')
             + '，在下面挑一个即可。')
          : ('❌ ' + (m.error || '拉取失败'));
        st.style.color = m.ok ? '' : '#c0392b';
      }
      renderModelPicker();
      if (modelPickerRefresh) modelPickerRefresh();   // 对话框里的选择器开着就地刷新
      releaseBtn('btnFetchModels');   // 结果到了就把按钮恢复，别一直停在「正在拉取…」
      break;
    }
    case 'apiTest': {
      const st = $('#apiTestResult');
      if (st) {
        st.textContent = m.ok
          ? ('✅ 连接正常 · ' + m.model + ' · ' + m.ms + ' ms · 模型回了：' + (m.reply || '（空）'))
          : ('❌ ' + (m.error || '连接失败'));
        st.style.color = m.ok ? '' : '#c0392b';
      }
      releaseBtn('btnApiTest');
      break;
    }
    case 'oauthBind': {
      const out = $('#oauthResult');
      const btn = $('#btnOAuthBind');
      if (btn) { btn.disabled = false; }
      if (m.phase === 'waiting') break;   // 只是「窗口已开」的进度提示
      if (m.ok) {
        // Key 内核已经落盘了，这里把界面同步过来，让用户看得见填了什么
        if ($('#apiKey')) $('#apiKey').value = '••••••••（已保存，尾号 ' + (m.keyTail || '') + '）';
        if ($('#apiUrl')) $('#apiUrl').value = m.url || '';
        if (m.format && $('#apiFormat')) $('#apiFormat').value = m.format;
        if (out) {
          // 用主题变量，深浅色都对；写死绿色在浅色下会发灰
          out.style.color = 'var(--blue)';
          out.textContent = '✅ 已绑定 ' + m.provider + '，Key 已保存（尾号 ' + (m.keyTail || '') + '）。'
            + '下面已经自动拉回模型列表，挑一个再点「保存」即可。';
        }
      } else if (out) {
        out.style.color = '#c0392b';
        out.textContent = '❌ 绑定失败：' + (m.error || '未知错误') + '　（可以改用下面的「按引导绑定」）';
      }
      break;
    }
    case 'ask': showAsk(m); break;
    case 'askClose': closeAsk(m.askId); break;
    case 'update': S.update = m.update; showUpdatePill(m.update); break;
    case 'updateLatest': S.update = null; $('#btnUpdate').classList.add('hidden'); toast(m.text); break;
    case 'updateProgress': updateProgress(m.percent, m.status); break;
    case 'updateStatus': updateProgress(null, m.text, m.done); break;
    case 'restarting': restarting(); break;
    case 'openMain': call('showWindow'); break;
    case 'pet': S.pet = !!m.enabled; if (modalName === 'settings') applySettings(); break;
    case 'hostInfo': S.host = m; if (modalName === 'settings') openSettings(); break;
    case 'coreLog': {
      const b = $('#coreLogBox');
      if (b) { b.textContent = (b.textContent + '\n' + m.text).split('\n').slice(-8).join('\n'); b.scrollTop = b.scrollHeight; }
      break;
    }
  }
}

/* ---------------- 提示 ---------------- */
function toast(text, level) {
  const d = document.createElement('div');
  d.className = 'toast' + (level === 'error' ? ' error' : '');
  d.textContent = text;
  $('#toasts').appendChild(d);
  setTimeout(() => d.remove(), level === 'error' ? 9000 : 4200);
}

function confirmBox(title, text, onYes, yesText) {
  const body = openModal('confirm', title, [
    { t: '取消' },
    { t: yesText || '确定', primary: true, fn: () => { onYes(); } },
  ]);
  const p = document.createElement('div');
  p.style.cssText = 'margin:6px 0 14px;line-height:1.6;font-size:12.5px';
  p.textContent = text;
  body.appendChild(p);
}

/* ---------------- 右键菜单 ---------------- */
let curMenu = null;
function menuAt(x, y, items) {
  closeMenu();
  const m = document.createElement('div');
  m.className = 'menu';
  for (const it of items) {
    if (it.sep) { const s = document.createElement('div'); s.className = 'sep'; m.appendChild(s); continue; }
    const b = document.createElement('button');
    b.textContent = it.text;
    b.onclick = () => { closeMenu(); it.fn(); };
    m.appendChild(b);
  }
  document.body.appendChild(m);
  m.style.left = Math.min(x, innerWidth - m.offsetWidth - 6) + 'px';
  m.style.top = Math.min(y, innerHeight - m.offsetHeight - 6) + 'px';
  curMenu = m;
}
function closeMenu() { if (curMenu) { curMenu.remove(); curMenu = null; } }
addEventListener('click', closeMenu, true);
addEventListener('resize', closeMenu);

function convMenu(ev, c) {
  menuAt(ev.clientX, ev.clientY, [
    { text: c.pinned ? '取消置顶' : '置顶', fn: () => call('pinConv', { sid: c.sid, pinned: !c.pinned }) },
    { text: '上移', fn: () => call('moveConv', { sid: c.sid, dir: -1 }) },
    { text: '下移', fn: () => call('moveConv', { sid: c.sid, dir: 1 }) },
    { sep: true },
    { text: '重命名…', fn: () => renameBox(c) },
    { text: '删除', fn: () => confirmBox('删除对话', '确认删除「' + c.title + '」？删除后不可恢复。', () => call('deleteConv', { sid: c.sid }), '删除') },
  ]);
}

function renameBox(c) {
  const body = openModal('rename', '重命名对话', [
    { t: '取消' },
    { t: '保存', primary: true, fn: () => { const v = $('#rnInput').value.trim(); if (v) call('renameConv', { sid: c.sid, title: v }); } },
  ]);
  const f = document.createElement('div');
  f.className = 'field';
  f.innerHTML = '<input id="rnInput" type="text">';
  body.appendChild(f);
  $('#rnInput').value = c.title;
  setTimeout(() => { $('#rnInput').focus(); $('#rnInput').select(); }, 30);
}
/* ---------------- 弹窗骨架 ---------------- */
let modalName = '';
// internal=true 表示是 openModal 内部在换弹窗，不算「用户自己关掉的」
function closeModal(internal) {
  const wasUpdate = modalName === 'update';
  if (activeAsk && modalName.indexOf('ask:') === 0) {
    const id = activeAsk;
    activeAsk = null;
    call('answer', { askId: id, yes: false, remember: false });
  }
  $('#modalRoot').classList.remove('on');
  $('#modalRoot').innerHTML = '';
  modalName = '';
  if (wasUpdate && !internal) updateDismissed = true;
}
function openModal(name, title, buttons) {
  closeModal(true);
  modalName = name;
  const wrap = document.createElement('div');
  wrap.className = 'backdrop';
  const m = document.createElement('div');
  m.className = 'modal';
  const h = document.createElement('h3');
  h.textContent = title;
  const body = document.createElement('div');
  body.className = 'body';
  const foot = document.createElement('div');
  foot.className = 'foot';
  for (const b of (buttons || [{ t: '关闭' }])) {
    const btn = document.createElement('button');
    btn.className = 'btn' + (b.primary ? ' primary' : '') + (b.danger ? ' danger' : '');
    btn.textContent = b.t;
    btn.onclick = () => { if (!b.fn || b.fn() !== false) closeModal(); };
    foot.appendChild(btn);
  }
  m.append(h, body, foot);
  wrap.appendChild(m);
  wrap.onclick = e => { if (e.target === wrap) closeModal(); };
  const root = $('#modalRoot');
  root.innerHTML = '';
  root.appendChild(wrap);
  root.classList.add('on');
  return body;
}
function field(label, id, value, type, hint) {
  const d = document.createElement('div');
  d.className = 'field';
  const t = type === 'textarea' ? 'textarea' : 'input';
  d.innerHTML = '<label for="' + id + '">' + esc(label) + '</label>' +
    (type === 'select'
      ? '<select id="' + id + '"></select>'
      : '<' + t + ' id="' + id + '" type="' + (type || 'text') + '">') +
    (hint ? '<div class="hint">' + esc(hint) + '</div>' : '');
  const el = d.querySelector('#' + id);
  if (type !== 'select') el.value = value == null ? '' : value;
  return d;
}
function ck(label, id, on) {
  const d = document.createElement('label');
  d.className = 'ck';
  d.innerHTML = '<input type="checkbox" id="' + id + '"><span>' + esc(label) + '</span>';
  d.querySelector('#' + id).checked = !!on;
  return d;
}
function val(id) { const e = $('#' + id); return e ? e.value.trim() : ''; }
function chk(id) { const e = $('#' + id); return !!(e && e.checked); }

/* ---------------- 记忆 ---------------- */
function openMemory(m) {
  if (!m) { call('memoryGet'); return; }
  const body = openModal('memory', '🧠 长期记忆（跨会话生效）', [
    { t: '取消' },
    { t: '清空', danger: true, fn: () => call('memoryClear') },
    { t: '保存', primary: true, fn: () => call('memorySave', { text: val('memText') }) },
  ]);
  const f = document.createElement('div');
  f.className = 'field';
  f.innerHTML = '<textarea id="memText" placeholder="每行一条记忆，例如：\n用户是产品经理，偏好简洁的答复\n项目约定：代码要写注释"></textarea>';
  body.appendChild(f);
  $('#memText').value = m.text || '';
  setTimeout(() => { const t = $('#memText'); if (t) t.focus(); }, 30);
}

/* ---------------- 技能 ---------------- */
function openSkills(m) {
  const list = (m && m.list) || S.skills || [];
  const body = openModal('skills', '🧩 技能管理', [{ t: '关闭' }]);
  if (!m) call('skills');
  const intro = document.createElement('div');
  intro.style.cssText = 'font-size:12px;color:var(--muted);margin:8px 0';
  intro.textContent = '已安装技能（AI 识别技能名后会自动套用说明）：共 ' + list.length + ' 个';
  body.appendChild(intro);
  for (const s of list) {
    const d = document.createElement('div');
    d.className = 'list-item';
    d.innerHTML = '<div class="n"><div>' + esc(s.name) + '</div><div class="d">' + esc(s.description || '') + '</div></div>';
    const del = document.createElement('button');
    del.className = 'btn danger'; del.textContent = '删除';
    del.onclick = () => call('skillRemove', { name: s.name });
    d.appendChild(del);
    body.appendChild(d);
  }
  const f = document.createElement('div');
  f.className = 'field';
  f.innerHTML = '<label>从 URL 安装（SKILL.md 直链）</label><input id="skillUrl" type="text" placeholder="https://.../SKILL.md">';
  body.appendChild(f);
  const row = document.createElement('div');
  row.style.cssText = 'display:flex;gap:8px;margin:4px 0 12px';
  const b1 = document.createElement('button');
  b1.className = 'btn'; b1.textContent = '⬇️ 从 URL 安装';
  b1.onclick = () => { const u = val('skillUrl'); if (u) call('skillInstallUrl', { url: u }); };
  const b2 = document.createElement('button');
  b2.className = 'btn'; b2.textContent = '📁 从本地文件夹安装';
  b2.onclick = () => call('pickFolder');
  row.append(b1, b2);
  body.appendChild(row);
}
/* ---------------- 超级连接 ---------------- */
let slTimer = null;
function stopSlPoll() { if (slTimer) { clearInterval(slTimer); slTimer = null; } }

/* ---------------- 应用内远程桌面 / 完整控制台 ----------------
   桌面端界面本身就是 WebView2 / Photino 网页，再走 openUrl 会拉起系统浏览器 ——
   等于又开一个外壳：会话不共享、深色模式对不上、切回来还得 Alt+Tab，手机上更是直接
   跳出去回不来。这里在同一个窗口里用 iframe 承载对端页面。
   只有「在新窗口打开」才真的交给系统浏览器（用户主动要的时候）。 */
const RD_URL = { url: '', title: '' };

function openInApp(url, title, info) {
  const root = $('#rdRoot'), frame = $('#rdFrame');
  if (!root || !frame) { call('openUrl', { url }); return; }   // 老界面兜底：还是开外部浏览器
  RD_URL.url = url;
  RD_URL.title = title || '远程桌面';
  $('#rdTitle').textContent = RD_URL.title;
  $('#rdInfo').textContent = info || '';
  frame.src = url;
  root.classList.remove('hidden');
}

function closeInApp() {
  const root = $('#rdRoot'), frame = $('#rdFrame');
  if (!root || !frame) return;
  root.classList.add('hidden');
  frame.src = 'about:blank';   // 断掉里面的 SSE 推流，别让它在后台继续拉画面
  RD_URL.url = '';
}

function wireRd() {
  const root = $('#rdRoot');
  if (!root) return;
  $('#rdBack').onclick = () => closeInApp();
  $('#rdReload').onclick = () => { const f = $('#rdFrame'); if (f) f.src = f.src; };
  $('#rdOpen').onclick = () => { if (RD_URL.url) call('openUrl', { url: RD_URL.url }); };
  // 内嵌期间 Esc 当「返回」，但别抢输入框和 iframe 里的 Esc（iframe 内的按键不会冒泡到本页，
  // 所以这里只在焦点还留在外层时才生效，安全）。
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && !$('#rdRoot').classList.contains('hidden')) {
      const t = e.target;
      if (t && t.tagName === 'IFRAME') return;
      closeInApp();
    }
  });
}

function openSuperLink() {
  const body = openModal('superlink', '🔗 超级连接', [{ t: '关闭', fn: stopSlPoll }]);
  const box = document.createElement('div');
  box.innerHTML =
    '<div class="hint">让另一台设备（手机 / 平板 / 另一台电脑）也能用这台机器上的 deepsleep，远程桌面连上就能用：<br>' +
    '· 想让别人连你 → 点「生成配对码」，把 6 位数字给对方；<br>' +
    '· 想连别人 → 在下面输入对方显示的 6 位数字。</div>' +
    '<div class="field" style="display:flex;gap:8px;margin-top:12px">' +
      '<button class="btn" id="slHost">生成配对码</button>' +
      '<button class="btn" id="slStop">断开</button>' +
    '</div>' +
    '<div id="slCodeBox" style="display:none;text-align:center;margin:4px 0 12px">' +
      '<div class="hint">把这 6 位数字给对方（5 分钟内有效）</div>' +
      '<div id="slCode" style="font:700 40px/1.45 ui-monospace,Consolas,monospace;letter-spacing:.22em"></div>' +
    '</div>' +
    '<div class="field"><label>配对：输入对方的 6 位配对码</label>' +
      '<div style="display:flex;gap:8px">' +
        '<input id="slJoinCode" maxlength="6" inputmode="numeric" placeholder="000000" style="flex:1">' +
        '<button class="btn" id="slJoin">连接</button>' +
      '</div></div>' +
    '<div id="slState" class="hint" style="margin-top:12px;min-height:20px"></div>' +
    '<div id="slActions" style="display:none;gap:8px;margin-top:6px">' +
      '<button class="btn" id="slRd">🖥 打开远程桌面</button>' +
      '<button class="btn" id="slFull">💬 打开完整控制台</button>' +
    '</div>';
  body.appendChild(box);

  $('#slHost').onclick = () => { $('#slHost').disabled = true; call('superlinkHost').then(updateSl); };
  $('#slStop').onclick = () => call('superlinkStop').then(updateSl);
  $('#slJoin').onclick = () => {
    const c = ($('#slJoinCode').value || '').replace(/\D/g, '');
    if (c.length !== 6) { $('#slState').textContent = '配对码是 6 位数字。'; return; }
    call('superlinkJoin', { code: c }).then(updateSl);
  };
  $('#slJoinCode').addEventListener('input', function () {
    this.value = this.value.replace(/\D/g, '').slice(0, 6);
    if (this.value.length === 6) $('#slJoin').click();
  });

  stopSlPoll();
  slTimer = setInterval(() => {
    if (modalName !== 'superlink') { stopSlPoll(); return; }
    call('superlinkStatus').then(updateSl);
  }, 1500);
  call('superlinkStatus').then(updateSl);
}

function updateSl(st) {
  if (!st || !st.state) return;
  S.sl = st;
  const codeEl = $('#slCode');
  if (!codeEl) return;
  const isHost = st.role === 'host';
  const active = st.state !== 'idle';
  codeEl.textContent = st.code || '';
  $('#slCodeBox').style.display = (isHost && st.code && active) ? 'block' : 'none';
  $('#slHost').disabled = (st.state === 'waiting' || st.state === 'connecting');
  const txt = {
    idle: '没在连接。',
    waiting: st.message || '等对方连过来…',
    connecting: st.message || '正在连…',
    connected: st.message || '已连上。',
    error: st.message || '出错了。',
  }[st.state] || st.message || '';
  $('#slState').textContent = (active && st.role ? '【' + (isHost ? '被连' : '去连') + '】' : '') + txt;
  const acts = $('#slActions');
  if (st.state === 'connected' && st.baseUrl) {
    acts.style.display = 'flex';
    const base = String(st.baseUrl).replace(/\/+$/, '');
    const tk = encodeURIComponent(st.remoteToken || '');
    // 令牌放 # 片段：片段不发给服务器，也不会被隧道商（serveo 免费版）的浏览器警告页
    // 「Continue」表单提交（action="" 的 GET）把查询串顶掉 —— 之前 rd.html 报「缺少配对令牌」就是这个原因。
    //
    // ⚠ 这里用应用内覆盖层承载，不再 openUrl 拉系统浏览器（桌面端本身就是 WebView）。
    const peer = st.peer ? '对端：' + st.peer : '';
    const via = st.p2p ? '点对点直连' : '内置隧道';
    // u 和 t 都放 # 片段：query 会被 serveo 免费隧道那个英文警告页的
    // 「Continue」表单（action="" 的 GET 提交）整条顶掉，放片段才不会被带走。
    $('#slRd').onclick = () => openInApp(
      base + '/web/core/rd.html#u=' + encodeURIComponent(base) + '&t=' + tk,
      '🖥 远程桌面', via + (peer ? ' · ' + peer : ''));

    // 「完整控制台」是对端内核的整站页面。它需要带 u=（对端地址）才能知道自己该连谁，
    // 且它自己是完整 HTML 页面，塞进 iframe 完全没问题。
    const fullUrl = base + '/web/core/#u=' + encodeURIComponent(base) + '&t=' + tk;
    $('#slFull').onclick = () => openInApp(fullUrl, '💬 完整控制台', via + (peer ? ' · ' + peer : ''));
  } else {
    acts.style.display = 'none';
  }
}

/* ---------------- 设置 ---------------- */
function applySettings() {
  const s = S.settings || {};
  if ($('#mode') && s.mode) $('#mode').value = s.mode;
  if ($('#tgFast')) $('#tgFast').checked = !!s.fast;
  if ($('#tgLocal')) $('#tgLocal').checked = !!s.local;
  if ($('#tgSearch')) $('#tgSearch').checked = !!s.search;
  if (s.theme) setTheme(s.theme);
  renderModelLabel();   // 顶部「🧠 模型」始终显示当前模型
}

/* ---------------- 外部 API：联网模型列表 ---------------- */
// 上一次「联网获取模型列表」的结果。存在全局是为了弹窗被重建（保存后内核会回推 settings 事件）时不丢。
let apiModelList = null;
let apiModelSource = '';   // 这份列表是从哪个地址拉来的，换平台后要作废
let modelPickerRefresh = null;   // 对话框内的模型选择器开着时，拉到列表就地刷新

// 「正在测试…」这类按钮的统一收尾：内核一有结果就恢复。
// 以前只靠 setTimeout 兜底，结果明明回来了按钮还写着「正在测试…」，看着就像卡死了。
const BTN_BUSY = new Map();
function markBtnBusy(btn, busyText) {
  if (!btn) return;
  if (!BTN_BUSY.has(btn)) BTN_BUSY.set(btn, btn.textContent);
  btn.disabled = true;
  btn.textContent = busyText;
}
function releaseBtn(id) {
  const btn = $('#' + id);
  if (!btn) return;
  btn.disabled = false;
  if (BTN_BUSY.has(btn)) { btn.textContent = BTN_BUSY.get(btn); BTN_BUSY.delete(btn); }
}

function renderModelPicker() {
  const wrap = $('#modelPickerWrap');
  const sel = $('#modelPicker');
  if (!wrap || !sel) return;
  const list = apiModelList;
  if (!list || !list.length) { wrap.style.display = 'none'; return; }
  wrap.style.display = '';
  const cur = val('apiModel');
  sel.innerHTML = list.map((m, i) => {
    const name = m.label && m.label !== m.id ? (m.label + '  ·  ' + m.id) : m.id;
    return '<option value="' + i + '">' + (m.free ? '🆓 ' : '') + esc(name) + '</option>';
  }).join('');
  const hit = list.findIndex(m => m.id === cur);
  sel.value = String(hit >= 0 ? hit : 0);
  sel.onchange = () => { const m = list[+sel.value]; if (m) $('#apiModel').value = m.id; };
}

/* ---------------- 对话框内的模型选择器 ----------------
   以前换模型只能进设置、点「联网获取」、再从下拉里挑 —— 三步而且藏在里面。
   现在顶部就有一个按钮，点开直接搜、直接切；列表没拉过就自动拉一次。 */
function modelShortName() {
  const m = String((S.settings || {}).apiModel || '');
  if (!m) return '模型';
  return m.length > 22 ? m.slice(0, 21) + '…' : m;
}

function renderModelLabel() {
  const el = $('#modelText');
  if (el) el.textContent = modelShortName();
}

function openModelPicker() {
  const s = S.settings || {};
  // closeOnPick：选完就关，不打断会话
  const body = openModal('model', '🧠 切换模型', [{ t: '关闭' }]);

  const head = document.createElement('div');
  head.className = 'hint';
  head.textContent = '当前：' + (s.apiModel || '（未设置）') + '　·　' + (s.apiUrl || '');
  body.appendChild(head);

  const filter = document.createElement('input');
  filter.type = 'text';
  filter.placeholder = '搜索模型名（例如 flash / qwen / deepseek）';
  filter.style.margin = '10px 0 6px';
  body.appendChild(filter);

  const status = document.createElement('div');
  status.className = 'hint';
  body.appendChild(status);

  const listBox = document.createElement('div');
  listBox.className = 'scroll';
  listBox.style.cssText = 'max-height:46vh;margin-top:6px';
  body.appendChild(listBox);

  function paint() {
    const kw = filter.value.trim().toLowerCase();
    const all = apiModelList || [];
    const hits = kw ? all.filter(m => (m.id + ' ' + (m.label || '')).toLowerCase().includes(kw)) : all;
    if (!hits.length) {
      listBox.innerHTML = '<div class="hint">' + (all.length ? '没有匹配的模型。' : '还没有模型列表。') + '</div>';
      return;
    }
    listBox.innerHTML = hits.slice(0, 400).map(m => {
      const name = (m.label && m.label !== m.id) ? (m.label + '　·　' + m.id) : m.id;
      const cur = m.id === s.apiModel ? ' ✔' : '';
      return '<div class="mrow' + (cur ? ' cur' : '') + '" data-id="' + esc(m.id) + '">'
        + (m.free ? '🆓 ' : '') + esc(name) + cur + '</div>';
    }).join('');
    listBox.querySelectorAll('.mrow').forEach(el => {
      el.onclick = () => {
        call('apiSetModel', { model: el.dataset.id });
        toast('已切换到 ' + el.dataset.id);
        closeModal();
      };
    });
  }

  filter.oninput = paint;
  paint();
  modelPickerRefresh = () => {
    if (modalName !== 'model') { modelPickerRefresh = null; return; }
    status.textContent = '共 ' + (apiModelList || []).length + ' 个模型（🆓 = 确认免费）';
    paint();
  };

  // 列表为空 → 自动联网拉一次（不用手动点）
  if (!apiModelList || apiModelSource !== String(s.apiUrl || '')) {
    if (!s.apiKey) {
      status.textContent = '还没有配置 API Key —— 先去 ⚙ 设置里绑定一个供应商。';
      return;
    }
    status.textContent = '正在联网获取模型列表…';
    apiModelSource = String(s.apiUrl || '');
    call('apiModels', { url: s.apiUrl, key: s.apiKey, format: s.apiFormat });
  } else {
    status.textContent = '共 ' + apiModelList.length + ' 个模型（🆓 = 确认免费）';
  }
}

function openSettings(fresh) {
  const s = fresh || S.settings || {};
  const body = openModal('settings', '⚙ 设置', [
    { t: '取消' },
    { t: '保存', primary: true, fn: () => saveSettings() },
  ]);

  const sect = t => { const d = document.createElement('div'); d.className = 'sect'; d.textContent = t; body.appendChild(d); };

  // ===== 外部 API：① 选平台 → ② 领 Key 并测试 → ③ 联网选模型 =====
  // 「绑定账号」在各家的技术约束下不可能真做成 OAuth 一键授权（硅基流动等只给控制台建 Key，
  // 不开放第三方应用授权），所以这里做成「半自动三步」：一键跳转申请页 → 粘贴 → 一键测试 + 拉模型列表。
  sect('① 绑定平台（内置 18 家带免费额度的平台，选中自动填好地址和协议）');
  const platWrap = document.createElement('div');
  platWrap.className = 'field';
  platWrap.innerHTML = '<label>平台</label><select id="platform"></select><div class="hint" id="platQuota"></div>';
  body.appendChild(platWrap);

  const platforms = s.platforms || [];
  const selPlat = $('#platform');
  selPlat.innerHTML = platforms
    .map((p, i) => '<option value="' + i + '">' + esc(p.name) + '</option>').join('');
  // 按已保存的地址回选平台；匹配不上就落到最后那个「自定义」
  const matchedIdx = platforms.findIndex(p => p.url && p.url.trim() === String(s.apiUrl || '').trim());
  selPlat.value = String(matchedIdx >= 0 ? matchedIdx : Math.max(0, platforms.length - 1));
  const curPlat = () => platforms[+selPlat.value] || {};
  const showQuota = () => { $('#platQuota').textContent = curPlat().quota || ''; };
  showQuota();

  const keyRow = document.createElement('div');
  keyRow.className = 'field';

  // ---- 一键绑定：只在平台真的开放了第三方 OAuth 时出现 ----
  // 各家对 OAuth 的开放程度差很多：硅基流动、智谱只能在控制台手工建 Key；魔搭虽有 OIDC，
  // 但换回的是身份 token（调不了推理接口）。所以这里按平台能力分两种入口，
  // 不会让用户点了「一键绑定」撞到一个必然报错的授权页。
  const oauthMap = {};
  (s.oauthProviders || []).forEach(p => { oauthMap[p.id] = p; });

  const oneClick = document.createElement('button');
  oneClick.className = 'btn';
  oneClick.id = 'btnOAuthBind';
  oneClick.style.margin = '2px 0 4px';
  oneClick.onclick = () => {
    const p = curPlat();
    const op = oauthTarget(p, oauthMap);
    if (!op) return;
    oneClick.disabled = true;
    oneClick.textContent = '等待授权…请在弹出的窗口里登录并点「同意」';
    const out = $('#oauthResult');
    if (out) { out.textContent = '授权窗口已打开。登录并同意后会**自动**填好 Key，不用手动复制粘贴。'; out.style.color = ''; }
    call('oauthBind', { provider: op.id });
    setTimeout(() => { oneClick.disabled = false; syncOAuthBtn(); }, 330000);
  };
  keyRow.appendChild(oneClick);

  const guide = document.createElement('button');
  guide.className = 'btn';
  guide.textContent = '🔑 按引导绑定（跳到官网申请页，手动创建 Key）';
  guide.style.margin = '2px 0 4px';
  guide.onclick = () => {
    const p = curPlat();
    if (p.keyUrl) {
      call('openUrl', { url: p.keyUrl });
      toast('已打开官网申请页 —— 登录后创建 API Key，复制回来粘到下面「API Key」。');
    } else {
      toast('这家没有预设申请页，去官网控制台创建 Key 即可。');
    }
  };
  keyRow.appendChild(guide);

  // 当前平台能不能一键绑定：能 → 显示一键按钮，不能 → 只显示引导按钮
  function oauthTarget(plat, map) {
    if (!plat) return null;
    if (plat.oauthId && map[plat.oauthId]) return map[plat.oauthId];
    return Object.values(map).find(o => o.apiUrl && plat.url && o.apiUrl === plat.url) || null;
  }
  function syncOAuthBtn() {
    const op = oauthTarget(curPlat(), oauthMap);
    oneClick.style.display = op ? '' : 'none';
    guide.style.display = op ? 'none' : '';
    if (op) oneClick.textContent = '⚡ 一键绑定 ' + op.name.split('（')[0] + '（授权登录，自动拿 Key）';
  }
  const oauthOut = document.createElement('div');
  oauthOut.className = 'hint';
  oauthOut.id = 'oauthResult';
  oauthOut.textContent = s.oauthExplain || '';
  keyRow.appendChild(oauthOut);
  body.appendChild(keyRow);

  // ---- 一键恢复：这家之前配好过就直接拉回来（Key / 模型 / 协议一起写回）----
  const restoreRow = document.createElement('div');
  restoreRow.className = 'field';
  const bRestore = document.createElement('button');
  bRestore.className = 'btn';
  bRestore.id = 'btnRestoreProfile';
  bRestore.style.margin = '2px 0 4px';
  bRestore.onclick = () => {
    markBtnBusy(bRestore, '正在恢复…');
    call('apiRestore', { url: val('apiUrl') });
    setTimeout(() => releaseBtn('btnRestoreProfile'), 5000);   // 内核会回推 settings，弹窗重建后按钮自然是新的
  };
  restoreRow.appendChild(bRestore);
  const rHint = document.createElement('div');
  rHint.className = 'hint';
  rHint.id = 'profileHint';
  restoreRow.appendChild(rHint);
  body.appendChild(restoreRow);

  const profiles = s.apiProfiles || [];
  const profileFor = url => profiles.find(x => x.url === String(url || '').trim()) || null;

  function syncRestoreBtn() {
    const p = profileFor(val('apiUrl'));
    if (p) {
      bRestore.style.display = '';
      bRestore.textContent = '↩ 恢复「' + (p.label || p.model || '这家') + '」上次的配置'
        + (p.model ? '（模型 ' + p.model + '）' : '');
      rHint.textContent = '上次保存于 ' + (p.updated || '未知时间') + '，Key 尾号 ' + (p.keyTail || '—')
        + '。点一下就把 Key / 模型 / 协议全部写回并保存。';
    } else {
      bRestore.style.display = 'none';
      rHint.textContent = profiles.length
        ? ('已记住 ' + profiles.length + ' 家供应商的配置：'
           + profiles.map(x => x.label || x.model || x.url).slice(0, 3).join('、')
           + (profiles.length > 3 ? ' 等' : '') + '。切到对应平台就能一键恢复。')
        : '填好 Key 并保存一次，这里就会记住这家供应商的配置；以后切换供应商可以一键恢复，不用重填。';
    }
  }
  syncRestoreBtn();

  selPlat.onchange = () => {
    const p = curPlat();
    showQuota();
    if (p.url) {
      $('#apiUrl').value = p.url;
      $('#apiFormat').value = p.format || 'openai';
      $('#apiModel').value = '';          // 各家的模型 ID 不通用，换了平台必须重新选
    }
    apiModelList = null;                  // 旧列表作废
    renderModelPicker();
    syncOAuthBtn();
    syncRestoreBtn();                     // 换平台后，这家有没有存档要重新判
    const st = $('#apiModelsStatus');
    if (st) { st.textContent = '点了「联网获取模型列表」后在这里挑模型。'; st.style.color = ''; }
  };
  syncOAuthBtn();

  sect('② 粘贴 Key 并测试连接');
  body.appendChild(field('API Key', 'apiKey', s.apiKey, 'password'));
  const testRow = document.createElement('div');
  testRow.className = 'field';
  const bTest = document.createElement('button');
  bTest.className = 'btn';
  bTest.id = 'btnApiTest';
  bTest.textContent = '🔌 测试连接（发一个最小请求验证 Key）';
  bTest.style.margin = '2px 0 4px';
  bTest.onclick = () => {
    markBtnBusy(bTest, '正在测试…');
    const out = $('#apiTestResult');
    if (out) { out.textContent = ''; out.style.color = ''; }
    call('apiTest', { url: val('apiUrl'), key: val('apiKey'), model: val('apiModel'), format: val('apiFormat') });
    setTimeout(() => releaseBtn('btnApiTest'), 35000);   // 兜底：万一事件丢了也别一直卡着
  };
  testRow.appendChild(bTest);
  const testOut = document.createElement('div');
  testOut.className = 'hint';
  testOut.id = 'apiTestResult';
  testRow.appendChild(testOut);
  body.appendChild(testRow);

  sect('③ 选模型（联网拉取该平台的最新模型列表，不用背模型名）');
  const fetchRow = document.createElement('div');
  fetchRow.className = 'field';
  const bFetch = document.createElement('button');
  bFetch.className = 'btn';
  bFetch.id = 'btnFetchModels';
  bFetch.textContent = '🔄 联网获取模型列表';
  bFetch.style.margin = '2px 0 4px';
  bFetch.onclick = () => { fetchModels(true); };
  fetchRow.appendChild(bFetch);
  const mStatus = document.createElement('div');
  mStatus.className = 'hint';
  mStatus.id = 'apiModelsStatus';
  fetchRow.appendChild(mStatus);
  body.appendChild(fetchRow);

  // 拉模型列表：bind 之后自动调，不必手动点。manual=true 表示用户主动点的。
  function fetchModels(manual) {
    if (!val('apiKey')) {
      if (manual) toast('还没填 API Key —— 先点上面的「免费领取 Key」。', 'error');
      return;
    }
    const st = $('#apiModelsStatus');
    if (st) { st.textContent = '正在拉取模型列表…'; st.style.color = ''; }
    markBtnBusy($('#btnFetchModels'), '正在拉取…');
    apiModelSource = val('apiUrl');
    call('apiModels', { url: val('apiUrl'), key: val('apiKey'), format: val('apiFormat') });
    setTimeout(() => releaseBtn('btnFetchModels'), 35000);
  }

  const pickerWrap = document.createElement('div');
  pickerWrap.className = 'field';
  pickerWrap.id = 'modelPickerWrap';
  pickerWrap.innerHTML = '<label>从列表里挑一个（🆓 = 确认免费）</label><select id="modelPicker"></select>';
  body.appendChild(pickerWrap);

  body.appendChild(field('API 模型名', 'apiModel', s.apiModel, 'text',
    '也可以直接手填。少数平台要填的不是模型 ID（如火山方舟填「接入点 ID」）。'));
  const fmt = document.createElement('div');
  fmt.className = 'field';
  fmt.innerHTML = '<label>API 协议格式（选平台时已自动设好，一般不用动）</label><select id="apiFormat">' +
    '<option value="openai">OpenAI 兼容（硅基流动 / 魔搭 / OpenRouter / NVIDIA / Groq / 智谱 / DeepSeek…）</option>' +
    '<option value="anthropic">Anthropic Claude（/v1/messages）</option>' +
    '<option value="gemini">Google Gemini（generateContent 原生接口）</option>' +
    '<option value="responses">OpenAI Responses API（/v1/responses，GPT-5 等新模型）</option>' +
    '</select>';
  body.appendChild(fmt);
  $('#apiFormat').value = s.apiFormat || 'openai';
  $('#apiFormat').onchange = () => {
    // 只在「自定义平台」下才用格式去猜地址；预设平台有自己的地址，别覆盖掉
    const p = curPlat();
    if (p && p.url) return;
    const map = {
      anthropic: 'https://api.anthropic.com/v1/messages',
      gemini: 'https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent',
      responses: 'https://api.openai.com/v1/responses',
      openai: 'https://api.deepseek.com/chat/completions',
    };
    $('#apiUrl').value = map[$('#apiFormat').value] || map.openai;
  };
  body.appendChild(field('API 地址', 'apiUrl', s.apiUrl, 'text', '选平台时已自动填好；只有「自定义平台」才需要手动改。'));

  sect('⚡ 模型快速预设（离线可用，一步填好地址 + 模型 + 协议）');
  const presetSel = document.createElement('div');
  presetSel.className = 'field';
  presetSel.innerHTML = '<label>预设</label><select id="preset"></select>' +
    '<div class="hint">不想联网时可从这里直接选；选定会覆盖上面的地址 / 模型名 / 协议格式。</div>';
  body.appendChild(presetSel);
  const sel = $('#preset');
  sel.innerHTML = '<option value="-1">（不选）</option>' +
    (s.presets || []).map((p, i) => '<option value="' + i + '">' + esc(p.name) + '</option>').join('');
  sel.onchange = () => {
    const i = +sel.value;
    if (i < 0) return;
    const p = s.presets[i];
    $('#apiUrl').value = p.url;
    $('#apiModel').value = p.model;
    // 协议格式必须一起回填：硅基流动等预设是 openai，但 Gemini/Claude 预设不是，
    // 只改地址和模型名会把上一次的格式留在原地，发请求直接报错。
    if (p.format && $('#apiFormat')) {
      $('#apiFormat').value = p.format;
    }
    apiModelList = null;
    renderModelPicker();
  };

  renderModelPicker();
  // 已经绑好 Key 但还没有模型列表 → 打开设置就自动拉一次，不用手动点。
  // （绑定成功时内核也会推一次 apiModels，这里只是覆盖「上次没拉到 / 换了电脑」的情况。）
  if (!apiModelList && val('apiKey')) fetchModels(false);

  const hint = document.createElement('div');
  hint.className = 'field';
  hint.innerHTML = '<div class="hint">' + esc(s.freeKeyHint || '') + '</div>';
  body.appendChild(hint);

  sect('本地大模型（Ollama）');
  body.appendChild(field('快模型名', 'ollamaModel', s.ollamaModel, 'text', '简单任务用，例如 qwen2.5:1.5b'));
  body.appendChild(field('强模型名', 'ollamaModelStrong', s.ollamaModelStrong, 'text', '复杂任务用，例如 qwen3:4b'));
  body.appendChild(field('服务地址', 'ollamaUrl', s.ollamaUrl));
  body.appendChild(ck('启用本地 Ollama 模型（不花 API 额度，适合长任务 / 集群）', 'useOllama', s.useOllama));
  const inst = document.createElement('button');
  inst.className = 'btn'; inst.textContent = '⬇️ 一键安装 Ollama + 大模型（qwen3:4b / qwen2.5:1.5b）';
  inst.style.margin = '6px 0 4px';
  inst.onclick = () => { closeModal(); call('installOllama'); };
  body.appendChild(inst);

  sect('图像生成（CogView-3-Flash）');
  body.appendChild(field('图像 API Key', 'imageApiKey', s.imageApiKey, 'password'));
  body.appendChild(field('图像模型名', 'imageModel', s.imageModel));

  sect('图像理解（GLM-4.6V-Flash 多模态）');
  body.appendChild(field('图像理解 API Key', 'visionApiKey', s.visionApiKey, 'password'));
  body.appendChild(field('图像理解模型名', 'visionModel', s.visionModel));
  body.appendChild(ck('主模型是多模态（图片直接发给主模型，不再调用「看图」工具）', 'multimodalMain', s.multimodalMain));

  sect('工具权限 / 能力开关');
  const modeSel = document.createElement('div');
  modeSel.className = 'field';
  modeSel.innerHTML = '<label>AI 助手模式</label><select id="setMode">' +
    '<option value="chat">chat · 聊天（不改动电脑）</option>' +
    '<option value="work">work · 命令需确认</option>' +
    '<option value="boom">boom · 全自动（命令免确认）</option></select>';
  body.appendChild(modeSel);
  $('#setMode').value = s.mode || 'work';
  body.appendChild(ck('⚡ 极速模式', 'setFast', s.fast));
  body.appendChild(ck('🌐 网络搜索 / 深度研究', 'setSearch', s.search));

  sect('OTA 自动更新');
  body.appendChild(field('更新源（owner/repo 或 update.json 的 URL）', 'updateUrl', s.updateUrl));
  body.appendChild(ck('启动时自动检查更新', 'autoCheckUpdate', s.autoCheckUpdate));
  const lineBox = document.createElement('div');
  lineBox.className = 'field';
  lineBox.innerHTML = '<label>更新线路（下载走哪边）</label>' +
    '<select id="updateSource">' +
    '<option value="gitee">Gitee 国内源（默认，推荐）</option>' +
    '<option value="github">GitHub</option>' +
    '</select>' +
    '<div class="hint">默认走 Gitee：不探速、直接从 Gitee 下（实测快一个数量级），只有 Gitee 连不上或下不动才回退 GitHub；两条线路下完都用官方 SHA256 校验，装的是同一个包。</div>';
  body.appendChild(lineBox);
  $('#updateSource').value = s.updateSource === 'github' ? 'github' : 'gitee';
  body.appendChild(field('录入 GitHub 令牌（多层加密保存，' + (s.tokenSaved ? '已保存' : '未保存') + '）', 'token', '', 'password', '私有仓库或想提高 GitHub API 限额时录入；用 DPAPI 加密存在 data\\github.token，不进配置文件。'));
  const tk = document.createElement('button');
  tk.className = 'btn'; tk.textContent = '🔑 加密保存令牌';
  tk.style.margin = '0 0 6px';
  tk.onclick = () => { const v = val('token'); if (v) call('setToken', { token: v }); };
  body.appendChild(tk);

  sect('初始提示词（AGENT.md）');
  const mdWrap = document.createElement('div');
  mdWrap.className = 'field';
  mdWrap.innerHTML = '<label>所有对话共享，会话里改不了，只能在这里改</label>';
  const mdTa = document.createElement('textarea');
  mdTa.id = 'setAgentMd';
  mdTa.className = 'mdarea';
  mdTa.rows = 8;
  mdTa.spellcheck = false;
  mdTa.value = s.agentMd || '';
  mdTa.placeholder = '例：\n用中文回答，先给结论再给理由。\n叫我老板。\n所有文件默认写到 D:\\work';
  mdWrap.appendChild(mdTa);
  const mdHint = document.createElement('div');
  mdHint.className = 'hint';
  mdHint.textContent = '存在 data\\AGENT.md，会插到 AI 助手 / Agent 集群 / 桌宠所有对话的系统提示最前面；HTML 注释里的内容不会发给模型。保存后立即生效。';
  mdWrap.appendChild(mdHint);
  body.appendChild(mdWrap);

  sect('桌面桌宠');
  body.appendChild(ck('显示桌面鲸鱼桌宠（透明窗口、可拖拽；单击弹出聊天浮窗、右键有菜单）', 'petEnabled', s.petEnabled));

  // 超远程提问：桌面客户端自带内核服务（和内核版 deepsleep-core 是同一份实现）
  if (S.host && S.host.desktop) {
    const h = S.host;
    const link = h.pairLink || h.lanLink || '';
    sect('超远程提问（内网穿透）');
    const box = document.createElement('div');
    box.className = 'field';
    box.innerHTML = '<label>手机 / 别的电脑打开这条链接，就能操控这台电脑</label>' +
      '<div class="hint" style="word-break:break-all;user-select:text">' + esc(link) + '</div>' +
      '<div class="hint">本机服务端口 ' + esc(String(h.corePort || '')) + '，令牌已经带在链接里 —— 等于这台电脑的钥匙，别外传。' +
      (h.tunnelUrl ? '' : '现在这条是局域网地址，只有在同一个网络里能打开。') + '</div>';
    body.appendChild(box);
    const row = document.createElement('div');
    row.className = 'field';
    const bTun = document.createElement('button');
    bTun.className = 'btn';
    bTun.textContent = h.tunnelUrl ? '🌐 关闭公网隧道' : '🌐 开启公网隧道（免注册，外网可用）';
    bTun.onclick = () => {
      bTun.disabled = true;
      bTun.textContent = h.tunnelUrl ? '正在关闭…' : '正在建内置隧道…不用下组件，约 10-40 秒';
      call('coreTunnel', { on: !h.tunnelUrl });
    };
    row.appendChild(bTun);
    const bCopy = document.createElement('button');
    bCopy.className = 'btn';
    bCopy.style.marginLeft = '8px';
    bCopy.textContent = '📋 复制配对链接';
    bCopy.onclick = () => { try { navigator.clipboard.writeText(link); toast('配对链接已复制'); } catch (e) { toast('复制失败，手动选中吧'); } };
    row.appendChild(bCopy);
    body.appendChild(row);
    const logBox = document.createElement('div');
    logBox.className = 'hint';
    logBox.id = 'coreLogBox';
    logBox.style.cssText = 'white-space:pre-wrap;max-height:90px;overflow:auto;font-family:ui-monospace,Consolas,monospace';
    logBox.textContent = h.tunnelUrl ? ('公网地址：' + h.tunnelUrl) : '';
    body.appendChild(logBox);
  }
}

function saveSettings() {
  // 配了外部 Key 就必须有模型名：换平台时模型名会被清空，防止把上一家的模型 ID 带过去。
  // 返回 false 会让弹窗不关闭，用户能接着改（只用 Ollama 的话 apiKey 为空，不受影响）。
  if (val('apiKey') && !val('apiModel')) {
    toast('还没选模型 —— 点「③ 联网获取模型列表」挑一个，或手动填模型名。', 'error');
    return false;
  }
  call('settingsSave', {
    apiUrl: val('apiUrl'), apiModel: val('apiModel'), apiKey: val('apiKey'),
    apiFormat: val('apiFormat'),
    ollamaModel: val('ollamaModel'), ollamaModelStrong: val('ollamaModelStrong'), ollamaUrl: val('ollamaUrl'),
    useOllama: chk('useOllama'),
    imageApiKey: val('imageApiKey'), imageModel: val('imageModel'),
    visionApiKey: val('visionApiKey'), visionModel: val('visionModel'),
    multimodalMain: chk('multimodalMain'),
    updateUrl: val('updateUrl'), autoCheckUpdate: chk('autoCheckUpdate'), updateSource: val('updateSource'),
    petEnabled: chk('petEnabled'),
    mode: val('setMode'), fast: chk('setFast'), search: chk('setSearch'),
    agentMd: val('setAgentMd'),
  }).then(() => toast('设置已保存。'));
}
/* ---------------- 权限确认 ---------------- */
let activeAsk = null;
function showAsk(m) {
  activeAsk = m.askId;
  const body = openModal('ask:' + m.askId, m.title || '确认', [
    { t: m.no || '拒绝', fn: () => call('answer', { askId: m.askId, yes: false, remember: false }) },
    { t: m.yes || '允许', primary: true, fn: () => call('answer', { askId: m.askId, yes: true, remember: chk('askRemember') }) },
  ]);
  const d = document.createElement('div');
  d.style.cssText = 'font-size:12.5px;line-height:1.6;white-space:pre-wrap;margin:6px 0;max-height:320px;overflow:auto';
  d.textContent = m.detail || '';
  body.appendChild(d);
  if (m.check) body.appendChild(ck(m.check, 'askRemember', false));
}
function closeAsk(id) {
  if (modalName === 'ask:' + id) { activeAsk = null; closeModal(); }
}

/* ---------------- 升级 ---------------- */
let updateActive = false;     // 这次会话里确实在跑升级（自动弹进度窗的前提）
let updateDismissed = false;  // 用户已经把进度窗关掉了：别再被进度事件顶回来
let updatePct = null;         // 最后一次进度（关掉再打开时接着显示）
let updateMsg = '';
function showUpdatePill(u) {
  $('#btnUpdate').classList.remove('hidden');
  $('#updateText').textContent = updateActive ? '升级中…' : '有新版 v' + u.version;
}
function onUpdateClick() {
  if (updateActive) { updateDismissed = false; openUpdateProgress(); return; }
  const u = S.update;
  if (!u) { call('checkUpdate'); return; }
  const body = openModal('updateinfo', '发现新版本 v' + u.version, [
    { t: '稍后' },
    // 返回 false：进度窗由 startUpdate 自己开，别让弹窗骨架再关一次
    { t: '立即升级', primary: true, fn: () => { startUpdate(); return false; } },
  ]);
  const d = document.createElement('div');
  d.style.cssText = 'font-size:12.5px;line-height:1.6;white-space:pre-wrap;margin:6px 0 14px';
  d.textContent = '当前版本：v' + u.current + '\n\n更新内容：\n' + (u.notes || '（无更新说明）') +
    '\n\n升级会下载安装包，退出后静默覆盖安装并自动重启。用户数据（API Key、对话历史、记忆、自训练模型）不受影响。\n\n' + (u.route || '');
  body.appendChild(d);
}
function setModalFoot(buttons) {
  const foot = $('#modalRoot').querySelector('.foot');
  if (!foot) return;
  foot.innerHTML = '';
  for (const b of buttons) {
    const btn = document.createElement('button');
    btn.className = 'btn' + (b.primary ? ' primary' : '') + (b.danger ? ' danger' : '');
    btn.textContent = b.t;
    btn.onclick = () => { if (!b.fn || b.fn() !== false) closeModal(); };
    foot.appendChild(btn);
  }
}
function openUpdateProgress(buttons) {
  const body = openModal('update', '正在升级', buttons || [
    { t: '后台继续', fn: () => toast('升级在后台接着下，点左下角「升级中」随时能看进度。') },
    { t: '取消升级', danger: true, fn: () => call('cancelUpdate') },
  ]);
  body.innerHTML = '<div id="upText" style="font-size:12.5px;margin:8px 0"></div>' +
    '<div class="bar"><i id="upBar"></i></div>';
  paintUpdate();
  return body;
}
function paintUpdate() {
  const t = $('#upText'), b = $('#upBar');
  if (t) t.textContent = updateMsg ? updateMsg + (updatePct == null ? '' : '\n进度 ' + Math.round(updatePct) + '%') : '正在准备下载…';
  if (b && updatePct != null) b.style.width = Math.max(0, Math.min(100, updatePct)) + '%';
}
function startUpdate() {
  updateActive = true; updateDismissed = false;
  updatePct = null; updateMsg = '';
  openUpdateProgress();
  call('runUpdate');
}
function updateProgress(p, text, done) {
  const hidden = updateDismissed, wasActive = updateActive;
  if (done) { updateActive = false; updateDismissed = false; }
  // 关掉过就保持关着，只把进度写到左下角小按钮上
  if (!hidden && wasActive) {
    if (modalName !== 'update') openUpdateProgress(done ? [{ t: '关闭' }] : null);
    else if (done) setModalFoot([{ t: '关闭' }]);
  }
  if (text) updateMsg = text;
  if (p != null) updatePct = p;
  if (done) updatePct = null;
  paintUpdate();
  if (!done) {
    $('#btnUpdate').classList.remove('hidden');
    $('#updateText').textContent = p == null ? '升级中…' : '升级中 ' + Math.round(p) + '%';
  } else {
    if (S.update) showUpdatePill(S.update); else $('#btnUpdate').classList.add('hidden');
    if (hidden) toast(text || '升级已结束。');
  }
}
function restarting() {
  toast('升级程序已启动，deepsleep 即将退出并自动重启…');
  // 升级脚本要等 deepsleep.exe 退出才会继续安装，所以这里必须真的退（光弹提示会卡死）
  setTimeout(() => host({ cmd: 'quitApp' }), 900);
}

/* ---------------- 交互接线 ---------------- */
function autosize() {
  const t = $('#input');
  t.style.height = 'auto';
  t.style.height = Math.min(t.scrollHeight, 170) + 'px';
}
function setInput(text) {
  const t = $('#input');
  t.value = text;
  autosize();
  t.focus();
}
function doSend() {
  const t = $('#input');
  const text = t.value.trim();
  const st = S.state;
  if (st === 'idle' && !text) return;
  const target = petMode ? 'pet' : 'main';
  if (st === 'running') { call('send', { kind: kind(), text, target }); return; }
  t.value = ''; autosize();
  call('send', { kind: kind(), text, target });
}

function wire() {
  $('#seg').onclick = e => { const b = e.target.closest('button'); if (b) call('tab', { tab: +b.dataset.tab }); };
  $('#btnTheme').onclick = () => call('setTheme', { theme: document.body.classList.contains('dark') ? 'light' : 'dark' });
  $('#btnSettings').onclick = () => openSettings();
  $('#btnModel').onclick = () => openModelPicker();
  $('#btnLink').onclick = () => openSuperLink();
  wireRd();
  $('#btnNew').onclick = () => call('newConv', { kind: kind() });
  $('#btnMemory').onclick = () => openMemory();
  $('#btnSkills').onclick = () => openSkills();
  $('#btnAttach').onclick = () => call('pickFile', { kind: kind() });
  $('#btnTpl').onclick = e => {
    menuAt(e.clientX, e.clientY - 10, S.prompts.map(p => ({ text: p.name, fn: () => setInput(p.prompt) })));
  };
  $('#btnClusterTpl').onclick = e => {
    menuAt(e.clientX, e.clientY - 10, S.clusterTemplates.map((t, i) => ({
      text: t.name + '（' + t.members.length + ' 人）',
      fn: () => call('clusterTemplate', { index: i }),
    })));
  };
  $('#mode').onchange = e => call('setMode', { mode: e.target.value });
  $('#tgFast').onchange = e => call('setFast', { on: e.target.checked });
  $('#tgLocal').onchange = e => call('setLocal', { on: e.target.checked });
  $('#tgSearch').onchange = e => call('setSearch', { on: e.target.checked });
  $('#btnSend').onclick = () => doSend();
  $('#btnRegen').onclick = () => call('regenerate', { kind: kind() });
  $('#btnClear').onclick = () => confirmBox('清空当前对话？',
    '清空后当前对话的消息与上下文都会被删掉，其他对话不受影响。', () => call('clear', { kind: kind() }), '清空');
  $('#btnBottom').onclick = () => scrollBottom(true);
  $('#msgs').addEventListener('scroll', () => {
    if (selfScroll) { selfScroll = false; paintBottomBtn(); return; }
    stickBottom = nearBottom(40);        // 自己滑回底部附近才恢复自动跟随
    paintBottomBtn();
  });
  $('#convSearch').oninput = e => call('searchConv', { kind: kind(), q: e.target.value });
  $('#btnUpdate').onclick = () => onUpdateClick();
  $('#input').oninput = autosize;
  $('#input').onkeydown = e => {
    if (e.isComposing || e.keyCode === 229) return;   // 输入法组词中，回车/Esc 交给输入法
    if (e.key === 'Escape') { e.preventDefault(); call('stop', { kind: kind() }); return; }
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      if (S.state === 'running') return;   // 运行中回车不打断
      doSend();
    }
  };
  document.addEventListener('click', e => {
    const a = e.target.closest('a');
    if (a) { e.preventDefault(); call('openUrl', { url: a.href }); return; }
    const c = e.target.closest('[data-copy]');
    if (c) {
      const code = c.parentElement.querySelector('code');
      if (code) navigator.clipboard.writeText(code.textContent);
      c.textContent = '已复制';
      setTimeout(() => { c.textContent = '复制'; }, 1200);
    }
  });
}

wire();

/* Windows 版由外壳在导航完成后推 {"ev":"uiReady"}；Photino 没有导航回调，
   由界面主动报个到（外壳收到 shellReady 会立刻回 uiReady + hostInfo）。 */
if (bridge.kind === 'photino') host({ cmd: 'shellReady' });
