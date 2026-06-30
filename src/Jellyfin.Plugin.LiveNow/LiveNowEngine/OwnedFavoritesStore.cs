using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Jellyfin.Plugin.LiveNow.LiveNowEngine.FavoritePlan;

namespace Jellyfin.Plugin.LiveNow.LiveNowEngine;

/// <summary>
/// Persistent record of every (userId, channelId) favorite the plugin added. We only ever
/// remove favorites listed here — NEVER a favorite a user set themselves. Stored as JSON in the
/// plugin's own data folder (portable per install — no hardcoded path). Never crashes on a bad
/// state file (treats it as empty).
///
/// Serialized form: a JSON array of two-element [userId, channelId] arrays (matches the daemon's
/// owned-favorites.json shape so an existing file migrates cleanly).
/// </summary>
public sealed class OwnedFavoritesStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OwnedFavoritesStore"/> class.
    /// </summary>
    /// <param name="path">Absolute path to the owned-favorites JSON file.</param>
    /// <param name="logger">Logger.</param>
    public OwnedFavoritesStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>
    /// Load the set of (userId, channelId) favorites the plugin added. Never throws.
    /// </summary>
    /// <returns>The owned set (empty if the file is missing or corrupt).</returns>
    public HashSet<UserChannel> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new HashSet<UserChannel>();
                }

                var json = File.ReadAllText(_path);
                var pairs = JsonSerializer.Deserialize<List<string[]>>(json);
                var set = new HashSet<UserChannel>();
                if (pairs is not null)
                {
                    foreach (var p in pairs)
                    {
                        if (p is { Length: 2 } && p[0] is not null && p[1] is not null)
                        {
                            set.Add(new UserChannel(p[0], p[1]));
                        }
                    }
                }

                return set;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LiveNow] owned-favorites file unreadable; treating as empty");
                return new HashSet<UserChannel>();
            }
        }
    }

    /// <summary>
    /// Persist the owned set atomically (write tmp + rename). Single-writer via the lock.
    /// </summary>
    /// <param name="owned">The owned set to persist.</param>
    public void Save(IReadOnlySet<UserChannel> owned)
    {
        lock (_lock)
        {
            var pairs = owned
                .Select(t => new[] { t.UserId, t.ChannelId })
                .OrderBy(a => a[0], StringComparer.Ordinal)
                .ThenBy(a => a[1], StringComparer.Ordinal)
                .ToList();
            var json = JsonSerializer.Serialize(pairs);
            var tmp = _path + ".tmp";
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
