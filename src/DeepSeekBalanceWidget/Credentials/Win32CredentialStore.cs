using System.Runtime.InteropServices;

namespace DeepSeekBalanceWidget.Credentials;

/// <summary>
/// Windows Credential Manager 自写 P/Invoke（探针 B §1-1 全链实证做法，D-07/可行性 §5-8）：
/// CredWriteW / CredReadW / CredDeleteW，CRED_TYPE_GENERIC(=1)、CRED_PERSIST_LOCAL_MACHINE(=2)、
/// CharSet.Unicode；TargetName = DeepSeekBalanceWidget_ApiKey。
/// 删除后再读返回 ERROR_NOT_FOUND(1168) 属正常未配置路径（§6-7）。
/// </summary>
public sealed class Win32CredentialStore : ICredentialStore
{
    public const string TargetName = "DeepSeekBalanceWidget_ApiKey";
    public const int ErrorNotFound = 1168;

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string targetName, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, uint type, uint reservedFlag);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public bool Write(string secret, out int win32Error)
    {
        IntPtr targetPtr = Marshal.StringToHGlobalUni(TargetName);
        IntPtr blobPtr = Marshal.StringToHGlobalUni(secret);
        try
        {
            var cred = new CREDENTIAL
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                Comment = IntPtr.Zero,
                CredentialBlobSize = (uint)(secret.Length * 2),
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount = 0,
            };
            bool ok = CredWriteW(ref cred, 0);
            win32Error = Marshal.GetLastWin32Error();
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
        }
    }

    public bool TryRead(out string? secret, out int win32Error)
    {
        if (!CredReadW(TargetName, CRED_TYPE_GENERIC, 0, out IntPtr ptr))
        {
            win32Error = Marshal.GetLastWin32Error();
            secret = null;
            return false;
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            secret = cred.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
            win32Error = 0;
            return !string.IsNullOrEmpty(secret);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public bool Delete(out int win32Error)
    {
        bool ok = CredDeleteW(TargetName, CRED_TYPE_GENERIC, 0);
        win32Error = Marshal.GetLastWin32Error();
        return ok || win32Error == ErrorNotFound;   // 不存在视为删除成功
    }
}
