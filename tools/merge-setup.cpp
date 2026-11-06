// deepsleep 安装包分片合并器（Windows / 纯 Win32 / 无第三方依赖）
//
// 用途：从 Gitee 下载的安装包被切成了多个分片（deepsleep-Setup.exe.part1 / .part2 …），
//       把它们按顺序拼回 deepsleep-Setup.exe，校验 SHA-256，然后直接启动安装程序。
//
// 用法：把本 exe 和所有分片放在同一个文件夹里，双击即可。
//   /norun     只合并，不启动安装程序
//   /nopause   完成后不按任意键（脚本里用）
//   /force     目标文件已存在且大小正确时也强制重新合并
//
// 校验：合并后会打印 SHA-256；如果同目录有 "<目标文件名>.sha256" 文件（内容是一串 64 位
//       十六进制），会严格比对，不一致就报错不启动。
//
// 编译：g++ -std=c++11 -O2 -static -municode -o 合并安装包.exe merge-setup.cpp

#include <windows.h>
#include <string>
#include <vector>
#include <map>
#include <algorithm>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cstdarg>
#include <cwchar>
#include <cwctype>
#include <cstdlib>

// ------------------------------------------------------------------ SHA-256

struct Sha256 {
    uint32_t h[8];
    uint64_t len;
    unsigned char buf[64];
    size_t buflen;

    Sha256() { Reset(); }

    void Reset() {
        static const uint32_t init[8] = {
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
            0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
        };
        memcpy(h, init, sizeof(h));
        len = 0;
        buflen = 0;
    }

    static uint32_t Ror(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }

