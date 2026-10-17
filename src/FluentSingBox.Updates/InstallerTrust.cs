using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FluentSingBox.Updates;

public static class InstallerTrust
{
    public static async Task VerifyInstallerAsync(ReleaseSettings settings, UpdateRelease release, string path, CancellationToken cancellation = default)
    {
        UpdateProtocol.ValidateRelease(release, settings.Repository);
        await UpdateProtocol.VerifyHashAsync(path, release, cancellation);
        Verify(path, settings.PublisherCertificateSha256);
    }

    public static void Verify(string path, string publisherCertificateSha256)
    {
        if (publisherCertificateSha256.Length != 64 || !publisherCertificateSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Update signing is not configured. Open the release page to install a test build manually.");
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1,
            UnionChoice = 1, File = pointer, StateAction = 1, ProviderFlags = 0x80 };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) throw new InvalidDataException($"Installer signature is not trusted (0x{result:X8}).");
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var hash = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            if (!hash.Equals(publisherCertificateSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installer was signed by an unexpected publisher certificate.");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallback; public IntPtr SipClient; public uint UiChoice;
        public uint RevocationChecks; public uint UnionChoice; public IntPtr File; public uint StateAction;
        public IntPtr StateData; public IntPtr Url; public uint ProviderFlags; public uint UiContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
