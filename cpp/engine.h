// -*- coding: utf-8 -*-
// 回怼引擎：攻击分类、文本脱敏、策略选择、实时组装回怼。
// 纯 C++17，零第三方依赖。字符串全程 UTF-8（编译需 /utf-8）。

#pragma once
#include <string>
#include <vector>
#include <map>
#include <deque>
#include <set>
#include <algorithm>
#include <random>
#include "data.h"

namespace engine {

// ---------------------------------------------------------------------------
// UTF-8 工具
// ---------------------------------------------------------------------------
inline std::vector<std::string> utf8_chars(const std::string& s) {
    std::vector<std::string> out;
    size_t i = 0;
    while (i < s.size()) {
        unsigned char c = (unsigned char)s[i];
        size_t len = 1;
        if ((c & 0x80) == 0) len = 1;
        else if ((c & 0xE0) == 0xC0) len = 2;
        else if ((c & 0xF0) == 0xE0) len = 3;
        else if ((c & 0xF8) == 0xF0) len = 4;
        out.push_back(s.substr(i, len));
        i += len;
    }
    return out;
}

inline size_t utf8_len(const std::string& s) {
    return utf8_chars(s).size();
}

// ---------------------------------------------------------------------------
// 随机
// ---------------------------------------------------------------------------
inline std::mt19937& rng() {
    static std::mt19937 g(std::random_device{}());
    return g;
}

inline int randint(int lo, int hi) {  // 含 hi
    if (hi < lo) std::swap(lo, hi);
    return std::uniform_int_distribution<int>(lo, hi)(rng());
}

template <typename T>
inline T choice(const std::vector<T>& v) {
    return v[randint(0, (int)v.size() - 1)];
}

template <typename T>
inline std::vector<T> sample(const std::vector<T>& v, int n) {
    std::vector<T> c = v;
    std::shuffle(c.begin(), c.end(), rng());
    if (n > (int)c.size()) n = (int)c.size();
    return std::vector<T>(c.begin(), c.begin() + n);
}

// ---------------------------------------------------------------------------
// 文本脱敏与工具
// ---------------------------------------------------------------------------
inline std::string replace_all(std::string s, const std::string& from, const std::string& to) {
    if (from.empty()) return s;
    size_t pos = 0;
    while ((pos = s.find(from, pos)) != std::string::npos) {
        s.replace(pos, from.size(), to);
        pos += to.size();
    }
    return s;
}

inline std::string mask_profanity(std::string text) {
    for (const auto& w : data::SENSITIVE_WORDS()) {
        text = replace_all(text, w, "**");
    }
    return text;
}

// ---------------------------------------------------------------------------
// 攻击分类与强度
// ---------------------------------------------------------------------------
inline std::string classify(const std::string& text) {
    for (const auto& cat : data::STRATEGY_ORDER()) { (void)cat; }  // noop 保持顺序无关
    for (const auto& kv : data::CATEGORY_KEYWORDS()) {
        for (const auto& w : kv.second) {
            if (text.find(w) != std::string::npos) return kv.first;
        }
    }
    return "general";
}

inline int aggression_score(const std::string& text) {
    int score = 0;
    for (const auto& kv : data::CATEGORY_KEYWORDS()) {
        for (const auto& w : kv.second) {
            if (text.find(w) != std::string::npos) score += 1;
        }
    }
    for (char c : text) {
        if (c == '!' ) score += 1;
    }
    // 全角感叹号是 3 字节，单独统计
    {
        auto chs = utf8_chars(text);
        score += (int)std::count(chs.begin(), chs.end(), "！");
    }
    score += std::min(3, (int)(utf8_len(text) / 60));
    return std::min(10, score);
}

// ---------------------------------------------------------------------------
// 引用片段抽取（对齐 Python composer 行为）
// ---------------------------------------------------------------------------
inline bool is_punct_char(const std::string& ch) {
    static const std::set<std::string> punct = []() {
        std::set<std::string> s;
        std::string all = "，。！？；：、,.!?;:…“”‘’\"'（）()【】[]《》〈〉<> \t\n";
        for (auto& c : utf8_chars(all)) s.insert(c);
        return s;
    }();
    return punct.count(ch) > 0;
}

inline std::string strip_punct(const std::string& s) {
    std::string out;
    for (auto& c : utf8_chars(s)) {
        if (!is_punct_char(c)) out += c;
    }
    return out;
}

// 从引用中剥离的无信息量攻击词/语气词
inline const std::vector<std::string>& EMPTY_WORDS() {
    static const std::vector<std::string> v = {
        "傻逼", "煞笔", "傻b", "傻B", "sb", "SB", "nmsl", "cnm", "你妈", "你妈的",
        "操你", "去死", "滚", "垃圾", "废物", "白痴", "智障", "脑残", "蠢货", "蠢猪",
        "人渣", "贱人", "饭桶", "狗东西", "有病", "嘴贱", "吃错药", "活该", "欠教育",
        "没家教", "丢人现眼", "狗屁不通", "什么玩意", "没素质", "呵呵", "哈哈", "笑死",
        "菜", "怂", "你算老几", "你也配", "你配", "没资格", "不配", "有本事",
        "等着瞧", "谁怕谁",
    };
    return v;
}

inline std::vector<std::string> extract_quotes(const std::string& text, int max_len = 14, int max_n = 3) {
    std::string t = mask_profanity(text);
    t = replace_all(t, "**", "");
    for (const auto& w : EMPTY_WORDS()) t = replace_all(t, w, "");
    // 标点替换为空格
    {
        std::string tmp;
        for (auto& c : utf8_chars(t)) {
            if (is_punct_char(c)) tmp += ' ';
            else tmp += c;
        }
        t = tmp;
    }
    // 按空白切分
    std::vector<std::string> cands;
    {
        std::string cur;
        for (char c : t) {
            if (c == ' ' || c == '\t' || c == '\n') {
                if (!cur.empty()) { cands.push_back(strip_punct(cur)); cur.clear(); }
            } else {
                cur += c;
            }
        }
        if (!cur.empty()) cands.push_back(strip_punct(cur));
    }
    std::vector<std::string> quotes;
    for (auto& c : cands) {
        if ((int)utf8_len(c) < 2) continue;
        if ((int)utf8_len(c) > max_len) {
            // 截断到 max_len 个字符
            auto chs = utf8_chars(c);
            c = "";
            for (int i = 0; i < max_len && i < (int)chs.size(); i++) c += chs[i];
        }
        bool dup = false;
        for (auto& q : quotes) if (q == c) dup = true;
        if (!dup && !c.empty()) quotes.push_back(c);
        if ((int)quotes.size() >= max_n) break;
    }
    return quotes;
}

inline std::string extract_quote(const std::string& text, int max_len = 16) {
    auto qs = extract_quotes(text, max_len, 1);
    return qs.empty() ? "" : qs[0];
}

inline std::string attack_word(const std::string& text) {
    for (const auto& kv : data::CATEGORY_KEYWORDS()) {
        for (const auto& w : kv.second) {
            if (text.find(w) != std::string::npos) return w;
        }
    }
    return "";
}

// ---------------------------------------------------------------------------
// 骨架填充
// ---------------------------------------------------------------------------
inline std::string fill(std::string tpl, const std::string& quote, const std::string& word, int turn) {
    tpl = replace_all(tpl, "{q}", quote.empty() ? "你上面这段话" : quote);
    tpl = replace_all(tpl, "{w}", word.empty() ? "你用的那些词" : word);
    tpl = replace_all(tpl, "{t}", std::to_string(turn));
    return tpl;
}

// ---------------------------------------------------------------------------
// 引擎
// ---------------------------------------------------------------------------
struct Reply {
    std::string troll_text;
    std::string response;
    std::string strategy;
    std::string strategy_name;
    std::string category;
    std::string category_name;
    int aggression = 0;
    int turns = 0;
    std::string source = "local";
};

class Engine {
public:
    struct State {
        int turns = 0;
        std::string last_strategy;
        int last_aggression = 0;
    };

