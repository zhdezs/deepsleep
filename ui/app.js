'use strict';
/* deepsleep 界面层：只负责画面与交互，逻辑全在内核（通过 JSON 协议调用） */

const $ = s => document.querySelector(s);
const petMode = new URLSearchParams(location.search).has('pet');
if (petMode) document.body.classList.add('pet');

/* ---------------- 与内核的通道 ---------------- */
let seq = 0;
const waiting = new Map();
function host(obj) { try { window.chrome.webview.postMessage(obj); } catch (e) { } }
function call(cmd, args) {
  return new Promise(res => {
    const id = ++seq;
    waiting.set(id, res);
    host(Object.assign({ id, cmd }, args || {}));
  });
}
window.chrome.webview.addEventListener('message', e => {
  const m = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
  if (!m) return;
  if (m.ev) handleEvent(m);
  else if (m.id != null) { const r = waiting.get(m.id); if (r) { waiting.delete(m.id); r(m); } }
});

/* ---------------- 状态 ---------------- */
const S = {
  tab: 0, conv: null, items: [], convs: [], settings: {},
  prompts: [], clusterTemplates: [], skills: [], state: 'idle',
  update: null, pet: true, version: ''
};
const kind = () => (S.tab === 1 ? 2 : 0);

/* ---------------- Markdown（轻量渲染，够用） ---------------- */
function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"]/g,
    c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}
