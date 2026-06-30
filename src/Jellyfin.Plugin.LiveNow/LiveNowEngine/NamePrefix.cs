using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LiveNow.LiveNowEngine;

/// <summary>
/// The "🔥xN " channel-name decoration helpers, ported from the daemon. Idempotent: we always
/// strip any existing prefix (including the legacy "🔥 N · " / "🔴 N · " formats) before
/// (re)applying, so the count updates cleanly and double-prefixing can't happen.
/// </summary>
public static partial class NamePrefix
{
    // 🔥 (U+1F525 = surrogate pair D83D DD25) or legacy 🔴 (U+1F534 = D83D DD34), followed by
    // "x<num> " (new) OR " <num> · " (legacy). The source generator doesn't accept \U 8-digit
    // escapes, so we match the leading surrogate D83D then either low surrogate.
    [GeneratedRegex("^\\uD83D[\\uDD25\\uDD34](?:x\\d+ |\\s+\\d+ · )")]
    private static partial Regex PrefixRegex();

    /// <summary>
    /// Gets a value indicating whether the name currently carries a Live Now decoration.
    /// </summary>
    /// <param name="name">The channel name.</param>
    /// <returns>True if decorated.</returns>
    public static bool IsDecorated(string? name) =>
        !string.IsNullOrEmpty(name) && PrefixRegex().IsMatch(name);

    /// <summary>
    /// Remove a leading "🔥xN " (or legacy "🔥 N · " / "🔴 N · ") decoration if present.
    /// </summary>
    /// <param name="name">The channel name.</param>
    /// <returns>The undecorated name.</returns>
    public static string Strip(string? name) =>
        PrefixRegex().Replace(name ?? string.Empty, string.Empty);

    /// <summary>
    /// Build the decorated name, e.g. "🔥x3 ESPN HD".
    /// </summary>
    /// <param name="original">The original (stripped) name.</param>
    /// <param name="count">The live viewer count.</param>
    /// <returns>The decorated name.</returns>
    public static string Decorate(string original, int count) => $"\U0001F525x{count} {original}";
}