    void new_session(int sid) {
        state_[sid] = State{};
        recent_[sid].clear();
    }

    std::string choose_strategy(const std::string& category, int agg, int sid,
                                const std::string& manual) {
        State& st = state_[sid];
        std::vector<std::string> pool;
        const auto& order = data::STRATEGY_ORDER();
        const auto& strategies = data::STRATEGIES();

        if (!manual.empty() && manual != "auto" && strategies.count(manual)) {
            pool = {manual};
        } else {
            if (agg >= 7 && st.turns == 0) pool = {"zen", "care"};
            else if (category == "ad_hominem") pool = {"praise", "zen"};
            else if (category == "threat") pool = {"care", "zen"};
            else if (category == "doubt") pool = {"logic", "question"};
            else if (category == "label") pool = {"praise", "zen"};
            else if (category == "provocation") pool = {"question", "zen", "echo"};
            else if (category == "general") pool = {"question", "logic", "zen"};
            else pool = std::vector<std::string>(order.begin(), order.end());

            bool rising = agg > st.last_aggression;
            if (rising && !st.last_strategy.empty() && pool.size() > 1) {
                pool.erase(std::remove(pool.begin(), pool.end(), st.last_strategy), pool.end());
                if (pool.empty()) pool = {"zen"};
            }
        }
        return choice(pool);
    }

    std::string pick_unique(const std::vector<std::string>& pool, int sid) {
        auto& used = recent_[sid];
        std::vector<std::string> candidates;
        for (auto& s : pool) {
            bool recent_dup = false;
            for (auto it = used.rbegin(); it != used.rend() && it != used.rbegin() + 6; ++it) {
                if (*it == s) { recent_dup = true; break; }
            }
            if (!recent_dup) candidates.push_back(s);
        }
        if (candidates.empty()) candidates = pool;
        std::string line = choice(candidates);
        used.push_back(line);
        while ((int)used.size() > 12) used.pop_front();
        return line;
    }

