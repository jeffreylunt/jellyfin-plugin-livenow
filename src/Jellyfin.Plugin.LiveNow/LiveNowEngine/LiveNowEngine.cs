using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using static Jellyfin.Plugin.LiveNow.LiveNowEngine.FavoritePlan;

namespace Jellyfin.Plugin.LiveNow.LiveNowEngine;

/// <summary>
/// The in-process port of the Live Now daemon loop. One <see cref="SyncOnceAsync"/> call is one
/// poll cycle: find warm channels (active live-TV playback), decorate their names "🔥xN", float
/// them to every user's favorites, and revert anything that went cold. All writes are in-process
/// via Jellyfin APIs, so the float is instant (no per-user HTTP fan-out).
///
/// Safety/reliability is ported exactly from the daemon:
///  - name decoration is idempotent + stateless-revertible (we scan the channel list for stale
///    "🔥" prefixes, so a crash never leaves a channel stuck decorated);
///  - favorites use the persistent owned-set + the pure <see cref="FavoritePlan"/> so we NEVER
///    remove a favorite a user set themselves, and partial failures self-heal next cycle.
/// </summary>
public sealed class LiveNowEngine
{
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly OwnedFavoritesStore _store;
    private readonly ILogger _logger;

    // In-memory hint of channels we are decorating {channelId: originalName}. Cold-revert does NOT
    // rely on this alone — it also scans the live channel list (stateless), so it self-heals.
    private readonly Dictionary<string, string> _decorated = new();

    private HashSet<UserChannel> _owned;
    private HashSet<string> _prevWarm = new();
    private int _cycle;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveNowEngine"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager (active playback).</param>
    /// <param name="userManager">User manager (all users).</param>
    /// <param name="libraryManager">Library manager (resolve + enumerate channels).</param>
    /// <param name="userDataManager">User-data manager (IsFavorite read/write, fires change event).</param>
    /// <param name="store">Owned-favorites persistence.</param>
    /// <param name="logger">Logger.</param>
    public LiveNowEngine(
        ISessionManager sessionManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        OwnedFavoritesStore store,
        ILogger logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _store = store;
        _logger = logger;
        _owned = store.Load();
    }

    /// <summary>
    /// Gets the current configuration (or sensible defaults if the plugin isn't loaded).
    /// </summary>
    private static Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>
    /// Map of channelId → live viewer count for channels with active live-TV playback.
    /// For live TV the NowPlayingItem IS the channel: item.Id is the channel id.
    /// </summary>
    private Dictionary<string, int> GetWarmChannels()
    {
        var warm = new Dictionary<string, int>();
        foreach (var session in _sessionManager.Sessions)
        {
            var item = session.NowPlayingItem;
            if (item is null || item.Type != BaseItemKind.TvChannel || item.Id == Guid.Empty)
            {
                continue;
            }

            var cid = item.Id.ToString("N");
            warm[cid] = warm.GetValueOrDefault(cid) + 1;
        }

        return warm;
    }

