using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TrollWrangler;

/// <summary>
/// GitHub 令牌保险箱：多层加密，绝不落明文。
///
/// 文件格式（data\github.token，纯文本单行）：
///   DST3|mode|salt|keyWrap|nonce|tag|ciphertext      （都是 base64）
///
/// 四层保护：
///   ① 密钥层：随机 256 位主密钥；能拿到 DPAPI 就用 Windows DPAPI(CurrentUser) 封起来（mode=dpapi），
///      拿不到（例如服务/受限进程）就退化为 PBKDF2 从「机器+用户绑定串」派生（mode=pbkdf）
///   ② 数据层：AES-256-GCM 加密令牌本身，密钥为上一步的主密钥
///   ③ 绑定层：把「机器 GUID + 用户 SID + 机器名」作为 GCM 的 AAD（附加认证数据），
///      文件被复制到别的机器/账号时即使解开 DPAPI 也认证失败
///   ④ 文件层：文件 ACL 收紧到只允许当前 Windows 账号读写
///
/// 兼容旧格式：DST2| 或裸 base64（DPAPI 直接加密令牌）。
/// </summary>
public static class TokenVault
{
    private const int SaltLen = 16;
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32;
    private const int Pbkdf2Iterations = 200_000;

    /// <summary>机器 + 用户绑定串（作为 PBKDF2 口令 / GCM 的 AAD）。</summary>
    public static string MachineBinding()
    {
        string guid = "";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            guid = key?.GetValue("MachineGuid")?.ToString() ?? "";
        }
        catch { /* 读不到就用机器名代替 */ }
        string sid = "";
        try
        {
            sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        }
        catch { }
        return guid + "|" + sid + "|" + Environment.MachineName;
    }

    public static string Protect(string token)
    {
        byte[] binding = Encoding.UTF8.GetBytes(MachineBinding());
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLen);
        byte[] key;
        string mode;
        string keyWrap = "";

        byte[] rawKey = RandomNumberGenerator.GetBytes(KeyLen);
        byte[]? sealedKey = Dpapi.TryProtect(rawKey);
        if (sealedKey != null)
        {
            mode = "dpapi";
            keyWrap = Convert.ToBase64String(sealedKey);
            key = rawKey;
        }
        else
        {
            mode = "pbkdf";
            key = Rfc2898DeriveBytes.Pbkdf2(binding, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyLen);
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
        byte[] plain = Encoding.UTF8.GetBytes(token);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagLen];
#pragma warning disable SYSLIB0053
        using (var gcm = new AesGcm(key, TagLen))
#pragma warning restore SYSLIB0053
        {
            gcm.Encrypt(nonce, plain, cipher, tag, binding);
        }
        CryptographicOperations.ZeroMemory(plain);

        return string.Join('|', "DST3", mode, Convert.ToBase64String(salt), keyWrap,
            Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
    }

    public static string Unprotect(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return "";

        if (text.StartsWith("DST3|", StringComparison.Ordinal))
        {
            var p = text.Split('|');
            if (p.Length != 7) throw new FormatException("令牌文件格式不对");
            string mode = p[1];
            byte[] salt = Convert.FromBase64String(p[2]);
            byte[] nonce = Convert.FromBase64String(p[4]);
            byte[] tag = Convert.FromBase64String(p[5]);
            byte[] cipher = Convert.FromBase64String(p[6]);
            byte[] binding = Encoding.UTF8.GetBytes(MachineBinding());

            byte[] key;
            if (mode == "dpapi")
            {
                byte[] sealedKey = Convert.FromBase64String(p[3]);
                key = Dpapi.Unprotect(sealedKey) ?? throw new CryptographicException("DPAPI 解封失败");
            }
            else
            {
                key = Rfc2898DeriveBytes.Pbkdf2(binding, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyLen);
            }

            byte[] plain = new byte[cipher.Length];
#pragma warning disable SYSLIB0053
            using (var gcm = new AesGcm(key, TagLen))
#pragma warning restore SYSLIB0053
            {
                gcm.Decrypt(nonce, cipher, tag, plain, binding);
            }
            string token = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return token;
        }

        if (text.StartsWith("DST2|", StringComparison.Ordinal))
            text = text[5..];

        // 旧格式：DPAPI 直接加密的 base64
        byte[] legacy = Convert.FromBase64String(text);
        return Dpapi.UnprotectToString(legacy);
    }

    /// <summary>加密写盘 + 收紧 ACL；返回写入的路径。</summary>
    public static string Save(string token, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Protect(token), new UTF8Encoding(false));
        TryLockDown(path);
        return path;
    }

    public static string Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            return Unprotect(File.ReadAllText(path)).Trim();
        }
        catch { return ""; }
    }

    /// <summary>④ 文件层：只留当前账号，断开继承。</summary>
    public static void TryLockDown(string path)
    {
        try
        {
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var psi = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(
                Environment.SystemDirectory, "icacls.exe"))
            {
                Arguments = "\"" + path + "\" /inheritance:r /grant:r \"" + me + ":F\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);
        }
        catch { /* 非 NTFS 等情况忽略 */ }
    }
}