    std::string compose(const std::string& troll_text, const std::string& category,
                        const std::string& strategy, int agg, int turns, int sid, bool rising) {
        auto quotes = extract_quotes(troll_text);
        std::string quote = quotes.empty() ? "" : quotes[0];
        std::string word = attack_word(troll_text);

        int n_skel, n_cat, n_quick, n_dissect;
        if (agg >= 7) { n_skel = choice(std::vector<int>{3,3,4}); n_cat = choice(std::vector<int>{2,2,3}); n_quick = choice(std::vector<int>{2,3}); n_dissect = 2; }
        else if (agg >= 4 || (rising && agg >= 5)) { n_skel = choice(std::vector<int>{2,2,3}); n_cat = choice(std::vector<int>{1,2}); n_quick = choice(std::vector<int>{1,2}); n_dissect = (quotes.size() >= 2) ? 1 : 0; }
        else { n_skel = choice(std::vector<int>{1,1,2}); n_cat = choice(std::vector<int>{0,1}); n_quick = 1; n_dissect = 0; }

        const auto& skel_map = data::SKELETONS();
        const auto& cat_map = data::CATEGORY_LINES();
        std::vector<std::string> skel_pool = skel_map.count(strategy) ? skel_map.at(strategy) : skel_map.at("question");
        std::vector<std::string> cat_pool = cat_map.count(category) ? cat_map.at(category) : cat_map.at("general");

        auto skels = sample(skel_pool, n_skel);
        auto cats = sample(cat_pool, n_cat);
        std::vector<std::string> mid = skels;
        mid.insert(mid.end(), cats.begin(), cats.end());
        std::shuffle(mid.begin(), mid.end(), rng());

        std::vector<std::string> dissects;
        if (n_dissect && quotes.size() >= 2) {
            std::string qseq;
            for (size_t i = 0; i < quotes.size() && i < 3; i++) {
                if (i) qseq += "…";
                qseq += "「" + quotes[i] + "」";
            }
            for (int i = 0; i < n_dissect; i++) {
                dissects.push_back(fill(choice(data::DISSECT_TEMPLATES()), qseq, word, turns));
            }
        }

        auto quick_pool = data::QUICK_CUTS();
        std::vector<std::string> quicks;
        for (int i = 0; i < n_quick && i < (int)quick_pool.size(); i++) {
            quicks.push_back(fill(pick_unique(quick_pool, sid), quote, word, turns));
        }

        std::vector<std::string> seq = mid;
        seq.insert(seq.end(), dissects.begin(), dissects.end());
        seq.insert(seq.end(), quicks.begin(), quicks.end());
        std::shuffle(seq.begin(), seq.end(), rng());

        std::vector<std::string> parts;
        parts.push_back(fill(pick_unique(data::QUOTE_OPENS(), sid), quote, word, turns));
        for (auto& x : seq) parts.push_back(fill(x, quote, word, turns));
        parts.push_back(fill(pick_unique(data::FINISHERS(), sid), quote, word, turns));

        std::string out;
        for (size_t i = 0; i < parts.size(); i++) {
            if (i) out += "\n";
            out += parts[i];
        }
        return mask_profanity(out);
    }

    Reply generate(const std::string& text, int sid, const std::string& manual = "auto") {
        Reply r;
        r.troll_text = mask_profanity(text);
        r.category = classify(text);
        r.aggression = aggression_score(text);
        r.category_name = data::CATEGORY_NAMES().count(r.category)
                              ? data::CATEGORY_NAMES().at(r.category) : r.category;

        State& st = state_[sid];
        bool rising = r.aggression > st.last_aggression;
        r.strategy = choose_strategy(r.category, r.aggression, sid, manual);
        r.strategy_name = data::STRATEGIES().count(r.strategy)
                              ? data::STRATEGIES().at(r.strategy).name : r.strategy;
        r.strategy_name += " · 实时组装";

        r.response = compose(text, r.category, r.strategy, r.aggression, st.turns, sid, rising);

        st.turns += 1;
        st.last_strategy = r.strategy;
        st.last_aggression = r.aggression;
        r.turns = st.turns;
        r.source = "local";
        return r;
    }

    std::vector<std::string> alternatives(const std::string& text, int n = 3,
                                          const std::string& manual = "auto") {
        std::string category = classify(text);
        int agg = aggression_score(text);
        std::string strategy = choose_strategy(category, agg, 0, manual);
        std::vector<std::string> out;
        for (int i = 0; i < n; i++) {
            out.push_back(compose(text, category, strategy, agg, 0, 1000 + i, false));
        }
        return out;
    }

private:
    std::map<int, State> state_;
    std::map<int, std::deque<std::string>> recent_;
};

}  // namespace engine
