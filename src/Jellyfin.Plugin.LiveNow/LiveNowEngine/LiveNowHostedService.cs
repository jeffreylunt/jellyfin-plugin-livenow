using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveNow.LiveNowEngine;

/// <summary>
/// The in-process background loop that drives Live Now. Registered as an
/// <see cref="IHostedService"/> by <see cref="LiveNowServiceRegistrator"/>, so it starts with
/// the server and stops cleanly on shutdown — no external daemon, no scheduler granularity limits.
///
/// Lifecycle:
///  - on start: reconcile — strip stale "🔥" decorations AND clear owned favorites whose channel
///    is not currently warm (self-heals across an unclean stop, independent of teardown);
///  - loop: every poll interval, run one <see cref="LiveNowEngine.SyncOnceAsync"/> cycle;
///  - on stop: best-effort teardown (revert all decorations + remove all owned favorites). If the
///    host's shutdown window cuts teardown short, the next startup reconcile cleans up anyway.
/// </summary>
public sealed class LiveNowHostedService : BackgroundService
{
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<LiveNowHostedService> _logger;
    private LiveNowEngine? _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveNowHostedService"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userDataManager">User-data manager.</param>
    /// <param name="logger">Logger.</param>
    public LiveNowHostedService(
        ISessionManager sessionManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ILogger<LiveNowHostedService> logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    private LiveNowEngine BuildEngine()
    {
        var dataDir = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
        Directory.CreateDirectory(dataDir);
        var store = new OwnedFavoritesStore(Path.Combine(dataDir, "owned-favorites.json"), _logger);
        return new LiveNowEngine(_sessionManager, _userManager, _libraryManager, _userDataManager, store, _logger);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var engine = BuildEngine();
        _engine = engine;
        _logger.LogInformation("[LiveNow] background loop starting");

        try
        {
            await engine.ReconcileAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LiveNow] startup reconcile failed; continuing");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = Plugin.Instance?.Configuration;
            var enabled = cfg?.Enabled ?? true;
            var pollSeconds = Math.Max(5, cfg?.PollSeconds ?? 45);

            if (enabled)
            {
                try
                {
                    await engine.SyncOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LiveNow] sync cycle failed; will retry next poll");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("[LiveNow] background loop stopped");
    }

    /// <summary>
    /// On shutdown, run a best-effort teardown (revert names + remove owned favorites) so the
    /// server is left clean. Runs BEFORE the base StopAsync cancels the loop, with its own bounded
    /// token. If the host's shutdown window cuts it short, the next startup reconcile heals it.
    /// </summary>
    /// <param name="cancellationToken">The host shutdown token.</param>
    /// <returns>A task.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var engine = _engine;
        if (engine is not null)
        {
            using var teardownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            teardownCts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await engine.TeardownAsync(teardownCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LiveNow] teardown on shutdown failed (next startup reconcile will heal)");
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