    void Block(const unsigned char* p) {
        static const uint32_t K[64] = {
            0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
            0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
            0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
            0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
            0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
            0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
            0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
            0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
        };
        uint32_t w[64];
        for (int i = 0; i < 16; i++)
            w[i] = ((uint32_t)p[i * 4] << 24) | ((uint32_t)p[i * 4 + 1] << 16) |
                   ((uint32_t)p[i * 4 + 2] << 8) | (uint32_t)p[i * 4 + 3];
        for (int i = 16; i < 64; i++) {
            uint32_t s0 = Ror(w[i - 15], 7) ^ Ror(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint32_t s1 = Ror(w[i - 2], 17) ^ Ror(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3];
        uint32_t e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int i = 0; i < 64; i++) {
            uint32_t S1 = Ror(e, 6) ^ Ror(e, 11) ^ Ror(e, 25);
            uint32_t ch = (e & f) ^ (~e & g);
            uint32_t t1 = hh + S1 + ch + K[i] + w[i];
            uint32_t S0 = Ror(a, 2) ^ Ror(a, 13) ^ Ror(a, 22);
            uint32_t mj = (a & b) ^ (a & c) ^ (b & c);
            uint32_t t2 = S0 + mj;
            hh = g; g = f; f = e; e = d + t1;
            d = c; c = b; b = a; a = t1 + t2;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d;
        h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }

    void Update(const void* data, size_t n) {
        const unsigned char* p = (const unsigned char*)data;
        len += n;
        while (n > 0) {
            if (buflen == 0 && n >= 64) { Block(p); p += 64; n -= 64; continue; }
            size_t take = 64 - buflen;
            if (take > n) take = n;
            memcpy(buf + buflen, p, take);
            buflen += take; p += take; n -= take;
            if (buflen == 64) { Block(buf); buflen = 0; }
        }
    }

    std::wstring Hex() {
        unsigned char pad[72];
        size_t padlen;
        uint64_t bits = len * 8;
        pad[0] = 0x80;
        size_t rem = (size_t)(len % 64);
        padlen = (rem < 56) ? (56 - rem) : (120 - rem);
        memset(pad + 1, 0, padlen - 1);
        for (int i = 0; i < 8; i++) pad[padlen + i] = (unsigned char)(bits >> (56 - i * 8));
        Update(pad, padlen + 8);
        wchar_t out[65];
        for (int i = 0; i < 8; i++) {
            wchar_t part[9];
            _snwprintf(part, 9, L"%08x", h[i]);
            part[8] = 0;
            memcpy(out + i * 8, part, 8 * sizeof(wchar_t));
        }
        out[64] = 0;
        return std::wstring(out);
    }
};

// ------------------------------------------------------------------ 小工具

// 输出：统一走 UTF-8。控制台已切到 65001，重定向到管道/文件也不会丢字。
static void Put(const wchar_t* s) {
    int need = WideCharToMultiByte(CP_UTF8, 0, s, -1, NULL, 0, NULL, NULL);
    if (need <= 1) return;
    std::vector<char> buf(need);
    WideCharToMultiByte(CP_UTF8, 0, s, -1, &buf[0], need, NULL, NULL);
    fwrite(&buf[0], 1, (size_t)need - 1, stdout);
    fflush(stdout);
}

static bool g_tty = false;   // stdout 是不是真控制台

static void Say(const wchar_t* fmt, ...) {
    wchar_t buf[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf(buf, 2047, fmt, ap);
    va_end(ap);
    buf[2047] = 0;
    Put(buf);
}

static void SayLine(const wchar_t* fmt, ...) {
    wchar_t buf[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf(buf, 2047, fmt, ap);
    va_end(ap);
    buf[2047] = 0;
    wcscat(buf, L"\n");
    Put(buf);
}

static bool FileSize(const std::wstring& path, uint64_t* out) {
    WIN32_FILE_ATTRIBUTE_DATA d;
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &d)) return false;
    *out = ((uint64_t)d.nFileSizeHigh << 32) | d.nFileSizeLow;
    return true;
}

static std::wstring BaseName(const std::wstring& path) {
    size_t p = path.find_last_of(L"\\/");
    return (p == std::wstring::npos) ? path : path.substr(p + 1);
}

static std::wstring DirName(const std::wstring& path) {
    size_t p = path.find_last_of(L"\\/");
    return (p == std::wstring::npos) ? L"." : path.substr(0, p);
}

static bool EndsWithI(const std::wstring& s, const std::wstring& suffix) {
    if (s.size() < suffix.size()) return false;
    std::wstring tail = s.substr(s.size() - suffix.size());
    for (size_t i = 0; i < tail.size(); i++)
        if (towlower(tail[i]) != towlower(suffix[i])) return false;
    return true;
}

// 参数比较：/norun、-norun、--norun 都认
static bool IsFlag(const char* a, const char* name) {
    const char* p = a;
    while (*p == '/' || *p == '-') p++;
    return _stricmp(p, name) == 0;
}

static bool ContainsI(const std::wstring& s, const std::wstring& needle) {
    std::wstring a = s, b = needle;
    for (size_t i = 0; i < a.size(); i++) a[i] = towlower(a[i]);
    for (size_t i = 0; i < b.size(); i++) b[i] = towlower(b[i]);
    return a.find(b) != std::wstring::npos;
}

// 读一个文件的 SHA-256（分块读，别把 130MB 全塞内存）
static bool HashFile(const std::wstring& path, std::wstring* hex) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    Sha256 sha;
    static const DWORD kChunk = 1 << 20;
    std::vector<unsigned char> buf(kChunk);
    DWORD read = 0;
    while (ReadFile(h, &buf[0], kChunk, &read, NULL) && read > 0)
        sha.Update(&buf[0], read);
    CloseHandle(h);
    *hex = sha.Hex();
    return true;
}

// 读同目录的 "<目标>.sha256"（内容：一串 64 位十六进制，允许前后空白/换行）
static bool ReadSidecarHash(const std::wstring& file, std::wstring* hex) {
    HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    char buf[512] = {0};
    DWORD read = 0;
    ReadFile(h, buf, sizeof(buf) - 1, &read, NULL);
    CloseHandle(h);
    std::string s(buf, read);
    std::string hexs;
    for (size_t i = 0; i < s.size(); i++) {
        char c = s[i];
        if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
            hexs += (char)tolower(c);
    }
    if (hexs.size() < 64) return false;
    *hex = std::wstring(hexs.begin(), hexs.begin() + 64);
    return true;
}

// ------------------------------------------------------------------ 分片发现

struct PartSet {
    std::wstring base;                 // 合并后的文件名
    std::vector<std::wstring> parts;   // 按序号排好
    uint64_t total;
};

// 在 dir 里找 "xxx.partN"，按 base 分组，挑一组最像安装包的
// 返回 0=成功 1=一个分片都没有 2=组里缺片 3=只有一片（多半是没下全）
static int FindParts(const std::wstring& dir, PartSet* out, std::wstring* baseName) {
    std::map<std::wstring, std::map<int, std::wstring> > groups;

    WIN32_FIND_DATAW fd;
    std::wstring pattern = dir + L"\\*.part*";
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return 1;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        std::wstring name = fd.cFileName;
        size_t pos = name.rfind(L".part");
        if (pos == std::wstring::npos) continue;
        std::wstring num = name.substr(pos + 5);
        if (num.empty()) continue;
        bool digits = true;
        for (size_t i = 0; i < num.size(); i++) if (!iswdigit(num[i])) { digits = false; break; }
        if (!digits) continue;
        int n = _wtoi(num.c_str());
        if (n <= 0) continue;
        groups[name.substr(0, pos)][n] = dir + L"\\" + name;
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    if (groups.empty()) return 1;

    // 选哪一组：优先名字里带 setup 的；否则取分片最多的；再否则取总字节最大的
    std::wstring best;
    size_t bestCount = 0;
    uint64_t bestSize = 0;
    bool bestIsSetup = false;
    for (std::map<std::wstring, std::map<int, std::wstring> >::iterator it = groups.begin();
         it != groups.end(); ++it) {
        uint64_t sum = 0;
        for (std::map<int, std::wstring>::iterator p = it->second.begin(); p != it->second.end(); ++p) {
            uint64_t sz = 0;
            if (FileSize(p->second, &sz)) sum += sz;
        }
        bool isSetup = ContainsI(it->first, L"setup");
        bool better = false;
        if (best.empty()) better = true;
        else if (isSetup && !bestIsSetup) better = true;
        else if (isSetup == bestIsSetup) {
            if (it->second.size() > bestCount) better = true;
            else if (it->second.size() == bestCount && sum > bestSize) better = true;
        }
        if (better) { best = it->first; bestCount = it->second.size(); bestSize = sum; bestIsSetup = isSetup; }
    }
    if (best.empty()) return 1;
    if (baseName) *baseName = best;

    std::map<int, std::wstring>& m = groups[best];
    if (m.find(1) == m.end()) return 2;                     // 没有 part1 就不认
    int maxN = 0;
    for (std::map<int, std::wstring>::iterator p = m.begin(); p != m.end(); ++p)
        if (p->first > maxN) maxN = p->first;
    // 只有一片：正常发布的安装包不会切成一片（Gitee 上限 100MB，130MB 的 Setup 必是 2 片以上）。
    // 硬合并只会得到一个能运行但残缺的 exe，不如直接拦下来让人重下。
    if (maxN < 2) return 3;

    out->base = best;
    out->parts.clear();
    out->total = 0;
    for (int i = 1; i <= maxN; i++) {
        std::map<int, std::wstring>::iterator p = m.find(i);
        if (p == m.end()) return 2;                         // 中间缺片 → 不合并，报错更有用
        uint64_t sz = 0;
        if (!FileSize(p->second, &sz)) return 2;
        out->parts.push_back(p->second);
        out->total += sz;
    }
    return 0;
}

// ------------------------------------------------------------------ 合并

static bool MergeParts(const PartSet& set, const std::wstring& outPath) {
    HANDLE out = CreateFileW(outPath.c_str(), GENERIC_WRITE, 0, NULL,
                             CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (out == INVALID_HANDLE_VALUE) return false;

    static const DWORD kChunk = 1 << 20;
    std::vector<unsigned char> buf(kChunk);
    uint64_t done = 0;
    bool ok = true;

    for (size_t i = 0; i < set.parts.size() && ok; i++) {
        HANDLE in = CreateFileW(set.parts[i].c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        if (in == INVALID_HANDLE_VALUE) { ok = false; break; }
        DWORD read = 0;
        while (ReadFile(in, &buf[0], kChunk, &read, NULL) && read > 0) {
            DWORD wrote = 0;
            if (!WriteFile(out, &buf[0], read, &wrote, NULL) || wrote != read) { ok = false; break; }
            done += wrote;
            if (set.total > 0) {
                double pct = done * 100.0 / (double)set.total;
                if (g_tty) {
                    Say(L"\r  合并中 %5.1f%%  (%.1f / %.1f MB)   ",
                        pct, done / 1048576.0, (double)set.total / 1048576.0);
                } else {
                    static int lastBucket = -1;
                    int bucket = (int)(pct / 10);
                    if (bucket != lastBucket) {
                        lastBucket = bucket;
                        SayLine(L"  合并中 %5.1f%%  (%.1f / %.1f MB)",
                                pct, done / 1048576.0, (double)set.total / 1048576.0);
                    }
                }
            }
        }
        CloseHandle(in);
    }
    CloseHandle(out);
    Say(L"\r%-60s\r", L"");
    return ok;
}

// 是不是一个正常的 PE 可执行文件（MZ + PE 头）
static bool LooksLikeExe(const std::wstring& path) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    unsigned char head[0x400] = {0};
    DWORD read = 0;
    ReadFile(h, head, sizeof(head), &read, NULL);
    CloseHandle(h);
    if (read < 0x40 || head[0] != 'M' || head[1] != 'Z') return false;
    uint32_t peOff = *(uint32_t*)(head + 0x3C);
    if (peOff + 4 > read) return false;
    return head[peOff] == 'P' && head[peOff + 1] == 'E';
}

// ------------------------------------------------------------------ main

int main(int argc, char** argv) {
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleTitleW(L"deepsleep 安装包合并器");
    DWORD cmode = 0;
    g_tty = GetConsoleMode(GetStdHandle(STD_OUTPUT_HANDLE), &cmode) != 0;

    bool noRun = false, noPause = false, force = false;
    for (int i = 1; i < argc; i++) {
        if (IsFlag(argv[i], "norun")) noRun = true;
        else if (IsFlag(argv[i], "nopause")) noPause = true;
        else if (IsFlag(argv[i], "force")) force = true;
    }

    wchar_t self[MAX_PATH] = {0};
    GetModuleFileNameW(NULL, self, MAX_PATH);
    std::wstring dir = DirName(self);

    SayLine(L"");
    SayLine(L"  deepsleep 安装包合并器");
    SayLine(L"  ------------------------------------------------");
    SayLine(L"  目录：%s", dir.c_str());
    SayLine(L"");

    PartSet set;
    std::wstring picked;
    int fr = FindParts(dir, &set, &picked);
    if (fr != 0) {
        if (fr == 1) {
            SayLine(L"  ✗ 没找到分片。");
            SayLine(L"    请把 deepsleep-Setup.exe.part1、.part2 … 和本程序放在同一个文件夹里再运行。");
        } else if (fr == 3) {
            SayLine(L"  ✗ 只找到 1 个分片（%s.part1），安装包不可能只有一片。", picked.c_str());
            SayLine(L"    多半是后面的分片没下完，补齐后重试。");
        } else {
            SayLine(L"  ✗ 分片不连续：%s 缺少中间的 partN。", picked.c_str());
            SayLine(L"    把 .part1 .part2 … 全部下载齐（不要漏），再运行本程序。");
        }
        goto done;
    }

    SayLine(L"  找到 %d 个分片：", (int)set.parts.size());
    for (size_t i = 0; i < set.parts.size(); i++)
        SayLine(L"    %d. %s", (int)i + 1, BaseName(set.parts[i]).c_str());
    SayLine(L"  合计 %.2f MB → 合并为 %s", (double)set.total / 1048576.0, set.base.c_str());
    SayLine(L"");

    {
        std::wstring outPath = dir + L"\\" + set.base;
        uint64_t existSize = 0;
        bool exists = FileSize(outPath, &existSize);

        if (exists && existSize == set.total && !force) {
            SayLine(L"  目标文件已存在且大小一致，跳过合并（要重来加 -force）");
        } else {
            SayLine(L"  正在合并…");
            if (!MergeParts(set, outPath)) {
                SayLine(L"  ✗ 合并失败：写文件出错（磁盘满了？目标被占用？）");
                DeleteFileW(outPath.c_str());
                goto done;
            }
            uint64_t got = 0;
            if (!FileSize(outPath, &got) || got != set.total) {
                SayLine(L"  ✗ 合并后大小不对：期望 %llu，实际 %llu", set.total, got);
                DeleteFileW(outPath.c_str());
                goto done;
            }
        }

        if (!LooksLikeExe(outPath)) {
            SayLine(L"  ✗ 合并出来的不是有效的 exe（分片可能下坏了，重新下载试试）");
            goto done;
        }

        std::wstring hex;
        if (HashFile(outPath, &hex)) {
            SayLine(L"  大小    %.2f MB", (double)set.total / 1048576.0);
            SayLine(L"  SHA-256 %s", hex.c_str());

            std::wstring want;
            std::wstring sidecar = outPath + L".sha256";
            if (ReadSidecarHash(sidecar, &want)) {
                if (_wcsicmp(hex.c_str(), want.c_str()) != 0) {
                    SayLine(L"  ✗ 校验不通过：和 %s 里写的不一致", BaseName(sidecar).c_str());
                    SayLine(L"    期望 %s", want.c_str());
                    SayLine(L"    分片可能下载不完整，删掉重新下载。");
                    goto done;
                }
                SayLine(L"  ✓ 校验通过（与 %s 一致）", BaseName(sidecar).c_str());
            } else {
                SayLine(L"  （同目录没有 %s，跳过比对；可拿上面的值和发布页对照）",
                        BaseName(sidecar).c_str());
            }
        }

        if (noRun) {
            SayLine(L"");
            SayLine(L"  ✓ 完成（/norun：没有启动安装程序）");
            goto done;
        }

        SayLine(L"");
        SayLine(L"  启动安装程序…");
        HINSTANCE r = ShellExecuteW(NULL, L"open", outPath.c_str(), NULL, dir.c_str(), SW_SHOWNORMAL);
        if ((INT_PTR)r <= 32) {
            SayLine(L"  ✗ 启动失败（%d）。可以手动双击 %s", (int)(INT_PTR)r, set.base.c_str());
            goto done;
        }
        return 0;
    }

done:
    SayLine(L"");
    if (!noPause) {
        SayLine(L"  按任意键退出…");
        FlushConsoleInputBuffer(GetStdHandle(STD_INPUT_HANDLE));
        system("pause > nul");
    }
    return 1;
}
