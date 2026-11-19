using System.Security.Cryptography;
using System.Text;

namespace CodexMultiLauncher;

/// <summary>One writer/UI per data root; the package debug helper does not acquire this lease.</summary>
internal sealed class LauncherInstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    public bool Acquired { get; }
    public LauncherInstanceLease(string root)
    {
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        _mutex = new Mutex(false, "Local\\CodexMultiLauncher.DataRoot." + hash);
        try { Acquired = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Acquired = true; }
    }
    public void Dispose() { if (Acquired) _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
