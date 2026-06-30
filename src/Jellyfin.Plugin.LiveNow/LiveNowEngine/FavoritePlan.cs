using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.LiveNow.LiveNowEngine;

/// <summary>
/// Pure, side-effect-free favorite decision logic — the safety-critical core ported verbatim
/// from the daemon's <c>plan_favorite_changes</c> (so it can be unit-tested without touching
/// any Jellyfin API). The ONE invariant that must be perfect: the plugin must NEVER un-favorite
/// a channel a user favorited themselves. It may only remove favorites IT added (tracked in the
/// owned-set).
/// </summary>
public static class FavoritePlan
{
    /// <summary>
    /// A (userId, channelId) pair. Value semantics so it works as a set/dictionary key.
    /// </summary>
    /// <param name="UserId">The user id.</param>
    /// <param name="ChannelId">The channel id.</param>
    public readonly record struct UserChannel(string UserId, string ChannelId);

    /// <summary>
    /// Given the warm channel set, the user list, the current favorite state, and the set of
    /// plugin-owned favorites, decide which (user, channel) favorites to add and which to remove.
    ///
    /// Rules (safety-critical, identical to the daemon):
    ///   - ADD a (user, warm-channel) favorite only if the user does NOT already have it. If a
    ///     user's favorite state is UNKNOWN (read failed → key absent from <paramref name="currentFavs"/>)
    ///     we SKIP them this cycle — we never claim ownership we're unsure of (which could later
    ///     delete a real favorite). If they already favorited it themselves, we leave it and do
    ///     NOT mark it owned.
    ///   - REMOVE a (user, channel) favorite ONLY if it is in <paramref name="owned"/> AND the
    ///     channel is no longer warm. NEVER remove a favorite that isn't plugin-owned.
    /// </summary>
    /// <param name="warm">The set of currently-warm channel ids.</param>
    /// <param name="users">The user ids to evaluate.</param>
    /// <param name="currentFavs">Known IsFavorite state keyed by (user, channel). Absent key = unknown.</param>
    /// <param name="owned">The set of (user, channel) favorites the plugin added.</param>
    /// <returns>The adds and removes to apply.</returns>
    public static (HashSet<UserChannel> Adds, HashSet<UserChannel> Removes) Plan(
        IEnumerable<string> warm,
        IEnumerable<string> users,
        IReadOnlyDictionary<UserChannel, bool> currentFavs,
        IReadOnlySet<UserChannel> owned)
    {
        var warmSet = warm as ISet<string> ?? new HashSet<string>(warm);
        var userList = users as ICollection<string> ?? users.ToList();

        var adds = new HashSet<UserChannel>();
        var removes = new HashSet<UserChannel>();

        // Adds: warm channels, for every user we CONFIRMED does not already have them favorited.
        foreach (var cid in warmSet)
        {
            foreach (var uid in userList)
            {
                var key = new UserChannel(uid, cid);
                if (!currentFavs.TryGetValue(key, out var isFav))
                {
                    continue; // unknown → skip (safe)
                }

                if (!isFav && !owned.Contains(key))
                {
                    adds.Add(key);
                }
            }
        }

        // Removes: only plugin-owned favorites whose channel is no longer warm.
        foreach (var key in owned)
        {
            if (!warmSet.Contains(key.ChannelId))
            {
                removes.Add(key);
            }
        }

        return (adds, removes);
    }

    /// <summary>
    /// Durability rule for a clear operation: a plugin-owned favorite may be dropped from the
    /// owned-set ONLY when the clear is CONFIRMED — i.e. the channel + user resolved AND
    /// SaveUserData(false) succeeded. If the channel is currently unresolvable (library not loaded
    /// at startup, or mid-M3U-refresh) the favorite may still be SET on the server, so we keep it
    /// owned and retry next cycle (never orphan a stuck plugin-set favorite). This is the pure form
    /// of the engine's TryClearFavorite contract so it can be unit-tested.
    /// </summary>
    /// <param name="channelResolved">Whether the channel BaseItem resolved.</param>
    /// <param name="userResolved">Whether the user resolved.</param>
    /// <param name="saveSucceeded">Whether SaveUserData(false) completed without throwing.</param>
    /// <returns>True only if the owned entry may be dropped (a confirmed clear).</returns>
    public static bool ClearConfirmed(bool channelResolved, bool userResolved, bool saveSucceeded) =>
        channelResolved && userResolved && saveSucceeded;
}
