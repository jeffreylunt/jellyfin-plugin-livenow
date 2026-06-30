using System.Collections.Generic;
using Jellyfin.Plugin.LiveNow.LiveNowEngine;
using Xunit;
using static Jellyfin.Plugin.LiveNow.LiveNowEngine.FavoritePlan;

namespace Jellyfin.Plugin.LiveNow.Tests;

/// <summary>
/// Ports the daemon's plan_favorite_changes safety tests to xUnit. The ONE thing that must be
/// perfect: never un-favorite a channel a user favorited themselves; only remove favorites we own.
/// </summary>
public class FavoritePlanTests
{
    private static UserChannel UC(string u, string c) => new(u, c);

    private static (HashSet<UserChannel> Adds, HashSet<UserChannel> Removes) Run(
        IEnumerable<string> warm,
        IEnumerable<string> users,
        Dictionary<UserChannel, bool> current,
        HashSet<UserChannel> owned) => Plan(warm, users, current, owned);

    [Fact]
    public void Add_for_user_without_it()
    {
        var (adds, removes) = Run(
            warm: new[] { "c1" },
            users: new[] { "u1" },
            current: new() { [UC("u1", "c1")] = false },
            owned: new());
        Assert.Contains(UC("u1", "c1"), adds);
        Assert.Empty(removes);
    }

    [Fact]
    public void Do_not_add_if_user_already_favorited()
    {
        var (adds, removes) = Run(
            warm: new[] { "c1" },
            users: new[] { "u1" },
            current: new() { [UC("u1", "c1")] = true },
            owned: new());
        Assert.Empty(adds);     // already favorited → no write
        Assert.Empty(removes);  // and never marked owned
    }

    [Fact]
    public void Remove_only_owned_when_cold()
    {
        var (adds, removes) = Run(
            warm: System.Array.Empty<string>(),
            users: new[] { "u1" },
            current: new() { [UC("u1", "c1")] = true },
            owned: new() { UC("u1", "c1") });
        Assert.Empty(adds);
        Assert.Contains(UC("u1", "c1"), removes);
    }

    [Fact]
    public void Never_remove_a_users_own_favorite()
    {
        var (_, removes) = Run(
            warm: System.Array.Empty<string>(),
            users: new[] { "u1" },
            current: new() { [UC("u1", "c1")] = true },
            owned: new());  // NOT owned
        Assert.Empty(removes);  // CRITICAL: leave the user's own favorite alone
    }

    [Fact]
    public void Keep_owned_favorite_while_still_warm()
    {
        var (adds, removes) = Run(
            warm: new[] { "c1" },
            users: new[] { "u1" },
            current: new() { [UC("u1", "c1")] = true },
            owned: new() { UC("u1", "c1") });
        Assert.Empty(adds);     // already set
        Assert.Empty(removes);  // still warm → keep
    }

    [Fact]
    public void Multi_user_multi_channel()
    {
        var (adds, removes) = Run(
            warm: new[] { "c1" },
            users: new[] { "u1", "u2" },
            current: new()
            {
                [UC("u1", "c1")] = true,   // u1 own → skip add, never own/remove
                [UC("u2", "c1")] = false,  // u2 → add (owned)
                [UC("u1", "c2")] = true,   // owned cold → remove
                [UC("u2", "c2")] = true,   // owned cold → remove
            },
            owned: new() { UC("u1", "c2"), UC("u2", "c2") });
        Assert.Equal(new HashSet<UserChannel> { UC("u2", "c1") }, adds);
        Assert.Equal(new HashSet<UserChannel> { UC("u1", "c2"), UC("u2", "c2") }, removes);
    }

    [Fact]
    public void Unknown_favorite_state_skips_add()
    {
        var (adds, removes) = Run(
            warm: new[] { "c1" },
            users: new[] { "u1" },
            current: new(),  // u1 state unknown (read failed)
            owned: new());
        Assert.Empty(adds);
        Assert.Empty(removes);
    }

    [Fact]
    public void Diff_only_no_redundant_writes()
    {
        var (adds, removes) = Run(
            warm: new[] { "c1" },
            users: new[] { "u1", "u2" },
            current: new() { [UC("u1", "c1")] = true, [UC("u2", "c1")] = true },
            owned: new() { UC("u1", "c1"), UC("u2", "c1") });
        Assert.Empty(adds);
        Assert.Empty(removes);
    }

    [Fact]
    public void Startup_reconcile_clears_owned_favorites_for_not_warm_channels()
    {
        // Models the engine's startup ReconcileOwnedFavorites: given the CURRENT warm set and the
        // FULL owned set, every owned favorite whose channel isn't warm is selected for removal (so
        // a skipped teardown self-heals on restart), while an owned fav still warm is kept.
        var (adds, removes) = Run(
            warm: new[] { "cWarm" },
            users: new[] { "u1", "u2" },
            current: new()
            {
                [UC("u1", "cWarm")] = true,
                [UC("u2", "cWarm")] = true,
                [UC("u1", "cCold")] = true,
                [UC("u2", "cCold")] = true,
            },
            owned: new() { UC("u1", "cWarm"), UC("u2", "cWarm"), UC("u1", "cCold"), UC("u2", "cCold") });
        Assert.Empty(adds); // everything already favorited
        Assert.Equal(new HashSet<UserChannel> { UC("u1", "cCold"), UC("u2", "cCold") }, removes);
    }

    [Theory]
    // channelResolved, userResolved, saveSucceeded -> may we DROP the owned entry?
    [InlineData(true, true, true, true)]      // confirmed clear -> drop
    [InlineData(false, false, false, false)]  // channel unresolvable -> KEEP owned (the #1 orphan path)
    [InlineData(true, false, false, false)]   // user unresolvable -> keep
    [InlineData(true, true, false, false)]    // SaveUserData threw -> keep, retry next cycle
    public void ClearConfirmed_only_drops_owned_on_a_fully_confirmed_clear(
        bool channelResolved, bool userResolved, bool saveSucceeded, bool expectedDrop)
    {
        Assert.Equal(expectedDrop, FavoritePlan.ClearConfirmed(channelResolved, userResolved, saveSucceeded));
    }
}
