/* deepsleep 网页版（精简版）：纯前端，聊天记录与 API Key 只存在你自己的浏览器里（localStorage）。
   没有后端 = 不能联网抓取 / 不能操作文件与命令，需要完整能力请下载客户端。 */
const LSKEY = 'dsweb.v1';
const $ = (s, r) => (r || document).querySelector(s);
const $$ = (s, r) => Array.prototype.slice.call((r || document).querySelectorAll(s));
/* 内置免费 Key：智谱开放平台的免费模型矩阵（GLM-4.7-Flash 聊天 / GLM-4.6V-Flash 看图 / CogView-3-Flash 画图）。
   大家共用会被限流，建议在设置里换成自己的 Key（只存在浏览器本地）。 */
const BUILTIN_KEY = '8020bf5b2eda4be9831cbb3f1d016ca0.clnzkMFtEyt5wKUw';
const DEF = {
  apiUrl: 'https://open.bigmodel.cn/api/paas/v4/chat/completions',
  model: 'glm-4.7-flash',
  visionModel: 'glm-4.6v-flash',
  imageUrl: 'https://open.bigmodel.cn/api/paas/v4/images/generations',
  imageModel: 'cogview-3-flash',
  key: '',
  useBuiltin: true,
  agentMd: '',
  memory: '',
  theme: 'light',
  imageMode: false
};
const S = { cfg: Object.assign({}, DEF), convs: [], cur: null, filter: '', busy: false, timer: null, imgs: [], effKey: '' };
let abortCtl = null;

/* ---------------- 存储 ---------------- */
function load() {
  try {
    const raw = localStorage.getItem(LSKEY);
    if (raw) {
      const o = JSON.parse(raw) || {};
      S.cfg = Object.assign({}, DEF, o.cfg || {});
      S.convs = o.convs || [];
      S.cur = o.cur || (S.convs[0] ? S.convs[0].sid : null);
    }
  } catch (e) { }
  if (!S.convs.length) newConv(false);
  if (!conv()) S.cur = S.convs[0].sid;
}
function save() {
  try { localStorage.setItem(LSKEY, JSON.stringify({ cfg: S.cfg, convs: S.convs, cur: S.cur })); } catch (e) { }
}
function conv() { return S.convs.find(c => c.sid === S.cur) || null; }
function uid() { return Date.now().toString(36) + Math.random().toString(36).slice(2, 7); }
function clock() {
  const d = new Date();
  return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
}

/* ---------------- 提示 ---------------- */
function toast(text, level) {
  const d = document.createElement('div');
  d.className = 'toast' + (level === 'error' ? ' error' : '');
  d.textContent = text;
  $('#toasts').appendChild(d);
  setTimeout(() => { d.style.opacity = '0'; }, 2200);
  setTimeout(() => d.remove(), 2600);
}

/* ---------------- Markdown ---------------- */
function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"]/g,
    c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}
