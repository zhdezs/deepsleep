using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TrollWrangler;

/// <summary>Windows DPAPI（crypt32.dll）薄封装：不依赖额外 NuGet 包。</summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[]? Run(bool protect, byte[] data)
    {
        if (data.Length == 0) return null;
        var inBlob = new DataBlob
        {
            cbData = data.Length,
            pbData = Marshal.AllocHGlobal(data.Length),
        };
        var outBlob = new DataBlob();
        var entropy = new DataBlob();
        try
        {
            Marshal.Copy(data, 0, inBlob.pbData, data.Length);
            bool ok = protect
                ? CryptProtectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob);
            if (!ok || outBlob.cbData <= 0 || outBlob.pbData == IntPtr.Zero) return null;
            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        catch { return null; }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    public static byte[]? TryProtect(byte[] data) => Run(true, data);
    public static byte[]? Unprotect(byte[] data) => Run(false, data);

    public static string UnprotectToString(byte[] data)
    {
        byte[]? plain = Unprotect(data);
        return plain == null ? "" : Encoding.UTF8.GetString(plain);
    }
}
