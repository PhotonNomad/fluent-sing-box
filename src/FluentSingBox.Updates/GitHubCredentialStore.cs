using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluentSingBox.Updates;

// Generic credentials belong to the signed-in Windows user; never serialize tokens into update state.
public static class GitHubCredentialStore
{
    private const int NotFound = 1168;
    private static string Target(string repository) => "FluentSingBox/GitHub/" + repository.ToLowerInvariant();

    public static string? Read(string repository)
    {
        if (!CredRead(Target(repository), 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    public static void Save(string repository, string token)
    {
        token = token.Trim();
        if (token.Length is < 1 or > 1024 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidDataException("Invalid GitHub access token format.");
        var blob = Marshal.StringToHGlobalUni(token);
        try
        {
            var credential = new Credential
            {
                Type = 1, TargetName = Target(repository), BlobSize = checked((uint)token.Length * 2),
                Blob = blob, Persist = 2, UserName = "GitHub"
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(blob); }
    }

    public static void Delete(string repository)
    {
        if (!CredDelete(Target(repository), 1, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound) throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
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
    private static extern void CredFree(IntPtr credential);
}
