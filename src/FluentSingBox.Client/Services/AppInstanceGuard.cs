using System.Security.Cryptography;
using System.Text;

namespace FluentSingBox.Client.Services;

public static class AppInstanceGuard
{
    private static Mutex? _mutex;
    public static bool TryAcquire()
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppPaths.DataDirectory).ToUpperInvariant())));
        _mutex = new Mutex(false, "Local\\fluent-sing-box-" + key);
        try { return _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }
}
