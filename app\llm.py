# -*- coding: utf-8 -*-
"""
DeepSeek 大模型接入：为引擎提供更强大的实时生成回怼能力。

- 纯标准库（urllib）实现，无需第三方依赖；
- API Key 读取优先级：环境变量 DEEPSEEK_API_KEY > data/config.json；
- 所有调用失败（网络/超时/额度/解析错误）一律返回 None，
  由 engine 自动回退到本地实时组装，绝不影响正常使用；
- 生成的回复仍会经过 mask_profanity() 统一脱敏，保证零脏话。

配置方式：
    方式一（推荐）：设置环境变量 DEEPSEEK_API_KEY=sk-xxxx
    方式二：py -3 -c "from app.llm import save_api_key; save_api_key('sk-xxxx')"
"""

import json
import os
import urllib.error
import urllib.request

from . import data_dir

CONFIG_PATH = os.path.join(data_dir(), "config.json")

API_URL = "https://api.deepseek.com/chat/completions"
MODEL = "deepseek-v4-flash"
TIMEOUT = 60          # 秒（长文生成需要更长时间）
MAX_RETRIES = 1       # 网络抖动重试次数
MAX_TOKENS = 700      # 支持多行长文破防
TEMPERATURE = 0.95    # 提高多样性，避免车轱辘话

# 系统提示词：约束风格，保证「零脏话·像真人·干净利落·多行·实时生成」
SYSTEM_PROMPT = (
    "你是一个「以理服人」话术助手，帮用户回怼网络键盘侠。铁律：\n"
    "1. 全文绝不允许出现任何脏话、侮辱性词汇（如：垃圾、废物、傻X、滚等），违者整段作废；\n"
    "2. 必须引用对方原话中的关键片段逐条拆解、逐一反击，让对方感觉被精准点名；\n"
    "3. 语言干净利落、一针见血，同时信息量大、篇幅充足，写成 4~7 行多段落长文；\n"
    "4. 每次输出必须完全不同，禁止套用固定模板、禁止车轱辘重复；\n"
    "5. 根据攻击类型使用对应策略：人身攻击→捧杀反讽/佛系；威胁恫吓→关怀反将/佛系；"
    "贬低质疑→逻辑质询/灵魂反问；贴标签→捧杀反讽；挑衅拉踩→灵魂反问/复读放大；\n"
    "6. 保持冷静、居高临下、游刃有余，不跟对方对骂，让对方自讨没趣；\n"
    "7. 【关键】一定要像真人网友在评论区抬杠的口吻：说人话、口语化、短句为主，\n"
    "   绝对不要写 AI 腔——禁止「首先/其次/综上/总而言之/不难发现」这类书面套话，\n"
    "   禁止工整排比、禁止口号、禁止一本正经地讲大道理，可以带点损、带点阴阳怪气，\n"
    "   读起来要像是个嘴皮子利索的真人，而不是一份 AI 生成的分析报告。\n"
    "直接输出回怼内容，不要任何解释与开场白。"
)


def load_config():
    if not os.path.exists(CONFIG_PATH):
        return {}
    try:
        with open(CONFIG_PATH, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def save_config(data):
    cfg = load_config()
    cfg.update(data or {})
    os.makedirs(os.path.dirname(CONFIG_PATH), exist_ok=True)
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    return CONFIG_PATH


def save_api_key(key):
    """把 API Key 写入 data/config.json（仅本机，勿提交到仓库）。"""
    return save_config({"deepseek_api_key": key.strip()})


def get_api_key():
    """环境变量优先，其次本地配置。"""
    key = os.environ.get("DEEPSEEK_API_KEY", "").strip()
    if key:
        return key
    return (load_config().get("deepseek_api_key") or "").strip()


def is_configured():
    """是否已配置可用 Key。"""
    return bool(get_api_key())


def chat(prompt, system=SYSTEM_PROMPT, model=MODEL, temperature=TEMPERATURE,
         max_tokens=MAX_TOKENS, timeout=TIMEOUT):
    """调用 DeepSeek 对话补全。成功返回文本；任何失败返回 None（绝不抛异常）。"""
    key = get_api_key()
    if not key:
        return None
    payload = {
        "model": model,
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": prompt},
        ],
        "temperature": temperature,
        "max_tokens": max_tokens,
        "stream": False,
    }
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        "Authorization": "Bearer " + key,
    }
    for attempt in range(MAX_RETRIES + 1):
        req = urllib.request.Request(API_URL, data=body, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                data = json.loads(resp.read().decode("utf-8"))
            content = (data.get("choices") or [{}])[0].get("message", {}).get("content", "")
            return content.strip() or None
        except (urllib.error.HTTPError, urllib.error.URLError,
                TimeoutError, OSError, json.JSONDecodeError, KeyError):
            if attempt >= MAX_RETRIES:
                return None
    return None


if __name__ == "__main__":
    # 自检：是否已配置 + 直接发一条测试请求
    key = get_api_key()
    if not key:
        print("未配置 DeepSeek API Key。设置环境变量 DEEPSEEK_API_KEY 或运行：")
        print("    py -3 -c \"from app.llm import save_api_key; save_api_key('sk-xxxx')\"")
    else:
        print("API Key 已配置（%s…%s）" % (key[:6], key[-4:]))
        print("测试请求……")
        out = chat("对方说：你懂个屁。请按风格要求回怼。")
        if out:
            print("返回：\n%s" % out)
        else:
            print("请求失败（网络/额度/超时），引擎将自动回退本地组装。")
