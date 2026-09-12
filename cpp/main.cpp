// -*- coding: utf-8 -*-
// 以理服人 · 键盘侠反制助手（C++ Win32 原生客户端）
// 现代微信聊天框风格：渐变顶栏、阴影圆角气泡、渐变头像、圆角输入框、
// 渐变发送按钮、消息时间分组、双缓冲无闪烁、圆角无边框窗口。
// 纯 Win32 API + GDI，零第三方依赖。编译需 /utf-8。

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <string>
#include <vector>
#include <algorithm>
#include <ctime>
#include <cmath>
#include "engine.h"

#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")

// ---- 颜色 ----
static const COLORREF BG            = RGB(0xed, 0xed, 0xed);
static const COLORREF TOP_TOP       = RGB(0x1a, 0x1a, 0x1a);   // 顶栏渐变起
static const COLORREF TOP_BOTTOM    = RGB(0x2b, 0x2b, 0x2b);   // 顶栏渐变止
static const COLORREF TOP_TEXT      = RGB(0xff, 0xff, 0xff);
static const COLORREF TOP_SUB       = RGB(0x9f, 0x9f, 0x9f);
static const COLORREF BUBBLE_SELF_T = RGB(0xa0, 0xf0, 0x78);   // 我方气泡渐变起
static const COLORREF BUBBLE_SELF_B = RGB(0x95, 0xec, 0x69);
static const COLORREF BUBBLE_OTHER  = RGB(0xff, 0xff, 0xff);
static const COLORREF ACCENT        = RGB(0x07, 0xc1, 0x60);
static const COLORREF ACCENT_DARK   = RGB(0x06, 0xad, 0x56);
static const COLORREF ACCENT_LIGHT  = RGB(0x22, 0xd8, 0x76);
static const COLORREF AVATAR_SELF_T = RGB(0x22, 0xd8, 0x76);
static const COLORREF AVATAR_SELF_B = RGB(0x06, 0xad, 0x56);
static const COLORREF AVATAR_OTH_T  = RGB(0xc6, 0xc6, 0xc6);
static const COLORREF AVATAR_OTH_B  = RGB(0x9a, 0x9a, 0x9a);
static const COLORREF TEXT_CLR      = RGB(0x1a, 0x1a, 0x1a);
static const COLORREF MUTED         = RGB(0x9a, 0x9a, 0x9a);
static const COLORREF INPUT_WHITE   = RGB(0xff, 0xff, 0xff);
static const COLORREF INPUT_BAR     = RGB(0xf7, 0xf7, 0xf7);
static const COLORREF DIVIDER       = RGB(0xe0, 0xe0, 0xe0);
static const COLORREF SHADOW        = RGB(0x00, 0x00, 0x00);

// ---- 布局常量 ----
static const int TOP_H       = 56;
static const int INPUT_H     = 80;
static const int AVATAR_R    = 18;
static const int PAD_X       = 14;
static const int PAD_Y       = 12;
static const int GAP         = 9;
static const int BPX         = 13;
static const int BPY         = 10;
static const int CORNER      = 12;    // 窗口圆角半径
static const int BUBBLE_R    = 10;    // 气泡圆角

// ---- 消息模型 ----
struct Message {
    bool is_self = false;
    bool is_sys = false;
    std::wstring text;
    std::wstring meta;
    std::wstring time_str;   // HH:MM
    bool show_time = false;  // 是否显示时间分隔
};

// ---- 全局 ----
static engine::Engine g_engine;
static int g_sid = 1;
static std::vector<Message> g_messages;
static HFONT g_font_msg = nullptr;
static HFONT g_font_meta = nullptr;
static HFONT g_font_title = nullptr;
static HFONT g_font_input = nullptr;
static HFONT g_font_time = nullptr;
static HWND g_hwnd = nullptr;
static HWND g_input = nullptr;
static HWND g_btn = nullptr;
static bool g_btn_hover = false;
static int g_scroll_pos = 0;
static int g_content_h = 0;

