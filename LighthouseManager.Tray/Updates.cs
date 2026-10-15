using Velopack;
using Velopack.Sources;

namespace LighthouseManager.Tray;

/// <summary>
///     Checks GitHub Releases for new versions. Only for installed builds; nothing is installed without the user
///     clicking "Update".
/// </summary>
public sealed class Updates
{
    private const string RepositoryUrl = "https://github.com/SeeUnsharp/LighthouseManager";

    private readonly UpdateManager? _manager;

    public Updates()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            // Zip downloads and development builds have nothing to update
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception)
        {
            // Not installed through Velopack
        }
    }

    public bool IsAvailable => _manager != null;

    public async Task<UpdateInfo?> CheckAsync()
    {
        if (_manager == null) return null;
        try
        {
            return await _manager.CheckForUpdatesAsync();
        }
        catch (Exception)
        {
            // Offline, GitHub rate limit, ... try again next time
            return null;
        }
    }

    /// <summary>
    ///     Downloads and installs the update, then restarts the tray. The service is stopped and started again by the
    ///     update hooks (Windows) or after the restart (Linux, see Program).
    /// </summary>
    public async Task ApplyAsync(UpdateInfo update)
    {
        if (_manager == null) return;
        await _manager.DownloadUpdatesAsync(update);
        _manager.ApplyUpdatesAndRestart(update);
    }
}
