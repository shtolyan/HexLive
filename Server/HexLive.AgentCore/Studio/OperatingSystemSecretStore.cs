using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HexLive.AgentCore.Studio;

/// <summary>Credentials never travel in process arguments, profile JSON or logs.</summary>
public sealed class OperatingSystemSecretStore : ISecretStore
{
    private const string Service = "HexLive.AgentStudio";
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<string?> ReadAsync(string id, CancellationToken token)
    {
        CheckId(id); await _gate.WaitAsync(token);
        try { return await Task.Run(() => Read(id), token); }
        finally { _gate.Release(); }
    }
    public async Task WriteAsync(string id, string value, CancellationToken token)
    {
        CheckId(id);
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 2560)
            throw new InvalidDataException("InvalidCredentialSize");
        await _gate.WaitAsync(token);
        try { await Task.Run(() => Write(id, value), token); }
        finally { _gate.Release(); }
    }
    public async Task DeleteAsync(string id, CancellationToken token)
    {
        CheckId(id); await _gate.WaitAsync(token);
        try { await Task.Run(() => Delete(id), token); }
        finally { _gate.Release(); }
    }
    private static void CheckId(string id)
    {
        if (id.Length is < 1 or > 128 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new InvalidDataException("InvalidCredentialId");
    }
    private static string? Read(string id)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CredRead(Service + "/" + id, 1, 0, out var pointer))
            { if (Marshal.GetLastWin32Error() == 1168) return null; throw Failure(); }
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                var bytes = new byte[credential.BlobSize];
                Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                try { return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { CredFree(pointer); }
        }
        if (OperatingSystem.IsMacOS())
        {
            var status = Find(id, out var size, out var bytes, out var item);
            if (status == -25300) return null;
            if (status != 0) throw Failure();
            try { return Marshal.PtrToStringUTF8(bytes, checked((int)size)); }
            finally { SecKeychainItemFreeContent(IntPtr.Zero, bytes); if (item != IntPtr.Zero) CFRelease(item); }
        }
        throw new PlatformNotSupportedException("NativeSecretStoreUnavailable");
    }
    private static void Write(string id, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var blob = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, blob, bytes.Length);
                    var credential = new Credential { Type = 1, Target = Service + "/" + id,
                        BlobSize = (uint)bytes.Length, Blob = blob, Persist = 2, UserName = Service };
                    if (!CredWrite(ref credential, 0)) throw Failure();
                }
                finally { Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length); Marshal.FreeHGlobal(blob); }
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                var status = Find(id, out _, out var oldData, out var item);
                if (status == -25300)
                {
                    status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
                        (uint)id.Length, id, (uint)bytes.Length, bytes, out item);
                    if (item != IntPtr.Zero) CFRelease(item);
                }
                else if (status == 0)
                {
                    SecKeychainItemFreeContent(IntPtr.Zero, oldData);
                    try { status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes); }
                    finally { if (item != IntPtr.Zero) CFRelease(item); }
                }
                if (status != 0) throw Failure();
                return;
            }
            throw new PlatformNotSupportedException("NativeSecretStoreUnavailable");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static void Delete(string id)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CredDelete(Service + "/" + id, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw Failure();
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            var status = Find(id, out _, out var data, out var item);
            if (status == -25300) return;
            if (status != 0) throw Failure();
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            try { if (SecKeychainItemDelete(item) != 0) throw Failure(); }
            finally { if (item != IntPtr.Zero) CFRelease(item); }
            return;
        }
        throw new PlatformNotSupportedException("NativeSecretStoreUnavailable");
    }
    private static Exception Failure() => new InvalidOperationException("OperatingSystemCredentialOperationFailed");
    private static int Find(string id, out uint size, out IntPtr data, out IntPtr item) =>
        SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)id.Length, id, out size, out data, out item);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string Target;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string service, uint accountLength,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string account, out uint length, out IntPtr data, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string service, uint accountLength,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string account, uint length, byte[] data, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr item);
}