    /// <summary>All live-TV channel BaseItems on the server (bounded, matching the daemon's Limit=1000).</summary>
    private IReadOnlyList<BaseItem> GetAllChannels() =>
        _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.TvChannel },
            Limit = 1000,
        });

    private async Task SetChannelNameAsync(BaseItem item, string newName, CancellationToken ct)
    {
        if (string.Equals(item.Name, newName, StringComparison.Ordinal))
        {
            return; // already correct — no write
        }

        item.Name = newName;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup reconcile. Two parts, both self-healing across an unclean stop (a crash, or a
    /// teardown that exceeded the host's shutdown window):
    ///  1. NAMES — strip any stale "🔥" decoration so no channel stays stuck showing a stale count.
    ///  2. FAVORITES — remove any plugin-owned favorite whose channel is NOT currently warm, so a
    ///     skipped teardown can't leave warm-channel favorites floated indefinitely. (We still only
    ///     ever clear plugin-OWNED favorites — a user's own favorite is never touched.)
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        _logger.LogInformation("[LiveNow] reconcile: scanning channels for stale decorations");
        var cleaned = 0;
        foreach (var ch in GetAllChannels())
        {
            ct.ThrowIfCancellationRequested();
            if (NamePrefix.IsDecorated(ch.Name))
            {
                await SetChannelNameAsync(ch, NamePrefix.Strip(ch.Name), ct).ConfigureAwait(false);
                cleaned++;
            }
        }

        _logger.LogInformation("[LiveNow] reconcile: cleaned {Count} stale-decorated channel(s)", cleaned);

        ReconcileOwnedFavorites(ct);
    }

    /// <summary>
    /// Remove any plugin-owned favorite whose channel is not currently warm. Drops an owned entry
    /// only on a CONFIRMED clear (a failed clear stays owned for the next cycle to retry). Makes
    /// the float self-healing across restarts even if teardown was skipped.
    /// </summary>
    private void ReconcileOwnedFavorites(CancellationToken ct)
    {
        if (_owned.Count == 0)
        {
            return;
        }

        HashSet<string> warmIds;
        try
        {
            warmIds = GetWarmChannels().Keys.ToHashSet();
        }
        catch (Exception ex)
        {
            // Can't read sessions → don't guess; leave owned untouched, the normal cycle will heal.
            _logger.LogWarning(ex, "[LiveNow] reconcile: could not read warm channels; skipping favorite reconcile");
            return;
        }

        var stale = _owned.Where(k => !warmIds.Contains(k.ChannelId)).ToList();
        if (stale.Count == 0)
        {
            return;
        }

        var newOwned = new HashSet<UserChannel>(_owned);
        var removed = 0;
        foreach (var key in stale)
        {
            if (TryClearFavorite(key, ct))
            {
                newOwned.Remove(key);
                removed++;
            }
        }

        if (removed > 0)
        {
            _owned = newOwned;
            _store.Save(_owned);
        }

        _logger.LogInformation("[LiveNow] reconcile: cleared {Count} stale owned favorite(s) (owned now {Owned})", removed, _owned.Count);
    }

    /// <summary>
    /// Graceful-stop teardown: strip all name decorations + remove ALL plugin-owned favorites.
    /// Retains any owned entry whose clear fails (don't orphan on a transient failure).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task TeardownAsync(CancellationToken ct)
    {
        _logger.LogInformation("[LiveNow] teardown: reverting names + removing owned favorites");

        foreach (var ch in GetAllChannels())
        {
            if (NamePrefix.IsDecorated(ch.Name))
            {
                try
                {
                    await SetChannelNameAsync(ch, NamePrefix.Strip(ch.Name), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LiveNow] teardown: failed to revert {Id}", ch.Id);
                }
            }
        }

        _decorated.Clear();

        var owned = _store.Load();
        var remaining = new HashSet<UserChannel>(owned);
        foreach (var key in owned)
        {
            if (TryClearFavorite(key, ct))
            {
                remaining.Remove(key);
            }
        }

        _store.Save(remaining);
        _owned = remaining;
        if (remaining.Count > 0)
        {
            _logger.LogWarning("[LiveNow] teardown: {Count} owned favorite(s) could not be cleared — kept for retry", remaining.Count);
        }
    }

    /// <summary>
    /// Read IsFavorite for the given (user, channel) pairs. Returns a dict; a pair whose read
    /// throws is OMITTED (→ unknown → the planner skips it this cycle, never claims ownership).
    /// </summary>
    private Dictionary<UserChannel, bool> ReadFavoriteState(
        IReadOnlyList<User> users,
        IReadOnlyDictionary<string, BaseItem> channels)
    {
        var state = new Dictionary<UserChannel, bool>();
        foreach (var user in users)
        {
            foreach (var (cid, item) in channels)
            {
                try
                {
                    var data = _userDataManager.GetUserData(user, item);
                    if (data is not null)
                    {
                        state[new UserChannel(user.Id.ToString("N"), cid)] = data.IsFavorite;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LiveNow] fav read failed u={User} c={Channel}", user.Id, cid);
                }
            }
        }

        return state;
    }

    private bool TrySetFavorite(UserChannel key, BaseItem item, CancellationToken ct) =>
        TryWriteFavorite(key, item, favorite: true, ct);

    private bool TryClearFavorite(UserChannel key, CancellationToken ct)
    {
        // A clear is only CONFIRMED (→ drop from owned) when the channel resolves AND the write
        // succeeds (see FavoritePlan.ClearConfirmed). An unparseable/unresolvable channel — library
        // not loaded yet at startup, or mid-M3U-refresh — means the favorite may still be SET on
        // the server, so we keep it owned and retry, never orphaning a stuck plugin-set favorite.
        var channelResolved = Guid.TryParse(key.ChannelId, out var channelGuid);
        var item = channelResolved ? _libraryManager.GetItemById(channelGuid) : null;
        if (item is null)
        {
            return FavoritePlan.ClearConfirmed(channelResolved: false, userResolved: false, saveSucceeded: false);
        }

        return TryWriteFavorite(key, item, favorite: false, ct);
    }

    private bool TryWriteFavorite(UserChannel key, BaseItem item, bool favorite, CancellationToken ct)
    {
        try
        {
            if (!Guid.TryParse(key.UserId, out var userGuid))
            {
                return false;
            }

            var user = _userManager.GetUserById(userGuid);
            if (user is null)
            {
                return false;
            }

            var data = _userDataManager.GetUserData(user, item);
            if (data is null)
            {
                return false;
            }

            data.IsFavorite = favorite;
            _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LiveNow] favorite write failed u={User} c={Channel} fav={Fav}", key.UserId, key.ChannelId, favorite);
            return false;
        }
    }

    /// <summary>
    /// Sync favorites: float warm channels for every user (only where not already set) and remove
    /// plugin-owned favorites whose channel went cold. Updates + persists the owned-set based on
    /// CONFIRMED per-op success (durability on partial failure — a failed op is retried next cycle).
    /// </summary>
    /// <param name="warmIds">Currently-warm channel ids.</param>
    /// <param name="full">When true, re-evaluate ALL warm channels (heals new users / skipped reads).</param>
    /// <param name="ct">Cancellation token.</param>
    private void SyncFavorites(HashSet<string> warmIds, bool full, CancellationToken ct)
    {
        if (!Config.EnableFavoriteFloat)
        {
            return;
        }

        var newlyWarm = full ? new HashSet<string>(warmIds) : warmIds.Except(_prevWarm).ToHashSet();
        var coldOwned = _owned.Where(k => !warmIds.Contains(k.ChannelId)).Select(k => k.ChannelId).ToHashSet();
        if (newlyWarm.Count == 0 && coldOwned.Count == 0)
        {
            return; // nothing changed — cheap cycle
        }

        var users = _userManager.Users.ToList();
        var newOwned = new HashSet<UserChannel>(_owned);
        var nAdd = 0;
        var nRem = 0;

        // Resolve the channel BaseItems we need (adds for newly-warm, removes for cold-owned).
        var addChannels = ResolveChannels(newlyWarm);
        var removeChannels = ResolveChannels(coldOwned);

        // ADDS — the float. Pre-check each user's IsFavorite so we never touch their own favorite.
        if (addChannels.Count > 0)
        {
            var current = ReadFavoriteState(users, addChannels);
            var (adds, _) = Plan(
                warm: newlyWarm,
                users: users.Select(u => u.Id.ToString("N")),
                currentFavs: current,
                owned: _owned.Where(k => newlyWarm.Contains(k.ChannelId)).ToHashSet());

            foreach (var key in adds)
            {
                if (addChannels.TryGetValue(key.ChannelId, out var item) && TrySetFavorite(key, item, ct))
                {
                    newOwned.Add(key);
                    nAdd++;
                }
            }
        }

        // REMOVES — cold owned favorites. Only drop tracking on a CONFIRMED clear.
        if (removeChannels.Count > 0)
        {
            var current = ReadFavoriteState(users, removeChannels);
            var (_, removes) = Plan(
                warm: Array.Empty<string>(),
                users: users.Select(u => u.Id.ToString("N")),
                currentFavs: current,
                owned: _owned.Where(k => coldOwned.Contains(k.ChannelId)).ToHashSet());

            foreach (var key in removes)
            {
                if (TryClearFavorite(key, ct))
                {
                    newOwned.Remove(key);
                    nRem++;
                }
            }
        }

        if (nAdd > 0 || nRem > 0)
        {
            _owned = newOwned;
            _store.Save(_owned);
            _logger.LogInformation("[LiveNow] favorites: +{Add} / -{Rem} (owned now {Owned})", nAdd, nRem, _owned.Count);
        }
    }

    private Dictionary<string, BaseItem> ResolveChannels(IEnumerable<string> ids)
    {
        var map = new Dictionary<string, BaseItem>();
        foreach (var cid in ids)
        {
            if (Guid.TryParse(cid, out var guid))
            {
                var item = _libraryManager.GetItemById(guid);
                if (item is not null)
                {
                    map[cid] = item;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Run one poll cycle: decorate warm channels, revert cold ones, sync favorites.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncOnceAsync(CancellationToken ct)
    {
        var cfg = Config;
        var fullEvery = Math.Max(1, cfg.FullFavoritePassEvery);
        var full = _cycle % fullEvery == 0;
        _cycle++;

        var warm = GetWarmChannels();

        // 1) Decorate / update warm channels.
        foreach (var (cid, count) in warm)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Guid.TryParse(cid, out var guid))
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(guid);
                if (item is null)
                {
                    continue;
                }

                var currentName = item.Name ?? string.Empty;
                // Re-derive the original from the CURRENT name (stripped) so an upstream rename
                // while warm (EPG/M3U refresh writes a fresh, undecorated name) is picked up — the
                // cached original would otherwise be stale and we'd revert to the OLD name when cold.
                // Only trust the cache while the current name is still our own decoration.
                var strippedCurrent = NamePrefix.Strip(currentName);
                var cached = _decorated.GetValueOrDefault(cid);
                var original = (cached is not null && NamePrefix.IsDecorated(currentName))
                    ? cached
                    : strippedCurrent;
                var target = NamePrefix.Decorate(original, count);
                if (!string.Equals(currentName, target, StringComparison.Ordinal))
                {
                    await SetChannelNameAsync(item, target, ct).ConfigureAwait(false);
                    _logger.LogInformation("[LiveNow] warm {Original} -> {Target} ({Count} viewer(s))", original, target, count);
                }

                _decorated[cid] = original;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LiveNow] failed to decorate {Channel}", cid);
            }
        }

        // 2) Revert any decorated channel that is no longer warm (stateless scan + in-memory hint).
        var toCheck = new Dictionary<string, string>(_decorated);
        try
        {
            foreach (var ch in GetAllChannels())
            {
                if (NamePrefix.IsDecorated(ch.Name))
                {
                    var cid = ch.Id.ToString("N");
                    toCheck.TryAdd(cid, NamePrefix.Strip(ch.Name));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LiveNow] could not scan channels for stale decorations");
        }

        foreach (var (cid, original) in toCheck)
        {
            if (warm.ContainsKey(cid))
            {
                continue;
            }

            try
            {
                if (Guid.TryParse(cid, out var guid))
                {
                    var item = _libraryManager.GetItemById(guid);
                    if (item is not null && NamePrefix.IsDecorated(item.Name))
                    {
                        await SetChannelNameAsync(item, original, ct).ConfigureAwait(false);
                        _logger.LogInformation("[LiveNow] cold reverted -> {Original}", original);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LiveNow] failed to revert {Channel}", cid);
            }
            finally
            {
                _decorated.Remove(cid);
            }
        }

        // 3) Sync favorites (warm → favorite for all users; cold owned → unfavorite).
        var warmIds = warm.Keys.ToHashSet();
        SyncFavorites(warmIds, full, ct);
        _prevWarm = warmIds;
    }
}
