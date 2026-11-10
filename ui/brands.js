/* 品牌识别：把模型名（id / 展示名）认成供应商，再取对应 logo。
 *
 * 为什么要「认」而不是直接用模型 id 查表：模型名是各家随手起的，
 * 同一个东西在不同网关下可能叫 glm-4.6、zai-org/GLM-4.6、Pro/GLM-4.6，
 * 还有 kimi-k2 / moonshot-v1、qwen2.5:1.5b（本地 ollama）这类混写。
 * 所以这里只做「品牌特征串」匹配，顺序从专有名词到泛用词，命中即停。
 *
 * logo 本体在 ui/logos.js（由 tools/build-logos.py 从 ui/logos/*.svg 生成）。
 * 认不出来时退化成首字母圆标，不至于空一块或者显示个问号。
 */
(function () {
  'use strict';

  // k = logo 键（ui/logos 里的文件名），n = 显示名，c = 主色（认不出 logo 时当底色）
  var RULES = [
    ['ltx', 'LTX · Lightricks', '#141414', /(^|[^a-z])ltx/i],
    ['glm', 'GLM · 智谱', '#3859FF', /(^|[^a-z])(glm|chatglm|charglm|cogview|cogvideo|codegeex|zhipu)/i],
    ['deepseek', 'DeepSeek', '#4D6BFE', /deepseek/i],
    ['doubao', '豆包 · 字节', '#1E37FC', /(doubao|skylark|bytedance|volcengine|(^|[^a-z])seed[-_.][0-9])/i],
    ['kimi', 'Kimi · 月之暗面', '#1783FF', /(kimi|moonshot)/i],
    ['qwen', '千问 · 通义', '#615CED', /(qwen|qwq|tongyi|通义)/i],
    ['stepfun', '阶跃 StepFun', '#1D4ED8', /(stepfun|阶跃|(^|[^a-z])step[-_.]?(1|2|3|r)\b)/i],
    ['baidu', '百度 · 文心', '#2932E1', /(ernie|wenxin|文心|baidu|yiyan|一言)/i],
    ['spark', '讯飞星火', '#1652D8', /(spark|generalv|iflytek|讯飞)/i],
    ['minimax', 'MiniMax', '#E2167E', /(minimax|abab|hailuo|海螺)/i],
    ['meta', 'Meta · Llama', '#0082FB', /(llama|(^|[^a-z])meta[-_.\/])/i],
    ['gemini', 'Gemini · Google', '#3186FF', /(gemini|gemma|(^|[^a-z])palm|google|bard)/i],
    ['grok', 'Grok · xAI', '#111111', /(grok|(^|[^a-z])xai|x\.ai)/i],
    ['claude', 'Claude · Anthropic', '#D97757', /(claude|anthropic)/i],
    ['gpt', 'OpenAI · GPT', '#0A0A0A', /(^|[^a-z])(gpt|chatgpt|davinci|text-embedding|o[1-4])([^a-z]|$)/i],
  ];

  // 兜底配色：按名字哈希取一个，同一个模型每次都是同一色
  var PALETTE = ['#5B8CFF', '#8E5BFF', '#FF8A3D', '#12B886', '#E64980', '#0CA678', '#7950F2', '#F59F00'];
  function pick(seed) {
    var h = 0;
    for (var i = 0; i < seed.length; i++) h = (h * 31 + seed.charCodeAt(i)) | 0;
    return PALETTE[Math.abs(h) % PALETTE.length];
  }

  var cache = {};
  function keyOf(name) {
    if (name in cache) return cache[name];
    var hit = null;
    for (var i = 0; i < RULES.length; i++) {
      if (RULES[i][3].test(name)) { hit = i; break; }
    }
    return (cache[name] = hit);
  }

  function brandOf(name) {
    name = String(name || '').trim();
    if (!name) return null;
    var i = keyOf(name);
    if (i === null) return null;
    var r = RULES[i];
    return { k: r[0], n: r[1], c: r[2] };
  }

  // 返回一段 HTML：能认出品牌就给官方图形，认不出给首字母圆标。
  // cls 用来追加 class（比如列表里的窄尺寸）。
  function html(name, cls) {
    var b = brandOf(name);
    var extra = cls ? ' ' + cls : '';
    if (b) {
      var svg = (window.DSL_LOGOS || {})[b.k];
      if (svg) return '<span class="brand' + extra + '" title="' + b.n + '">' + svg + '</span>';
      return '<span class="brand mono' + extra + '" title="' + b.n + '" style="background:' + b.c + '">' +
        b.n.charAt(0).toUpperCase() + '</span>';
    }
    var nm = String(name || '?').replace(/^[^0-9a-zA-Z\u4e00-\u9fa5]+/, '');
    return '<span class="brand mono' + extra + '" title="' + name + '" style="background:' +
      pick(name) + '">' + (nm.charAt(0) || '?').toUpperCase() + '</span>';
  }

  // 给一个空容器填图标（容器自己带 .brand-slot 控制尺寸与居中）
  function paint(el, name) {
    if (el) el.innerHTML = html(name);
  }

  window.DSBrand = { of: brandOf, html: html, paint: paint };
})();
