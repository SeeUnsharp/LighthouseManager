namespace LighthouseManager.Core;

/// <summary>
///     Per-user single instance lock: an exclusively opened lock file in <see cref="AppPaths.StateDirectory" />. The
///     OS releases it when the process ends, also after a crash, so there are no stale locks.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly FileStream _lock;

    private SingleInstance(FileStream fileLock)
    {
        _lock = fileLock;
    }

    public void Dispose() => _lock.Dispose();

    /// <returns>The lock, or null if another process holds it.</returns>
    public static SingleInstance? TryAcquire(string name)
    {
        var path = LockFile(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // FileShare.None is an exclusive lock on Windows and an flock() on Linux
            return new SingleInstance(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Whether another process holds the lock right now.
    /// </summary>
    public static bool IsHeld(string name)
    {
        using var instance = TryAcquire(name);
        return instance == null;
    }

    private static string LockFile(string name) => Path.Combine(AppPaths.StateDirectory, name + ".lock");
}