function inline(t) {
  return t.replace(/`([^`]+)`/g, (m, c) => '<code>' + c + '</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>')
    .replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<i>$2</i>')
    .replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g, '<a href="$2">$1</a>');
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
    b.className = 'bubble';
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
    let html = '';
    if (it.speaker) html += '<div class="who">' + esc(it.speaker) + '</div>';
    else if (it.meta && it.meta !== 'AI' && it.meta !== '你') html += '<div class="who">' + esc(it.meta) + '</div>';
    if (it.image) html += '<img src="' + esc(it.image) + '" alt="图片">';
    if (it.attName) html += '<div class="who">📎 ' + esc(it.attName) + '　' + esc(it.attSize) + '</div>';
    html += md(it.text);
    b.innerHTML = html;
  } else {
    const bar = row.querySelector('.sysbar');
    if (bar) bar.textContent = it.text;
  }
}

function renderItems() {
  const box = $('#msgs');
  box.innerHTML = '';
  for (const it of S.items) for (const n of itemNodes(it)) box.appendChild(n);
  scrollBottom(true);
}

function addItem(it) {
  if (!S.conv || it.sessionId !== S.conv.sid) return;
  S.items.push(it);
  const box = $('#msgs');
  for (const n of itemNodes(it)) box.appendChild(n);
  scrollBottom();
}

function findRow(conv, id) {
  if (!S.conv || conv !== S.conv.sid) return null;
  return $('#msgs').querySelector('[data-id="' + id + '"]');
}

function updateText(conv, id, text) {
  if (!S.conv || conv !== S.conv.sid) return;
  const it = S.items.find(x => x.id === id);
  if (it) { it.text = text; it.thinking = false; }
  const row = findRow(conv, id);
  if (!row) return;
  paintBubble(row, it || { text: text });
  scrollBottom();
}

function patchItem(conv, m) {
  const it = S.items.find(x => x.id === m.id);
  if (it) { if (m.text != null) it.text = m.text; if (m.thinking != null) it.thinking = m.thinking; if (m.opacity != null) it.opacity = m.opacity; }
  const row = findRow(conv, m.id);
  if (row) paintBubble(row, it || m);
}

function removeItem(conv, id) {
  if (!S.conv || conv !== S.conv.sid) return;
  S.items = S.items.filter(x => x.id !== id);
  const row = findRow(conv, id);
  if (row) {
    const prev = row.previousElementSibling;
    if (prev && prev.classList.contains('timeline')) prev.remove();
    row.remove();
  }
}

function scrollBottom(force) {
  const box = $('#msgs');
  const near = box.scrollHeight - box.scrollTop - box.clientHeight < 60;
  if (force || near) box.scrollTop = box.scrollHeight;
  $('#btnBottom').classList.toggle('hidden', box.scrollHeight - box.scrollTop - box.clientHeight < 40);
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
  S.tab = t === 1 ? 1 : 0;
  document.querySelectorAll('#seg button').forEach(b =>
    b.classList.toggle('on', +b.dataset.tab === S.tab));
  $('#btnClusterTpl').classList.toggle('hidden', S.tab !== 1);
  $('#mode').classList.toggle('hidden', S.tab === 1);
  $('#input').placeholder = S.tab === 1
    ? '一句话指挥集群…（Enter 发送 / Shift+Enter 换行 / Esc 停止）'
    : '说点什么…（Enter 发送 / Shift+Enter 换行 / Esc 停止）';
  document.querySelector('#composer').dataset.tab = S.tab;
}

function setTheme(t) {
  document.body.classList.toggle('dark', t === 'dark');
  document.body.classList.toggle('light', t !== 'dark');
  $('#btnTheme').textContent = t === 'dark' ? '☀️' : '🌙';
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
      renderConvs(); renderItems(); renderStatus(m.status);
      if (S.update) showUpdatePill(S.update);
      document.title = 'deepsleep v' + S.version;
      break;
    case 'tab':
      setTabUI(m.tab || 0);
      S.convs = m.convs || []; S.conv = m.conv; S.items = m.items || [];
      renderConvs(); renderItems(); renderStatus(m.status);
      break;
    case 'convs':
      if (m.tab != null && m.tab !== S.tab) break;
      S.convs = m.convs || []; renderConvs();
      break;
    case 'conv':
      if (m.tab != null) setTabUI(m.tab);
      S.convs = m.convs || []; S.conv = m.conv; S.items = m.items || [];
      renderConvs(); renderItems();
      if (m.status) renderStatus(m.status);
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
    case 'ask': showAsk(m); break;
    case 'askClose': closeAsk(m.askId); break;
    case 'update': S.update = m.update; showUpdatePill(m.update); break;
    case 'updateLatest': S.update = null; $('#btnUpdate').classList.add('hidden'); toast(m.text); break;
    case 'updateProgress': updateProgress(m.percent, m.status); break;
    case 'updateStatus': updateProgress(null, m.text, m.done); break;
    case 'restarting': restarting(); break;
    case 'openMain': call('showWindow'); break;
    case 'pet': S.pet = !!m.enabled; if (modalName === 'settings') applySettings(); break;
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
function closeModal() {
  if (activeAsk && modalName.indexOf('ask:') === 0) {
    const id = activeAsk;
    activeAsk = null;
    call('answer', { askId: id, yes: false, remember: false });
  }
  $('#modalRoot').classList.remove('on');
  $('#modalRoot').innerHTML = '';
  modalName = '';
}
function openModal(name, title, buttons) {
  closeModal();
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
  setTimeout(() => $('#memText').focus(), 30);
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
/* ---------------- 设置 ---------------- */
function applySettings() {
  const s = S.settings || {};
  if ($('#mode') && s.mode) $('#mode').value = s.mode;
  if ($('#tgFast')) $('#tgFast').checked = !!s.fast;
  if ($('#tgLocal')) $('#tgLocal').checked = !!s.local;
  if ($('#tgSearch')) $('#tgSearch').checked = !!s.search;
  if (s.theme) setTheme(s.theme);
}

function openSettings(fresh) {
  const s = fresh || S.settings || {};
  const body = openModal('settings', '⚙ 设置', [
    { t: '取消' },
    { t: '保存', primary: true, fn: () => saveSettings() },
  ]);

  const sect = t => { const d = document.createElement('div'); d.className = 'sect'; d.textContent = t; body.appendChild(d); };

  sect('外部 API（OpenAI 兼容，如 DeepSeek）');
  const presetSel = document.createElement('div');
  presetSel.className = 'field';
  presetSel.innerHTML = '<label>免费模型预设（选中自动填地址和模型名，只需再填自己的免费 Key）</label><select id="preset"></select>';
  body.appendChild(presetSel);
  const sel = $('#preset');
  sel.innerHTML = '<option value="-1">（手动配置）</option>' +
    (s.presets || []).map((p, i) => '<option value="' + i + '">' + esc(p.name) + '</option>').join('');
  sel.onchange = () => {
    const i = +sel.value;
    if (i < 0) return;
    $('#apiUrl').value = s.presets[i].url;
    $('#apiModel').value = s.presets[i].model;
  };
  body.appendChild(field('API 地址', 'apiUrl', s.apiUrl));
  body.appendChild(field('API 模型名', 'apiModel', s.apiModel));
  body.appendChild(field('API Key', 'apiKey', s.apiKey, 'password'));
  const fmt = document.createElement('div');
  fmt.className = 'field';
  fmt.innerHTML = '<label>API 协议格式（选错会报错）</label><select id="apiFormat">' +
    '<option value="openai">OpenAI 兼容（DeepSeek / 智谱 / 硅基流动 / Groq / OpenRouter）</option>' +
    '<option value="anthropic">Anthropic Claude（/v1/messages）</option>' +
    '<option value="gemini">Google Gemini（generateContent）</option>' +
    '<option value="responses">OpenAI Responses API（/v1/responses，GPT-5 等新模型）</option>' +
    '</select>';
  body.appendChild(fmt);
  $('#apiFormat').value = s.apiFormat || 'openai';
  $('#apiFormat').onchange = () => {
    const map = {
      anthropic: 'https://api.anthropic.com/v1/messages',
      gemini: 'https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent',
      responses: 'https://api.openai.com/v1/responses',
      openai: 'https://api.deepseek.com/chat/completions',
    };
    $('#apiUrl').value = map[$('#apiFormat').value] || map.openai;
  };
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
    '<option value="chat">chat · 聊天（可搜索 / 深度研究）</option>' +
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

  sect('桌面桌宠');
  body.appendChild(ck('显示桌面鲸鱼桌宠（透明窗口、可拖拽；单击弹出聊天浮窗、右键有菜单）', 'petEnabled', s.petEnabled));
}

function saveSettings() {
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
function showUpdatePill(u) {
  $('#btnUpdate').classList.remove('hidden');
  $('#updateText').textContent = '有新版 v' + u.version;
}
function onUpdateClick() {
  const u = S.update;
  if (!u) { call('checkUpdate'); return; }
  const body = openModal('updateinfo', '发现新版本 v' + u.version, [
    { t: '稍后' },
    { t: '立即升级', primary: true, fn: () => startUpdate() },
  ]);
  const d = document.createElement('div');
  d.style.cssText = 'font-size:12.5px;line-height:1.6;white-space:pre-wrap;margin:6px 0 14px';
  d.textContent = '当前版本：v' + u.current + '\n\n更新内容：\n' + (u.notes || '（无更新说明）') +
    '\n\n升级会下载安装包，退出后静默覆盖安装并自动重启。用户数据（API Key、对话历史、记忆、自训练模型）不受影响。\n\n' + (u.route || '');
  body.appendChild(d);
}
function startUpdate() {
  openModal('update', '正在升级', []);
  const body = $('#modalRoot').querySelector('.body');
  body.innerHTML = '<div id="upText" style="font-size:12.5px;margin:8px 0">正在准备下载…</div><div class="bar"><i id="upBar"></i></div>';
  call('runUpdate');
}
function updateProgress(p, text, done) {
  if (modalName !== 'update') {
    openModal('update', '正在升级', done ? [{ t: '关闭' }] : []);
    const body = $('#modalRoot').querySelector('.body');
    body.innerHTML = '<div id="upText" style="font-size:12.5px;margin:8px 0"></div><div class="bar"><i id="upBar"></i></div>';
  }
  const t = $('#upText'), b = $('#upBar');
  if (t && text) t.textContent = text + (p == null ? '' : '\n进度 ' + Math.round(p) + '%');
  if (b && p != null) b.style.width = Math.max(0, Math.min(100, p)) + '%';
  if (done && t) t.textContent = text || '';
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
  $('#msgs').onscroll = () => scrollBottom();
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