using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.LiveNow.LiveNowEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.LiveNow.LiveNowEngine.FavoritePlan;

namespace Jellyfin.Plugin.LiveNow.Tests;

/// <summary>
/// The owned-favorites file must always reflect what's actually set on the server, even across
/// crashes and a corrupt file — otherwise plugin favorites orphan (set on server, untracked).
/// Ports the daemon's OwnedFavoritesStore tests.
/// </summary>
public class OwnedFavoritesStoreTests
{
    private static (OwnedFavoritesStore Store, string Path) NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "livenow-test-" + Path.GetRandomFileName() + ".json");
        return (new OwnedFavoritesStore(path, NullLogger.Instance), path);
    }

    [Fact]
    public void Empty_load_when_missing()
    {
        var (store, path) = NewStore();
        try
        {
            Assert.Empty(store.Load());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Round_trip()
    {
        var (store, path) = NewStore();
        try
        {
            var owned = new HashSet<UserChannel> { new("u1", "c1"), new("u2", "c1") };
            store.Save(owned);
            Assert.Equal(owned, store.Load());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Corrupt_file_is_empty()
    {
        var (store, path) = NewStore();
        try
        {
            File.WriteAllText(path, "{not json");
            Assert.Empty(store.Load());  // never crash on a bad state file
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Migrates_daemon_format()
    {
        // The Python daemon wrote a JSON array of [userId, channelId] pairs — same shape.
        var (store, path) = NewStore();
        try
        {
            File.WriteAllText(path, "[[\"u1\",\"c1\"],[\"u2\",\"c2\"]]");
            var loaded = store.Load();
            Assert.Equal(
                new HashSet<UserChannel> { new("u1", "c1"), new("u2", "c2") },
                loaded);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
