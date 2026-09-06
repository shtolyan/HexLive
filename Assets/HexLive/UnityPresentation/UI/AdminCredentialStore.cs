using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
/// <summary>§161 OS-protected admin credentials. No PlayerPrefs or plaintext fallback.</summary>
internal static class AdminCredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr value);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static string Key(string server, string client)
    { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(server + "\n" + client))).Replace("-", ""); }
    public static string Read(string server, string client)
    {
        var account = Encoding.UTF8.GetBytes(Key(server, client)); var service = Encoding.UTF8.GetBytes("HexLiveAdmin");
        if (Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor)
        {
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out var length, out var data, out var item);
            if (status == -25300) return string.Empty;
            if (status != 0) throw new IOException("CredentialStoreUnavailable");
            try { var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
            finally { SecKeychainItemFreeContent(IntPtr.Zero, data); if (item != IntPtr.Zero) CFRelease(item); }
        }
        if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
        {
            var path = Path.Combine(Application.persistentDataPath, "admin-" + Key(server, client));
            return File.Exists(path) ? Encoding.UTF8.GetString(Protect(File.ReadAllBytes(path), false)) : string.Empty;
        }
        throw new PlatformNotSupportedException("CredentialStoreUnavailable");
    }
    public static void Write(string server, string client, string secret)
    {
        var account = Encoding.UTF8.GetBytes(Key(server, client)); var service = Encoding.UTF8.GetBytes("HexLiveAdmin"); var bytes = Encoding.UTF8.GetBytes(secret);
        if (Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor)
        {
            var found = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out _, out var data, out var item);
            int status;
            if (found == 0)
            {
                try { SecKeychainItemFreeContent(IntPtr.Zero, data); status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes); }
                finally { if (item != IntPtr.Zero) CFRelease(item); }
            }
            else if (found == -25300)
            {
                status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, (uint)bytes.Length, bytes, out item);
                if (item != IntPtr.Zero) CFRelease(item);
            }
            else throw new IOException("CredentialStoreUnavailable");
            if (status != 0) throw new IOException("CredentialStoreUnavailable");
            return;
        }
        if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
        { File.WriteAllBytes(Path.Combine(Application.persistentDataPath, "admin-" + Key(server, client)), Protect(bytes, true)); return; }
        throw new PlatformNotSupportedException("CredentialStoreUnavailable");
    }
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = encrypt ? CryptProtectData(ref input, "HexLive", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new IOException("CredentialStoreUnavailable");
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
}