function inline(t) {
  // 行内代码先抽出来，免得里面的 Markdown 语法被二次解析
  const keep = [];
  let s = t.replace(/`([^`]+)`/g, (m, c) => { keep.push('<code>' + c + '</code>'); return '\u0001' + (keep.length - 1) + '\u0001'; });
  // 图片（画图结果 / 用户附图）—— 必须在普通链接之前
  s = s.replace(/!\[([^\]]*)\]\((https?:[^)\s]+|data:[^)\s]+)\)/g,
    '<img class="genimg" src="$2" alt="$1" loading="lazy">');
  s = s.replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>')
    .replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<i>$2</i>')
    .replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noreferrer">$1</a>');
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
  const parts = String(src).split(String.fromCharCode(96, 96, 96));
  const fence = String.fromCharCode(96, 96, 96);
  let html = '';
  for (let i = 0; i < parts.length; i++) {
    if (i % 2 === 1) {
      let code = parts[i], lang = '';
      const nl = code.indexOf('\n');
      if (nl >= 0 && code.slice(0, nl).indexOf(String.fromCharCode(96)) < 0) { lang = code.slice(0, nl).trim(); code = code.slice(nl + 1); }
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
  const kw = S.filter.toLowerCase();
  const list = S.convs.filter(c => !kw || c.title.toLowerCase().indexOf(kw) >= 0);
  if (!list.length) {
    const e = document.createElement('div');
    e.className = 'sysbar';
    e.style.margin = '10px auto';
    e.textContent = S.convs.length ? '没有匹配的对话' : '还没有对话';
    box.appendChild(e);
  }
  for (const c of list) {
    const d = document.createElement('div');
    d.className = 'conv' + (c.sid === S.cur ? ' on' : '');
    d.dataset.sid = c.sid;
    const t = document.createElement('span');
    t.className = 't';
    t.textContent = c.title;
    d.appendChild(t);
    d.onclick = () => { S.cur = c.sid; save(); renderAll(); };
    d.oncontextmenu = ev => convMenu(ev, c);
    box.appendChild(d);
  }
}
function scrollBottom(force) {
  const box = $('#msgs');
  const near = box.scrollHeight - box.scrollTop - box.clientHeight < 120;
  if (force || near) box.scrollTop = box.scrollHeight;
}
function msgNode(m) {
  const row = document.createElement('div');
  row.className = 'row' + (m.self ? ' self' : '') + (m.sys ? ' sys' : '');
  row.dataset.id = m.id;
  if (m.sys) {
    const bar = document.createElement('div');
    bar.className = 'sysbar';
    bar.textContent = m.text;
    row.appendChild(bar);
    return row;
  }
  const av = document.createElement('div');
  av.className = 'avatar';
  av.style.background = m.self ? '#007AFF' : '#4A90D9';
  av.textContent = m.self ? '你' : 'AI';
  const b = document.createElement('div');
  b.className = 'bubble';
  const acts = document.createElement('div');
  acts.className = 'acts';
  const cp = document.createElement('button');
  cp.textContent = '复制';
  cp.onclick = () => { navigator.clipboard.writeText(m.text || ''); toast('已复制'); };
  const del = document.createElement('button');
  del.textContent = '删除';
  del.onclick = () => { const c = conv(); if (!c) return; c.items = c.items.filter(x => x.id !== m.id); save(); renderMsgs(); };
  acts.append(cp, del);
  row.append(av, b, acts);
  paint(row, m);
  return row;
}
function paint(row, m) {
  const b = row.querySelector('.bubble');
  if (!b) return;
  if (m.thinking) {
    b.classList.add('thinking');
    if (b.dataset.think !== m.text) {
      b.dataset.think = m.text;
      b.innerHTML = '<span>' + esc(m.text) + '</span><span class="dots"><i></i><i></i><i></i></span>';
    }
    return;
  }
  b.classList.remove('thinking');
  const pics = m.mm ? m.mm.filter(x => x.type === 'image_url')
    .map(x => '<img class="genimg" src="' + esc(x.image_url.url) + '" loading="lazy">').join('') : '';
  const html = pics + md(m.text);
  const key = pics.length + '|' + m.text;
  if (b.dataset.src !== key) { b.dataset.src = key; b.innerHTML = html; }
}
function renderMsgs() {
  const box = $('#msgs');
  box.innerHTML = '';
  const c = conv();
  if (!c) return;
  for (const m of c.items) box.appendChild(msgNode(m));
  scrollBottom(true);
}
function renderAll() { renderConvs(); renderMsgs(); }

/* ---------------- 会话操作 ---------------- */
function newConv(repaint) {
  if (repaint === undefined) repaint = true;
  const c = { sid: uid(), title: '新对话', items: [], pinned: false, updated: Date.now() };
  S.convs.unshift(c);
  S.cur = c.sid;
  if (repaint) { save(); renderAll(); $('#input').focus(); }
  return c;
}
function convMenu(ev, c) {
  ev.preventDefault();
  const wrap = document.createElement('div');
  wrap.className = 'backdrop';
  const m = document.createElement('div');
  m.className = 'modal';
  const h = document.createElement('h3');
  h.textContent = '对话：' + c.title;
  const body = document.createElement('div');
  body.className = 'body';
  const f1 = document.createElement('div');
  f1.className = 'field';
  f1.innerHTML = '<label for="cvTitle">重命名</label><input id="cvTitle" type="text">';
  f1.querySelector('#cvTitle').value = c.title;
  const f2 = document.createElement('div');
  f2.className = 'field';
  f2.innerHTML = '<label class="ck"><input type="checkbox" id="cvPin"><span>置顶</span></label>';
  f2.querySelector('#cvPin').checked = !!c.pinned;
  body.append(f1, f2);
  const foot = document.createElement('div');
  foot.className = 'foot';
  const bDel = document.createElement('button');
  bDel.className = 'btn danger';
  bDel.textContent = '删除对话';
  bDel.onclick = () => {
    S.convs = S.convs.filter(x => x.sid !== c.sid);
    if (S.cur === c.sid) S.cur = S.convs.length ? S.convs[0].sid : newConv(false).sid;
    save(); closeModal(); renderAll();
    toast('已删除');
  };
  const bNo = document.createElement('button');
  bNo.className = 'btn';
  bNo.textContent = '取消';
  bNo.onclick = closeModal;
  const bOk = document.createElement('button');
  bOk.className = 'btn primary';
  bOk.textContent = '保存';
  bOk.onclick = () => {
    c.title = f1.querySelector('#cvTitle').value.trim() || c.title;
    c.pinned = f2.querySelector('#cvPin').checked;
    save(); closeModal(); renderConvs();
  };
  foot.append(bDel, bNo, bOk);
  m.append(h, body, foot);
  wrap.appendChild(m);
  wrap.onclick = e => { if (e.target === wrap) closeModal(); };
  openModalRaw(wrap);
}
function closeModal() {
  const r = $('#modalRoot');
  r.classList.remove('on');
  r.innerHTML = '';
}
function openModalRaw(wrap) {
  const r = $('#modalRoot');
  r.innerHTML = '';
  r.appendChild(wrap);
  r.classList.add('on');
}
function exportConvs() {
  const blob = new Blob([JSON.stringify({ cfg: { apiUrl: S.cfg.apiUrl, model: S.cfg.model }, convs: S.convs }, null, 2)], { type: 'application/json' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = 'deepsleep-web-convs.json';
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 3000);
  toast('已导出（不含 API Key）');
}

/* ---------------- 请求 ---------------- */
const PRESETS = [
  { n: '智谱 GLM 免费矩阵（4.7 聊天 / 4.6V 看图 / CogView 画图）', u: 'https://open.bigmodel.cn/api/paas/v4/chat/completions', m: 'glm-4.7-flash', v: 'glm-4.6v-flash' },
  { n: '硅基流动 SiliconFlow', u: 'https://api.siliconflow.cn/v1/chat/completions', m: 'Qwen/Qwen3-8B' },
  { n: 'DeepSeek 官方', u: 'https://api.deepseek.com/v1/chat/completions', m: 'deepseek-chat' },
  { n: 'OpenRouter（可挑 :free 模型）', u: 'https://openrouter.ai/api/v1/chat/completions', m: 'deepseek/deepseek-chat-v3.1:free' },
  { n: 'OpenAI 官方', u: 'https://api.openai.com/v1/chat/completions', m: 'gpt-4o-mini' },
  { n: '本地 Ollama', u: 'http://localhost:11434/v1/chat/completions', m: 'qwen3:4b' }
];
function systemPrompt() {
  const parts = [];
  const a = (S.cfg.agentMd || '').trim();
  if (a) parts.push('【初始提示词 · AGENT.md（所有对话共享，只能在 ⚙ 设置里修改，优先级最高）】' + '\n' + a);
  const mem = (S.cfg.memory || '').trim();
  if (mem) parts.push('【长期记忆（用户自己写的背景，回答时参考）】' + '\n' + mem);
  parts.push('你是 deepsleep 网页版的 AI 助手，运行在浏览器里。用中文回答，Markdown 排版，代码用围栏代码块。');
  return parts.join('\n\n');
}
function failHint(status, msg) {
  const m = String(msg || '');
  if (status === 429 || /429|访问量过大|限流|too many|rate limit/i.test(m)) {
    return '内置的免费 Key 是大家共用的，这会儿被限流了 —— 过一两分钟再试，或者去 ⚙ 设置里填自己的 Key（免费注册智谱开放平台即可）。';
  }
  if (status === 401 || status === 403) return 'Key 不对或没有权限：去 ⚙ 设置检查 API Key 和地址（内置免费 Key 请在设置里勾上「没有自己的 Key 时使用内置免费 Key」）。';
  if (/Failed to fetch|NetworkError|Load failed/i.test(m)) {
    return '这类报错通常是**浏览器跨域（CORS）**或地址/网络不通。\n- 换一个允许浏览器直连的地址（例如智谱、硅基流动）再试\n- 或用「本地 Ollama」这种同机地址\n- 桌面客户端没有跨域限制，功能也更全，建议下载客户端';
  }
  return '请检查 API 地址、模型名、Key 是否正确。';
}

function setBusy(on) {
  S.busy = on;
  const b = $('#btnSend');
  b.textContent = on ? '停止' : '发送';
  b.classList.toggle('danger', on);
  $('#busy').textContent = on ? '生成中…' : '';
  $('#btnRegen').classList.toggle('off', on);
}
function lastRow(id) { return $('[data-id="' + id + '"]'); }
function livePaint(m) {
  if (S.timer) return;
  S.timer = setTimeout(() => { S.timer = null; const r = lastRow(m.id); if (r) paint(r, m); scrollBottom(); }, 120);
}
async function send() {
  if (S.busy) { stop(); return; }
  const c = conv();
  const box = $('#input');
  const text = box.value.trim();
  if (!c || !text) return;
  if (S.cfg.imageMode) { box.value = ''; grow(); return draw(text); }
  if (!(S.effKey || '').trim()) { toast('先在 ⚙ 设置里填 API Key', 'error'); return openSettings(); }
  if (!(S.cfg.apiUrl || '').trim()) { toast('先去 ⚙ 设置填 API 地址', 'error'); return openSettings(); }
  box.value = '';
  grow();
  const me = { id: uid(), self: true, text: text, time: clock() };
  const withImgs = S.imgs.slice();
  if (withImgs.length) {
    me.mm = [{ type: 'text', text: text }].concat(withImgs.map(x => ({ type: 'image_url', image_url: { url: x.url } })));
    me.text = text + '（附 ' + withImgs.length + ' 张图）';
    S.imgs = [];
    renderImgs();
  }
  const ai = { id: uid(), self: false, text: '', thinking: true, time: clock() };
  c.items.push(me, ai);
  if (c.title === '新对话') c.title = text.slice(0, 18);
  c.updated = Date.now();
  save(); renderAll();
  await run(c, ai, withImgs.length > 0);
}
async function run(c, ai, useVision) {
  setBusy(true);
  abortCtl = new AbortController();
  const history = c.items.filter(m => !m.sys && !m.thinking && m.id !== ai.id)
    .map(m => ({ role: m.self ? 'user' : 'assistant', content: m.mm || m.text }));
  const model = useVision ? (S.cfg.visionModel || S.cfg.model) : S.cfg.model;
  const body = { model: model, stream: true, messages: [{ role: 'system', content: systemPrompt() }].concat(history) };
  try {
    const res = await fetch(S.cfg.apiUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + (S.effKey || '') },
      body: JSON.stringify(body),
      signal: abortCtl.signal
    });
    if (!res.ok) {
      const t = await res.text();
      const err = new Error('HTTP ' + res.status + '：' + t.slice(0, 300));
      err.status = res.status;
      throw err;
    }
    const ctype = res.headers.get('content-type') || '';
    if (ctype.indexOf('event-stream') < 0) {
      const j = await res.json();
      const ch = (j.choices || [])[0] || {};
      ai.text = ((ch.message || {}).content) || '';
      ai.thinking = false;
    } else {
      ai.thinking = false;
      const reader = res.body.getReader();
      const dec = new TextDecoder('utf-8');
      let buf = '';
      for (;;) {
        const r = await reader.read();
        if (r.done) break;
        buf += dec.decode(r.value, { stream: true });
        const lines = buf.split('\n');
        buf = lines.pop();
        for (const ln of lines) {
          const s = ln.trim();
          if (s.indexOf('data:') !== 0) continue;
          const payload = s.slice(5).trim();
          if (!payload || payload === '[DONE]') continue;
          try {
            const j = JSON.parse(payload);
            const d = (((j.choices || [])[0] || {}).delta) || {};
            const piece = d.content || (d.reasoning_content ? '' : '');
            if (piece) { ai.text += piece; livePaint(ai); }
          } catch (e) { }
        }
      }
      const row0 = lastRow(ai.id);
      if (row0) paint(row0, ai);
    }
  } catch (e) {
    ai.thinking = false;
    if (e && e.name === 'AbortError') {
      if (!ai.text) ai.text = '（已停止）';
    } else {
      const msg = (e && e.message) || String(e);
      ai.text = '请求失败：' + msg + '\n\n' + failHint((e && e.status) || 0, msg);
    }
  }
  ai.thinking = false;
  if (!ai.text) ai.text = '（没有内容）';
  if (S.timer) { clearTimeout(S.timer); S.timer = null; }
  const row = lastRow(ai.id);
  if (row) paint(row, ai);
  S.busy = false;
  setBusy(false);
  abortCtl = null;
  save();
  scrollBottom();
}
function stop() {
  if (abortCtl) { try { abortCtl.abort(); } catch (e) { } }
}
async function regen() {
  if (S.busy) return;
  const c = conv();
  if (!c || !c.items.length) return;
  while (c.items.length && !c.items[c.items.length - 1].self) c.items.pop();
  if (!c.items.length) { toast('没有可重新生成的内容'); return renderAll(); }
  const ai = { id: uid(), self: false, text: '', thinking: true, time: clock() };
  c.items.push(ai);
  save(); renderAll();
  await run(c, ai, false);
}

/* ---------------- 看图 / 画图 ---------------- */
function attachImage() {
  const inp = document.createElement('input');
  inp.type = 'file';
  inp.accept = 'image/*';
  inp.style.display = 'none';
  document.body.appendChild(inp);
  inp.onchange = () => {
    const f = inp.files && inp.files[0];
    inp.remove();
    if (!f) return;
    if (f.size > 8 * 1024 * 1024) { toast('图片别超过 8MB', 'error'); return; }
    const fr = new FileReader();
    fr.onload = () => { S.imgs.push({ name: f.name, url: String(fr.result) }); renderImgs(); };
    fr.readAsDataURL(f);
  };
  inp.click();
}
function renderImgs() {
  const box = $('#imgs');
  box.innerHTML = '';
  S.imgs.forEach((it, i) => {
    const d = document.createElement('div');
    d.className = 'imgchip';
    d.innerHTML = '<img src="' + it.url + '"><span>' + esc(it.name) + '</span><b>×</b>';
    d.querySelector('b').onclick = () => { S.imgs.splice(i, 1); renderImgs(); };
    box.appendChild(d);
  });
  box.classList.toggle('hidden', S.imgs.length === 0);
}
async function draw(text) {
  const c = conv();
  const me = { id: uid(), self: true, text: text, time: clock() };
  const ai = { id: uid(), self: false, text: '', thinking: true, time: clock() };
  c.items.push(me, ai);
  if (c.title === '新对话') c.title = text.slice(0, 18);
  save(); renderAll();
  setBusy(true);
  try {
    const res = await fetch(S.cfg.imageUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + (S.effKey || '') },
      body: JSON.stringify({ model: S.cfg.imageModel, prompt: text })
    });
    const t = await res.text();
    let j = null;
    try { j = JSON.parse(t); } catch (e) { }
    if (!res.ok || !j) {
      const err = new Error('HTTP ' + res.status + '：' + t.slice(0, 240));
      err.status = res.status;
      throw err;
    }
    const url = (((j.data || [])[0]) || {}).url;
    if (!url) throw new Error('返回里没有图片地址：' + t.slice(0, 200));
    ai.thinking = false;
    ai.text = '画好了：\n\n![' + text.slice(0, 30) + '](' + url + ')\n\n[原图链接](' + url + ')';
  } catch (e) {
    ai.thinking = false;
    ai.text = '画图失败：' + ((e && e.message) || e) + '\n\n' + failHint((e && e.status) || 0, (e && e.message) || e);
  }
  S.busy = false;
  setBusy(false);
  const row = lastRow(ai.id);
  if (row) paint(row, ai);
  save();
  scrollBottom();
}

/* ---------------- 设置 ---------------- */
function openSettings() {
  const body = [];
  const wrap = document.createElement('div');
  wrap.className = 'backdrop';
  const m = document.createElement('div');
  m.className = 'modal';
  const h = document.createElement('h3');
  h.textContent = '⚙ 设置（网页版）';
  const b = document.createElement('div');
  b.className = 'body';
  b.innerHTML =
    '<div class="field"><label>免费预设</label><select id="setPreset"></select>' +
    '<div class="hint">网页版内置一把免费的 GLM Key，打开就能用：GLM-4.7-Flash 聊天、GLM-4.6V-Flash 看图、CogView-3-Flash 画图。用的人多会被限流，建议填自己的 Key。</div></div>' +
    '<div class="field"><label>API 地址</label><input id="setApiUrl" type="text"></div>' +
    '<div class="field"><label>聊天模型</label><input id="setModel" type="text"></div>' +
    '<div class="field"><label>看图模型</label><input id="setVision" type="text"></div>' +
    '<div class="field"><label>画图接口</label><input id="setImgUrl" type="text"></div>' +
    '<div class="field"><label>画图模型</label><input id="setImgModel" type="text"></div>' +
    '<div class="field"><label>API Key</label><input id="setKey" type="password">' +
    '<div class="hint">只存在你自己的浏览器（localStorage），不会上传。清空则用内置免费 Key。</div></div>' +
    '<div class="field"><label>初始提示词（AGENT.md）</label><textarea id="setAgentMd" class="mdarea" placeholder="例：用中文回答，先给结论再给理由。"></textarea>' +
    '<div class="hint">所有对话共享；只在设置里能改；HTML 注释 &lt;!-- --&gt; 里的内容不会发给模型。</div></div>' +
    '<div class="field"><label>长期记忆</label><textarea id="setMemory" placeholder="每行一条，例如：我是产品经理，喜欢简短回答"></textarea></div>' +
    '<label class="ck" style="margin:10px 0"><input type="checkbox" id="setUseBuiltin"><span>没有自己的 Key 时使用内置免费 Key</span></label>';
  const foot = document.createElement('div');
  foot.className = 'foot';
  const bClose = document.createElement('button');
  bClose.className = 'btn'; bClose.textContent = '取消'; bClose.onclick = closeModal;
  const bSave = document.createElement('button');
  bSave.className = 'btn primary'; bSave.textContent = '保存';
  bSave.onclick = () => {
    S.cfg.apiUrl = val('setApiUrl') || S.cfg.apiUrl;
    S.cfg.model = val('setModel') || S.cfg.model;
    S.cfg.visionModel = val('setVision') || S.cfg.visionModel;
    S.cfg.imageUrl = val('setImgUrl') || S.cfg.imageUrl;
    S.cfg.imageModel = val('setImgModel') || S.cfg.imageModel;
    S.cfg.key = val('setKey');
    S.cfg.agentMd = val('setAgentMd');
    S.cfg.memory = val('setMemory');
    S.cfg.useBuiltin = $('#setUseBuiltin').checked;
    save(); syncKey(); closeModal(); toast('已保存');
  };
  foot.append(bClose, bSave);
  m.append(h, b, foot);
  wrap.appendChild(m);
  wrap.onclick = e => { if (e.target === wrap) closeModal(); };
  openModalRaw(wrap);

  const sel = b.querySelector('#setPreset');
  sel.innerHTML = '<option value="">（手动配置）</option>' +
    PRESETS.map((p, i) => '<option value="' + i + '">' + esc(p.n) + '</option>').join('');
  sel.onchange = () => {
    const i = sel.value === '' ? -1 : parseInt(sel.value, 10);
    if (i < 0) return;
    b.querySelector('#setApiUrl').value = PRESETS[i].u;
    b.querySelector('#setModel').value = PRESETS[i].m;
    if (PRESETS[i].v) b.querySelector('#setVision').value = PRESETS[i].v;
  };
  b.querySelector('#setApiUrl').value = S.cfg.apiUrl;
  b.querySelector('#setModel').value = S.cfg.model;
  b.querySelector('#setVision').value = S.cfg.visionModel;
  b.querySelector('#setImgUrl').value = S.cfg.imageUrl;
  b.querySelector('#setImgModel').value = S.cfg.imageModel;
  b.querySelector('#setKey').value = S.cfg.key;
  b.querySelector('#setAgentMd').value = S.cfg.agentMd;
  b.querySelector('#setMemory').value = S.cfg.memory;
  b.querySelector('#setUseBuiltin').checked = S.cfg.useBuiltin !== false;
}
function val(id) { const e = $('#' + id); return e ? e.value.trim() : ''; }
function syncKey() {
  const hasOwn = (S.cfg.key || '').trim().length > 0;
  const useBuiltin = S.cfg.useBuiltin !== false;
  if (!hasOwn && useBuiltin) S.effKey = BUILTIN_KEY; else S.effKey = S.cfg.key;
  const b = $('#keyHint');
  if (b) b.classList.toggle('hidden', !!S.effKey);
}

/* ---------------- 交互 ---------------- */
function grow() {
  const t = $('#input');
  t.style.height = 'auto';
  t.style.height = Math.min(180, Math.max(38, t.scrollHeight)) + 'px';
}
function applyTheme() {
  const dark = S.cfg.theme === 'dark';
  document.body.classList.toggle('dark', dark);
  document.body.classList.toggle('light', !dark);
}
function clearConv() {
  const c = conv();
  if (!c || !c.items.length) { toast('这个对话已经是空的'); return; }
  if (!confirm('清空当前对话？')) return;
  c.items = [];
  save(); renderMsgs(); toast('已清空');
}
function paintDrawBtn() {
  const b = $('#btnDraw');
  if (!b) return;
  b.classList.toggle('on', !!S.cfg.imageMode);
  b.title = S.cfg.imageMode ? '画图模式：开（点一下切回聊天）' : '画图模式（CogView-3-Flash）';
}
function init() {
  load();
  syncKey();
  applyTheme();
  $('#btnSend').onclick = () => send();
  $('#btnRegen').onclick = () => regen();
  $('#btnNew').onclick = () => newConv(true);
  $('#btnSettings').onclick = () => openSettings();
  $('#btnTheme').onclick = () => {
    S.cfg.theme = S.cfg.theme === 'dark' ? 'light' : 'dark';
    applyTheme(); save();
  };
  $('#btnAttach').onclick = () => attachImage();
  $('#btnClear').onclick = () => clearConv();
  $('#btnExport').onclick = () => exportConvs();
  $('#btnDraw').onclick = () => {
    S.cfg.imageMode = !S.cfg.imageMode;
    paintDrawBtn();
    save();
    toast(S.cfg.imageMode ? '画图模式：直接描述画面，我用 CogView-3-Flash 画' : '已切回聊天');
  };
  $('#convSearch').oninput = ev => { S.filter = ev.target.value.trim().toLowerCase(); renderConvs(); };
  const inp = $('#input');
  inp.addEventListener('input', grow);
  inp.addEventListener('keydown', ev => {
    if (ev.key === 'Enter' && !ev.shiftKey && !ev.isComposing) { ev.preventDefault(); send(); }
    else if (ev.key === 'Escape') stop();
  });
  $('#msgs').addEventListener('click', ev => {
    const b = ev.target.closest('[data-copy]');
    if (!b) return;
    const code = b.parentElement ? b.parentElement.querySelector('code') : null;
    navigator.clipboard.writeText(code ? code.textContent : '');
    b.textContent = '已复制';
    setTimeout(() => { b.textContent = '复制'; }, 1200);
  });
  $('#msgs').addEventListener('scroll', () => {
    const box = $('#msgs');
    const near = box.scrollHeight - box.scrollTop - box.clientHeight < 120;
    $('#btnBottom').classList.toggle('hidden', near);
  });
  $('#btnBottom').onclick = () => scrollBottom(true);
  window.addEventListener('beforeunload', () => { try { save(); } catch (e) { } });
  renderAll();
  renderImgs();
  paintDrawBtn();
  grow();
  if (!S.effKey) toast('还没配 API Key，点右上角 ⚙ 填一个', 'error');
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
else init();
