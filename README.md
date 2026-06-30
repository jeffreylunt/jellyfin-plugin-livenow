# Jellyfin "Live Now" Plugin

Floats the **Live TV channels someone is already watching** ("warm" channels) to the
top of every user's native guide and badges them with a live viewer count, so the
household tunes into an already-open channel instead of opening a new upstream IPTV
stream.

Jellyfin proxies one upstream IPTV stream to many local viewers, but each *distinct*
channel consumes one of only a few scarce upstream subscription slots. This plugin
nudges everyone onto already-open channels — automatically, on every client.

- **Target server:** Jellyfin 10.11.x (`targetAbi 10.11.0.0`, **.NET 9**)
- **Plugin GUID:** `b919b337-587b-4533-922e-c8f8c5c8c9b0`
- **Version:** 2.0.0.0

## Recommended setup — Dispatcharr in PROXY mode

For this plugin to actually save upstream connections, your IPTV source should be
running in **proxy mode** (e.g. [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr)
configured as a proxy that Jellyfin pulls from).

- **Proxy mode (recommended):** one upstream IPTV stream is shared to multiple
  clients. When someone is already watching a channel, others can join the **same**
  live stream **without opening a new upstream connection** (or burning another
  provider subscription slot). This is exactly the "join what's already live"
  behavior the plugin is built to nudge people toward.
- **Redirect mode (defeats the purpose):** each viewer is redirected to the provider
  and opens their **own** upstream connection — so "joining" a warm channel still
  consumes a slot, and a few viewers can exhaust your provider's connection limit.

In short: the plugin surfaces *which channels are warm*; **proxy mode is what makes
joining them free.** Without it, the plugin still works but the slot-saving benefit
is largely lost.

## What it does

While someone is watching a Live TV channel, the plugin:

1. **Badges the channel name** with a live viewer count, e.g. `🔥x3 ESPN HD`, reverting
   to `ESPN HD` the instant it goes cold. The native Android TV / Google TV app renders
   the channel name in the guide, so this shows up **on every client** — no web UI needed.
2. **Floats the channel to every user's favorites**, so it rises to the top of the
   native guide's favorites block. Favorites are set **in-process** via Jellyfin's own
   `IUserDataManager`, so they appear **instantly and live** — no Jellyfin restart.

Everything runs **inside Jellyfin** as a background service. There is **no external
daemon**, no companion plugin, and no configuration required — install it and it works.

### Self-contained — one install, zero config

- The whole loop (poll active sessions → decorate names → set/clear favorites) runs
  in-process via a background service registered through `IPluginServiceRegistrator`.
- Favorite writes use `IUserDataManager.SaveUserData(...UpdateUserRating...)`, which fires
  the user-data-changed event so the guide reflects the change with no restart.
- No web-client injection, no File Transformation plugin, no `index.html` edits — so it
  works on a vanilla (read-only-web) Docker install with no caveats.

## Safety

The one guarantee that must be perfect: **the plugin never un-favorites a channel a user
favorited themselves.** It tracks exactly which `(user, channel)` favorites *it* added (a
small JSON file in the plugin's data folder) and only ever clears those. Concretely:

- **Pre-check:** before floating a channel for a user, it reads their current `IsFavorite`;
  if they already favorited it (or the read fails), it leaves it alone and does not claim it.
- **Owned-only removal:** when a channel goes cold it only clears favorites it added.
- **Durability:** an add is recorded only on a confirmed write; an owned favorite is dropped
  only on a confirmed clear. A transient failure (or a channel that's momentarily
  unresolvable during an M3U refresh) leaves tracking intact and retries next cycle — it
  never orphans a favorite it set, and never drops tracking of one still set.
- **Reconcile on startup:** strips any stale `🔥` badge AND clears any owned favorite whose
  channel isn't currently warm, so the float self-heals across restarts/crashes.
- **Graceful revert on stop:** reverts all badges and removes all owned favorites.

Name decoration is idempotent (it strips any existing prefix before re-applying) and the
cold-revert is stateless (it scans the channel list for stale badges), so a crash can't
leave a channel stuck decorated. Renaming a channel does **not** affect tuning — the badge
is a display-name change only; the HDHR lineup / stream is untouched.

## Configuration (optional)

Dashboard → Plugins → **Live Now** opens a native settings page. Sensible defaults mean you
never have to touch it:

| Setting | Default | What it does |
|---|---|---|
| Enable Live Now | on | Master switch for the background loop. |
| Float warm channels to favorites | on | Set favorites for all users (off = badge only). |
| Poll interval (seconds) | 45 | How often active playback is re-checked. |
| Full re-evaluation every (cycles) | 10 | Periodic full pass that catches new users / transient misses. |
| Live Now page refresh (seconds) | 15 | Reserved for future use. |

## Install

### Plugin repository (recommended)

In Jellyfin: **Dashboard → Plugins → Repositories → +** and add:

```
https://raw.githubusercontent.com/jeffreylunt/jellyfin-plugin-livenow/main/manifest.json
```

Then **Catalog → Live Now → Install** (under *Live TV*) and restart Jellyfin. That's it —
no other plugins, no config.

### Manual

1. Copy the plugin folder into your Jellyfin `plugins` directory:
   ```
   <config>/plugins/Live Now_2.0.0.0/
     ├── Jellyfin.Plugin.LiveNow.dll
     └── meta.json
   ```
2. Restart Jellyfin. ⚠️ Live TV is family-critical — check `GET /Sessions` first and don't
   restart while someone is mid-playback.

## Build from source

No local .NET needed — `./build.sh` builds in a .NET 9 SDK container and produces the
installable folder + ZIP under `dist/`. Run the tests:

```bash
docker run --rm \
  -v "$PWD/tests/Jellyfin.Plugin.LiveNow.Tests":/test -v "$PWD/src":/src -w /test \
  mcr.microsoft.com/dotnet/sdk:9.0 \
  bash -c "rm -rf /test/bin /test/obj && dotnet test -c Release"
```

## Notes

- **Privacy:** the float/badge surfaces that a channel is being watched (and by how many) —
  it does not reveal *who*. Fine for a family server; worth noting for multi-tenant setups.
  Turn off "Float warm channels to favorites" to keep only the badge.
- The badge shows on clients that render the guide channel name (incl. the native Android
  TV / Google TV app). It is a reversible display-name decoration; tuning and channel order
  are unaffected (SortName keeps its number prefix).
- **How it works under the hood:** active sessions are read via `ISessionManager`; for a
  live-TV session the `NowPlayingItem` *is* the channel (`item.Id` = channel id). Names are
  updated via `BaseItem.UpdateToRepositoryAsync`, favorites via `IUserDataManager`.