// ---------------------------------------------------------------------------
// UTF-8 <-> UTF-16
// ---------------------------------------------------------------------------
static std::wstring u8to16(const std::string& s) {
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}
static std::string u16to8(const std::wstring& w) {
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

static std::wstring now_hm() {
    time_t t = time(nullptr);
    struct tm lt;
    localtime_s(&lt, &t);
    wchar_t buf[8];
    swprintf_s(buf, L"%02d:%02d", lt.tm_hour, lt.tm_min);
    return buf;
}

// ---------------------------------------------------------------------------
// GDI 绘制原语
// ---------------------------------------------------------------------------
static int text_w(HDC dc, HFONT f, const std::wstring& s) {
    HGDIOBJ old = SelectObject(dc, f);
    SIZE sz{};
    GetTextExtentPoint32W(dc, s.c_str(), (int)s.size(), &sz);
    SelectObject(dc, old);
    return sz.cx;
}
static int font_h(HDC dc, HFONT f) {
    TEXTMETRICW tm{};
    HGDIOBJ old = SelectObject(dc, f);
    GetTextMetricsW(dc, &tm);
    SelectObject(dc, old);
    return tm.tmHeight;
}

static std::vector<std::wstring> wrap_text(HDC dc, HFONT f, const std::wstring& text, int max_w) {
    std::vector<std::wstring> lines;
    auto push = [&](const std::wstring& seg) {
        std::wstring c;
        for (wchar_t ch : seg) {
            if (text_w(dc, f, c + ch) <= max_w) { c += ch; }
            else { lines.push_back(c); c = ch; }
        }
        lines.push_back(c);
    };
    std::wstring seg;
    for (wchar_t ch : text) {
        if (ch == L'\n') { push(seg); seg.clear(); }
        else seg += ch;
    }
    push(seg);
    return lines;
}

// 垂直渐变填充
static void fill_vgrad(HDC dc, RECT r, COLORREF top, COLORREF bottom) {
    int h = r.bottom - r.top;
    if (h <= 0) return;
    for (int i = 0; i < h; i++) {
        int t = (int)((double)i / (h - 1) * 255);
        COLORREF c = RGB(
            (GetRValue(top) * (255 - t) + GetRValue(bottom) * t) / 255,
            (GetGValue(top) * (255 - t) + GetGValue(bottom) * t) / 255,
            (GetBValue(top) * (255 - t) + GetBValue(bottom) * t) / 255);
        HPEN p = CreatePen(PS_SOLID, 1, c);
        HPEN op = (HPEN)SelectObject(dc, p);
        MoveToEx(dc, r.left, r.top + i, nullptr);
        LineTo(dc, r.right, r.top + i);
        SelectObject(dc, op);
        DeleteObject(p);
    }
}

// 圆角矩形路径
static void round_rect_path(HDC dc, int x1, int y1, int x2, int y2, int r) {
    BeginPath(dc);
    MoveToEx(dc, x1 + r, y1, nullptr);
    LineTo(dc, x2 - r, y1);
    // 右上角
    ArcTo(dc, x2 - 2 * r, y1, x2, y1 + 2 * r, x2 - r, y1, x2, y1 + r);
    LineTo(dc, x2, y2 - r);
    // 右下角
    ArcTo(dc, x2 - 2 * r, y2 - 2 * r, x2, y2, x2, y2 - r, x2 - r, y2);
    LineTo(dc, x1 + r, y2);
    // 左下角
    ArcTo(dc, x1, y2 - 2 * r, x1 + 2 * r, y2, x1 + r, y2, x1, y2 - r);
    LineTo(dc, x1, y1 + r);
    // 左上角
    ArcTo(dc, x1, y1, x1 + 2 * r, y1 + 2 * r, x1, y1 + r, x1 + r, y1);
    CloseFigure(dc);
    EndPath(dc);
}

static void fill_round_rect(HDC dc, int x1, int y1, int x2, int y2, int r, COLORREF fill) {
    round_rect_path(dc, x1, y1, x2, y2, r);
    HBRUSH br = CreateSolidBrush(fill);
    HBRUSH ob = (HBRUSH)SelectObject(dc, br);
    FillPath(dc);
    SelectObject(dc, ob);
    DeleteObject(br);
}

// 圆角渐变填充
static void fill_round_vgrad(HDC dc, int x1, int y1, int x2, int y2, int r,
                             COLORREF top, COLORREF bottom) {
    round_rect_path(dc, x1, y1, x2, y2, r);
    SelectClipPath(dc, RGN_COPY);
    RECT rr = { x1, y1, x2, y2 };
    fill_vgrad(dc, rr, top, bottom);
    SelectClipRgn(dc, nullptr);
}

// 气泡带阴影
static void draw_bubble_shadow(HDC dc, int x1, int y1, int x2, int y2, int r) {
    for (int o = 0; o < 3; o++) {
        COLORREF c = RGB(0, 0, 0);
        // 阴影用 alpha 近似：画多层半透明不好做，这里用更浅的灰描边
        (void)c;
    }
    // 简化阴影：偏移绘制浅灰底
    fill_round_rect(dc, x1 + 1, y1 + 2, x2 + 1, y2 + 2, r, RGB(0xd8, 0xd8, 0xd8));
}

// 渐变圆形
static void fill_vgrad_ellipse(HDC dc, int cx, int cy, int rad, COLORREF top, COLORREF bottom) {
    for (int i = 0; i < rad * 2; i++) {
        int y = cy - rad + i;
        int dy = y - (cy - rad);
        double t = (double)dy / (rad * 2 - 1);
        COLORREF c = RGB(
            (int)(GetRValue(top) * (1 - t) + GetRValue(bottom) * t),
            (int)(GetGValue(top) * (1 - t) + GetGValue(bottom) * t),
            (int)(GetBValue(top) * (1 - t) + GetBValue(bottom) * t));
        double half = rad * sin(acos((double)(y - cy) / rad));
        int x1 = (int)(cx - half), x2 = (int)(cx + half);
        HPEN p = CreatePen(PS_SOLID, 1, c);
        HPEN op = (HPEN)SelectObject(dc, p);
        MoveToEx(dc, x1, y, nullptr);
        LineTo(dc, x2 + 1, y);
        SelectObject(dc, op);
        DeleteObject(p);
    }
}

// ---------------------------------------------------------------------------
// 聊天区绘制（含时间分组）
// ---------------------------------------------------------------------------
static void draw_chat(HDC dc, RECT rc) {
    HBRUSH bgbr = CreateSolidBrush(BG);
    FillRect(dc, &rc, bgbr);
    DeleteObject(bgbr);
    SetBkMode(dc, TRANSPARENT);

    const int LH = font_h(dc, g_font_msg) + 7;
    const int META_H = font_h(dc, g_font_meta) + 4;
    const int TIME_H = font_h(dc, g_font_time) + 8;
    int cw = rc.right - rc.left;
    int max_bubble_w = (int)(cw * 0.70);

    int y = PAD_Y - g_scroll_pos;

    for (auto& m : g_messages) {
        // 时间分隔
        if (m.show_time) {
            int ty = y;
            std::wstring ts = m.time_str;
            SetTextColor(dc, RGB(0xbb, 0xbb, 0xbb));
            SelectObject(dc, g_font_time);
            int tw = text_w(dc, g_font_time, ts);
            // 时间胶囊
            int px = (cw - tw) / 2 - 8;
            fill_round_rect(dc, px, ty + 2, px + tw + 16, ty + TIME_H - 2, 9, RGB(0xdd, 0xdd, 0xdd));
            SetTextColor(dc, RGB(0x88, 0x88, 0x88));
            RECT tr = { px + 8, ty + 2, px + tw + 8, ty + TIME_H - 2 };
            DrawTextW(dc, ts.c_str(), (int)ts.size(), &tr, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
            y += TIME_H;
        }

        if (m.is_sys) {
            SetTextColor(dc, RGB(0xb0, 0xb0, 0xb0));
            SelectObject(dc, g_font_meta);
            auto lines = wrap_text(dc, g_font_meta, m.text, (int)(cw * 0.9));
            for (auto& ln : lines) {
                int w = text_w(dc, g_font_meta, ln);
                RECT tr = { (cw - w) / 2, y, (cw + w) / 2, y + font_h(dc, g_font_meta) };
                DrawTextW(dc, ln.c_str(), (int)ln.size(), &tr, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
                y += font_h(dc, g_font_meta) + 2;
            }
            y += 8;
            continue;
        }

        auto lines = wrap_text(dc, g_font_msg, m.text, max_bubble_w - BPX * 2);
        int bubble_w = 40;
        for (auto& ln : lines) bubble_w = std::max(bubble_w, text_w(dc, g_font_msg, ln) + BPX * 2);
        bubble_w = std::min(bubble_w, max_bubble_w);
        int bubble_h = (int)lines.size() * LH + BPY * 2;

        int avatar_x = m.is_self ? (cw - PAD_X - AVATAR_R) : (PAD_X + AVATAR_R);
        int bx1, bx2;
        if (m.is_self) { bx2 = avatar_x - AVATAR_R - GAP; bx1 = bx2 - bubble_w; }
        else { bx1 = avatar_x + AVATAR_R + GAP; bx2 = bx1 + bubble_w; }
        int by1 = y + AVATAR_R - 4, by2 = by1 + bubble_h;

        // 阴影
        fill_round_rect(dc, bx1 + 1, by1 + 3, bx2 + 1, by2 + 3, BUBBLE_R, RGB(0xd8, 0xd8, 0xd8));

        // 气泡主体
        if (m.is_self)
            fill_round_vgrad(dc, bx1, by1, bx2, by2, BUBBLE_R, BUBBLE_SELF_T, BUBBLE_SELF_B);
        else
            fill_round_rect(dc, bx1, by1, bx2, by2, BUBBLE_R, BUBBLE_OTHER);

        // 小尾巴（三角）
        if (m.is_self) {
            POINT tri[3] = { {bx2, by1 + 14}, {bx2 + 6, by1 + 20}, {bx2, by1 + 26} };
            HBRUSH tb = CreateSolidBrush(BUBBLE_SELF_B);
            HBRUSH ob = (HBRUSH)SelectObject(dc, tb);
            HPEN tp = CreatePen(PS_NULL, 0, 0);
            HPEN op = (HPEN)SelectObject(dc, tp);
            Polygon(dc, tri, 3);
            SelectObject(dc, op); DeleteObject(tp);
            SelectObject(dc, ob); DeleteObject(tb);
        } else {
            POINT tri[3] = { {bx1, by1 + 14}, {bx1 - 6, by1 + 20}, {bx1, by1 + 26} };
            HBRUSH tb = CreateSolidBrush(BUBBLE_OTHER);
            HBRUSH ob = (HBRUSH)SelectObject(dc, tb);
            HPEN tp = CreatePen(PS_NULL, 0, 0);
            HPEN op = (HPEN)SelectObject(dc, tp);
            Polygon(dc, tri, 3);
            SelectObject(dc, op); DeleteObject(tp);
            SelectObject(dc, ob); DeleteObject(tb);
        }

        // 气泡文字
        SetTextColor(dc, TEXT_CLR);
        SelectObject(dc, g_font_msg);
        int ty = by1 + BPY;
        for (auto& ln : lines) {
            RECT lr = { bx1 + BPX, ty, bx2 - BPX, ty + LH };
            DrawTextW(dc, ln.c_str(), (int)ln.size(), &lr,
                      (m.is_self ? DT_RIGHT : DT_LEFT) | DT_TOP | DT_NOPREFIX);
            ty += LH;
        }

        // 头像（渐变圆）
        fill_vgrad_ellipse(dc, avatar_x, y + AVATAR_R, AVATAR_R,
                           m.is_self ? AVATAR_SELF_T : AVATAR_OTH_T,
                           m.is_self ? AVATAR_SELF_B : AVATAR_OTH_B);
        SetTextColor(dc, RGB(255, 255, 255));
        SelectObject(dc, g_font_title);
        RECT art = { avatar_x - AVATAR_R, y, avatar_x + AVATAR_R, y + AVATAR_R * 2 };
        std::wstring ach = m.is_self ? L"理" : L"侠";
        DrawTextW(dc, ach.c_str(), 1, &art, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

        // 元信息
        if (!m.meta.empty()) {
            SetTextColor(dc, MUTED);
            SelectObject(dc, g_font_meta);
            int mw = text_w(dc, g_font_meta, m.meta);
            int mty = by2 + 4;
            if (m.is_self) {
                RECT mr = { bx2 - mw, mty, bx2, mty + META_H };
                DrawTextW(dc, m.meta.c_str(), (int)m.meta.size(), &mr, DT_RIGHT | DT_TOP | DT_NOPREFIX);
            } else {
                RECT mr = { bx1, mty, bx1 + mw, mty + META_H };
                DrawTextW(dc, m.meta.c_str(), (int)m.meta.size(), &mr, DT_LEFT | DT_TOP | DT_NOPREFIX);
            }
        }

        y += bubble_h + AVATAR_R + 24;
    }
    g_content_h = y + g_scroll_pos;
}

// ---------------------------------------------------------------------------
// 发送
// ---------------------------------------------------------------------------
static void add_message(bool is_self, const std::wstring& text, const std::wstring& meta) {
    Message m;
    m.is_self = is_self;
    m.text = text;
    m.meta = meta;
    m.time_str = now_hm();
    // 与上一条间隔超 5 分钟则显示时间
    if (!g_messages.empty()) {
        m.show_time = g_messages.back().time_str != m.time_str;
    } else {
        m.show_time = true;
    }
    g_messages.push_back(m);
}

static void send_reply() {
    int len = GetWindowTextLengthW(g_input);
    if (len <= 0) return;
    std::wstring wbuf(len, 0);
    GetWindowTextW(g_input, &wbuf[0], len + 1);
    std::string text = u16to8(wbuf);
    size_t b = text.find_first_not_of(" \t\r\n");
    size_t e = text.find_last_not_of(" \t\r\n");
    if (b == std::string::npos) return;
    text = text.substr(b, e - b + 1);

    engine::Reply r = g_engine.generate(text, g_sid);

    add_message(false, u8to16(r.troll_text),
                u8to16(r.category_name + " · 强度" + std::to_string(r.aggression)));
    add_message(true, u8to16(r.response), u8to16(r.strategy_name));

    SetWindowTextW(g_input, L"");
    g_scroll_pos = INT_MAX;
    InvalidateRect(g_hwnd, nullptr, FALSE);
}

// ---------------------------------------------------------------------------
// 输入框子类化：回车发送，Shift+回车换行
// ---------------------------------------------------------------------------
static WNDPROC g_edit_old_proc = nullptr;

static LRESULT CALLBACK EditProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
    if (msg == WM_KEYDOWN) {
        if (w == VK_RETURN && !(GetKeyState(VK_SHIFT) & 0x8000)) {
            send_reply();
            return 0;
        }
    }
    return CallWindowProcW(g_edit_old_proc, hwnd, msg, w, l);
}

// ---------------------------------------------------------------------------
// 窗口过程
// ---------------------------------------------------------------------------
enum { ID_INPUT = 101, ID_SEND = 102 };

static void layout_controls(HWND hwnd) {
    RECT rc; GetClientRect(hwnd, &rc);
    int cw = rc.right, ch = rc.bottom;
    int bar_y = ch - INPUT_H;
    SetWindowPos(g_input, nullptr, 14, bar_y + 12, cw - 14 - 72 - 12, INPUT_H - 24, SWP_NOZORDER);
    SetWindowPos(g_btn, nullptr, cw - 72 - 10, bar_y + 12, 72, INPUT_H - 24, SWP_NOZORDER);
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
    switch (msg) {
        case WM_CREATE: {
            g_font_msg = CreateFontW(-17, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_font_meta = CreateFontW(-12, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_font_title = CreateFontW(-16, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_font_input = CreateFontW(-16, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                DEFAULT_PITCH, L"Microsoft YaHei UI");
            g_font_time = CreateFontW(-12, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                DEFAULT_PITCH, L"Microsoft YaHei UI");

            g_input = CreateWindowExW(0, L"EDIT", L"",
                WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN,
                0, 0, 0, 0, hwnd, (HMENU)ID_INPUT, GetModuleHandle(nullptr), nullptr);
            SendMessageW(g_input, WM_SETFONT, (WPARAM)g_font_input, TRUE);
            SendMessageW(g_input, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, MAKELPARAM(10, 10));
            g_edit_old_proc = (WNDPROC)SetWindowLongPtrW(g_input, GWLP_WNDPROC,
                                                         (LONG_PTR)EditProc);

            g_btn = CreateWindowExW(0, L"BUTTON", L"发送",
                WS_CHILD | WS_VISIBLE | BS_OWNERDRAW,
                0, 0, 0, 0, hwnd, (HMENU)ID_SEND, GetModuleHandle(nullptr), nullptr);

            Message sys;
            sys.is_sys = true;
            sys.time_str = now_hm();
            sys.show_time = true;
            sys.text = L"新会话已创建。把对方的话粘贴到输入框，回车发送即可。";
            g_messages.push_back(sys);
            return 0;
        }
        case WM_SIZE:
            layout_controls(hwnd);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_CTLCOLOREDIT: {
            HDC dc = (HDC)w;
            SetTextColor(dc, TEXT_CLR);
            SetBkColor(dc, INPUT_WHITE);
            static HBRUSH br = CreateSolidBrush(INPUT_WHITE);
            return (LRESULT)br;
        }
        case WM_CTLCOLORSTATIC: {
            HDC dc = (HDC)w;
            SetTextColor(dc, TOP_TEXT);
            SetBkColor(dc, TOP_BOTTOM);
            static HBRUSH br = CreateSolidBrush(TOP_BOTTOM);
            return (LRESULT)br;
        }

        case WM_COMMAND:
            if (LOWORD(w) == ID_SEND) {
                if (HIWORD(w) == BN_CLICKED) send_reply();
            }
            return 0;

        // 发送按钮悬停
        case WM_DRAWITEM: {
            LPDRAWITEMSTRUCT di = (LPDRAWITEMSTRUCT)l;
            if (di->CtlID == ID_SEND) {
                RECT r = di->rcItem;
                bool hover = g_btn_hover;
                bool pressed = (di->itemState & ODS_SELECTED);
                // 圆角渐变按钮
                int h = r.bottom - r.top;
                if (pressed) {
                    fill_round_vgrad(di->hDC, r.left, r.top, r.right, r.bottom, 10,
                                     ACCENT_DARK, ACCENT_DARK);
                } else if (hover) {
                    fill_round_vgrad(di->hDC, r.left, r.top, r.right, r.bottom, 10,
                                     ACCENT_LIGHT, ACCENT);
                } else {
                    fill_round_vgrad(di->hDC, r.left, r.top, r.right, r.bottom, 10,
                                     ACCENT, ACCENT_DARK);
                }
                SetBkMode(di->hDC, TRANSPARENT);
                SetTextColor(di->hDC, RGB(255, 255, 255));
                SelectObject(di->hDC, g_font_title);
                RECT tr = r;
                DrawTextW(di->hDC, L"发送", 2, &tr, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
                return TRUE;
            }
            break;
        }

        case WM_MOUSEMOVE: {
            // 检测是否悬停在发送按钮上
            RECT r; GetClientRect(g_btn, &r);
            POINT pt = { GET_X_LPARAM(l), GET_Y_LPARAM(l) };
            POINT origin = { 0, 0 };
            ClientToScreen(g_btn, &origin);
            ScreenToClient(g_hwnd, &origin);
            bool inside = pt.x >= origin.x && pt.x < origin.x + (r.right - r.left) &&
                          pt.y >= origin.y && pt.y < origin.y + (r.bottom - r.top);
            if (inside != g_btn_hover) {
                g_btn_hover = inside;
                InvalidateRect(g_btn, nullptr, FALSE);
            }
            // 跟踪离开
            TRACKMOUSEEVENT tme{ sizeof(TRACKMOUSEEVENT), TME_LEAVE, g_hwnd, 0 };
            TrackMouseEvent(&tme);
            return 0;
        }
        case WM_MOUSELEAVE:
            if (g_btn_hover) {
                g_btn_hover = false;
                InvalidateRect(g_btn, nullptr, FALSE);
            }
            return 0;

        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(hwnd, &ps);
            RECT rc; GetClientRect(hwnd, &rc);
            int cw = rc.right, ch = rc.bottom;

            // 双缓冲
            HDC mem = CreateCompatibleDC(dc);
            HBITMAP bmp = CreateCompatibleBitmap(dc, cw, ch);
            HGDIOBJ obmp = SelectObject(mem, bmp);

            // 整体背景
            HBRUSH bg = CreateSolidBrush(BG);
            FillRect(mem, &rc, bg);
            DeleteObject(bg);

            // 顶栏渐变
            RECT top = { 0, 0, cw, TOP_H };
            fill_vgrad(mem, top, TOP_TOP, TOP_BOTTOM);
            // 顶栏底部高光线
            HPEN hl = CreatePen(PS_SOLID, 1, RGB(0x3a, 0x3a, 0x3a));
            HPEN ohl = (HPEN)SelectObject(mem, hl);
            MoveToEx(mem, 0, TOP_H - 1, nullptr);
            LineTo(mem, cw, TOP_H - 1);
            SelectObject(mem, ohl); DeleteObject(hl);

            SetBkMode(mem, TRANSPARENT);
            // 标题 + 头像徽标
            SetTextColor(mem, TOP_TEXT);
            SelectObject(mem, g_font_title);
            RECT ttr = { 16, 6, cw, 32 };
            DrawTextW(mem, L"网络键盘侠", -1, &ttr, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
            SetTextColor(mem, TOP_SUB);
            SelectObject(mem, g_font_meta);
            RECT str = { 16, 32, cw, TOP_H };
            DrawTextW(mem, L"在线 · 实时组装", -1, &str, DT_LEFT | DT_VCENTER | DT_SINGLELINE);

            // 输入区
            int bar_y = ch - INPUT_H;
            RECT ibar = { 0, bar_y, cw, ch };
            HBRUSH ib = CreateSolidBrush(INPUT_BAR);
            FillRect(mem, &ibar, ib);
            DeleteObject(ib);
            HPEN dv = CreatePen(PS_SOLID, 1, DIVIDER);
            HPEN odv = (HPEN)SelectObject(mem, dv);
            MoveToEx(mem, 0, bar_y, nullptr);
            LineTo(mem, cw, bar_y);
            SelectObject(mem, odv); DeleteObject(dv);

            // 聊天区裁剪
            RECT chat = { 0, TOP_H, cw, bar_y };
            SaveDC(mem);
            IntersectClipRect(mem, chat.left, chat.top, chat.right, chat.bottom);
            draw_chat(mem, chat);
            RestoreDC(mem, -1);

            // 复制到窗口
            BitBlt(dc, 0, 0, cw, ch, mem, 0, 0, SRCCOPY);
            SelectObject(mem, obmp);
            DeleteObject(bmp);
            DeleteDC(mem);
            EndPaint(hwnd, &ps);
            return 0;
        }
        case WM_ERASEBKGND:
            return 1;

        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
    }
    return DefWindowProcW(hwnd, msg, w, l);
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, PWSTR, int nShow) {
    g_sid = 1;
    g_engine.new_session(g_sid);

    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInst;
    wc.lpszClassName = L"TrollWranglerWnd";
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hbrBackground = nullptr;
    RegisterClassW(&wc);

    int wpx = 460, hpx = 760;
    RECT wr = { 0, 0, wpx, hpx };
    AdjustWindowRect(&wr, WS_OVERLAPPEDWINDOW, FALSE);

    g_hwnd = CreateWindowExW(0, wc.lpszClassName, L"以理服人 · 键盘侠反制助手",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
        wr.right - wr.left, wr.bottom - wr.top,
        nullptr, nullptr, hInst, nullptr);

    ShowWindow(g_hwnd, nShow);
    UpdateWindow(g_hwnd);

    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0)) {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    return (int)m.wParam;
}
