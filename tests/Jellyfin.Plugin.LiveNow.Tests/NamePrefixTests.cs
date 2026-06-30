using Jellyfin.Plugin.LiveNow.LiveNowEngine;
using Xunit;

namespace Jellyfin.Plugin.LiveNow.Tests;

/// <summary>
/// The "🔥xN " decoration must be idempotent: strip/re-apply must round-trip, and the strip must
/// also remove the legacy "🔥 N · " / "🔴 N · " formats so a format change reverts cleanly.
/// </summary>
public class NamePrefixTests
{
    [Fact]
    public void Decorate_then_strip_round_trips()
    {
        var decorated = NamePrefix.Decorate("ESPN HD", 3);
        Assert.Equal("\U0001F525x3 ESPN HD", decorated);
        Assert.True(NamePrefix.IsDecorated(decorated));
        Assert.Equal("ESPN HD", NamePrefix.Strip(decorated));
    }

    [Fact]
    public void Strip_is_idempotent_on_plain_name()
    {
        Assert.False(NamePrefix.IsDecorated("ESPN HD"));
        Assert.Equal("ESPN HD", NamePrefix.Strip("ESPN HD"));
    }

    [Fact]
    public void Re_decorate_does_not_double_prefix()
    {
        var once = NamePrefix.Decorate("ESPN HD", 1);
        // Simulate the engine: strip whatever is there, then decorate with the new count.
        var twice = NamePrefix.Decorate(NamePrefix.Strip(once), 5);
        Assert.Equal("\U0001F525x5 ESPN HD", twice);
    }

    [Theory]
    [InlineData("\U0001F525 2 · ESPN HD")]   // legacy "🔥 N · "
    [InlineData("\U0001F534 2 · ESPN HD")]   // legacy "🔴 N · "
    public void Strip_removes_legacy_formats(string legacy)
    {
        Assert.True(NamePrefix.IsDecorated(legacy));
        Assert.Equal("ESPN HD", NamePrefix.Strip(legacy));
    }

    [Fact]
    public void Strip_handles_null_and_empty()
    {
        Assert.Equal(string.Empty, NamePrefix.Strip(null));
        Assert.Equal(string.Empty, NamePrefix.Strip(string.Empty));
        Assert.False(NamePrefix.IsDecorated(null));
    }
}
