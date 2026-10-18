using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VNsave.Core;

public interface ICredentialStore
{
    string? Read();
    void Write(string refreshToken);
    void Delete();
}

public sealed class WindowsCredentialStore : ICredentialStore
{
    public const string TargetName = "VNsave/dropbox";
    private const uint Generic = 1;
    private const int NotFound = 1168;

    public string? Read()
    {
        if (!CredRead(TargetName, Generic, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw CredentialError("読み出し", error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Write(string refreshToken)
    {
        var bytes = Encoding.UTF8.GetBytes(refreshToken);
        if (bytes.Length > 2560)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new UserError("refresh token が資格情報マネージャーの保存上限を超えています。平文には保存しません。");
        }
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential
            {
                Type = Generic,
                TargetName = TargetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = pointer,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE: persists for this Windows user on this PC.
                UserName = "VNsave"
            };
            if (!CredWrite(ref credential, 0)) throw CredentialError("保存", Marshal.GetLastWin32Error());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.FreeHGlobal(pointer);
        }
    }

    public void Delete()
    {
        if (!CredDelete(TargetName, Generic, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != NotFound) throw CredentialError("削除", error);
        }
    }

    private static UserError CredentialError(string action, int error) =>
        new($"Windows 資格情報の{action}に失敗しました（Win32 {error}）。平文ファイルには切り替えません。");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
