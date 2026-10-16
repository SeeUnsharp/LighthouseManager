using Velopack;
using Velopack.Sources;

namespace LighthouseManager.Tray;

/// <summary>
///     Checks GitHub Releases for new versions. Only for installed builds; nothing is installed without the user
///     clicking "Update".
/// </summary>
/// <remarks>
///     With <see cref="IncludeBeta" />, GitHub pre-releases (tags like <c>v2.1.0-beta.1</c>) count too; the highest
///     version wins, so a beta is replaced by its final release. Never downgrades: turning beta off keeps the installed
///     beta until a newer stable release exists.
/// </remarks>
public sealed class Updates
{
    private const string RepositoryUrl = "https://github.com/SeeUnsharp/LighthouseManager";

    private UpdateManager? _manager;
    private bool _includeBeta;

    public Updates(bool includeBeta)
    {
        IncludeBeta = includeBeta;
    }

    public bool IsAvailable => _manager != null;

    public bool IncludeBeta
    {
        get => _includeBeta;
        set
        {
            _includeBeta = value;
            _manager = Create(value);
        }
    }

    public async Task<UpdateInfo?> CheckAsync()
    {
        if (_manager is not { } manager) return null;
        try
        {
            return await manager.CheckForUpdatesAsync();
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
        if (_manager is not { } manager) return;
        await manager.DownloadUpdatesAsync(update);
        manager.ApplyUpdatesAndRestart(update);
    }

    private static UpdateManager? Create(bool includeBeta)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, includeBeta));
            // Zip downloads and development builds have nothing to update
            return manager.IsInstalled ? manager : null;
        }
        catch (Exception)
        {
            // Not installed through Velopack
            return null;
        }
    }
}
