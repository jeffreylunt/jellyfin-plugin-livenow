# Live Now plugin — fully self-contained, ONE-INSTALL (no external daemon)

## Goal (Jeff, 2026-06-30 pivot)
A separate user installs ONLY this plugin and everything works: warm Live TV channels
float to the top of every user's native guide (favorites) + get a "🔥xN" name badge,
instantly, reverting when cold. No external Python daemon, no File Transformation plugin,
no manual config, no hardcoded paths/users/hosts.

## Approach
Move the ENTIRE `daemon/live_now_guide.py` loop INTO the plugin as an in-process background
service. Favorite + name writes happen in-process via Jellyfin APIs, so the float is instant
by construction (no HTTP fan-out across ~24 users).

### Architecture (verified against the 10.11 ABI via reflection)
- **Background loop**: register an `IHostedService` (a `BackgroundService`) via
  `IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)`
  (`MediaBrowser.Controller.Plugins.IPluginServiceRegistrator`). The hosted service owns the
  poll loop (default 45s) with full interval control — cleaner than `IScheduledTask`, whose
  `ExecuteAsync` is a one-shot-per-trigger and whose scheduler granularity isn't sub-minute.
  ALSO expose an `IScheduledTask` ("Live Now: sync now") for a manual one-shot run + a
  graceful "revert all" teardown hook.
- **Warm channels**: `ISessionManager.Sessions` → `NowPlayingItem.Type == TvChannel`,
  item.Id = channel id (ChannelId/ChannelName are null for live TV — confirmed).
- **Name decoration (in-process)**: resolve the channel `BaseItem` via
  `ILibraryManager.GetItemById(Guid)`, set `item.Name`, persist with
  `ILibraryManager.UpdateItemAsync(item, item.GetParent()?? , ItemUpdateType.MetadataEdit, ct)`.
  (Mirror the daemon's POST /Items rename, in-process. VALIDATE this reflects live on clients
  during the milestone-b deploy — if UpdateItemAsync doesn't push to clients the way the HTTP
  rename did, fall back to the exact server path the HTTP route uses.)
- **Favorites (in-process, INSTANT)**: `IUserManager.Users` (entity
  `Jellyfin.Database.Implementations.Entities.User`, has Id + Username) ×
  `IUserDataManager.GetUserData(user, item)` → `IsFavorite = x` →
  `SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, ct)`. SaveUserData
  fires the user-data-changed event → favorite goes live, no restart. Mirrors Jellyfin's own
  `UserLibraryController.MarkFavoriteItem`.
- **Owned-favorites persistence**: JSON in the plugin data dir
  (`Plugin.Instance.DataFolderPath` / `IApplicationPaths` plugin path), NOT /home/jeff —
  portable per install.
- **Web tab/page: DROPPED (Jeff, 2026-06-30).** The 🔥-name + favorite-float already surface
  warm channels at the top of the native guide on EVERY client, so the web page was redundant.
  Removing it eliminates the only caveat (read-only-Docker web dir): no index.html injection,
  no File Transformation dependency, no AllowAnonymous script controller, no custom API surface.
  Verified: Jeff's official jellyfin/jellyfin:10.11.8 has /jellyfin/jellyfin-web READ-ONLY for
  uid 1000 — which is exactly why the on-disk inject couldn't be self-contained. On deploy,
  ALSO remove the existing File Transformation inject from Jeff's box so his web UI returns to
  clean [Home | Favorites].
- **Config page**: native Jellyfin dashboard config page (IHasWebPages, livenowconfig.html) —
  enable/disable, favorite-float toggle, poll seconds (45), full-pass cadence (10), page refresh.
  Zero-config defaults so a fresh install just works. NO web-client injection.

### Safety + reliability contract (PORT EXACTLY — proven, non-negotiable)
1. Owned-favorites tracking — only ever clear a favorite the plugin added.
2. Pre-check — never record/remove a user's OWN favorite (read IsFavorite first; unknown → skip).
3. Reconcile-on-startup — strip stale "🔥" name decorations left by a crash.
4. Graceful-stop revert — on shutdown/disable strip all decorations + remove all owned favs.
5. Incremental persistence + partial-failure durability — record an add as owned only on
   confirmed success; drop an owned fav only on confirmed clear; a failure leaves state
   unchanged so the next cycle retries (never orphan, never drop tracking of a still-set fav).
6. Periodic FULL re-evaluation of ALL warm channels (not just delta) — heals new-user-mid-warm
   + transient-read misses. Default every 10 cycles.
7. The pure decision fn (`PlanFavoriteChanges`, port of `plan_favorite_changes`) stays
   side-effect-free + unit-tested (port the 17 Python tests to xUnit).

## Deploy discipline + sequence (Jeff's box)
- Build DLL in the .NET 9 SDK Docker container (no local dotnet). Jellyfin 10.11 = net9.0.
- **Before ANY Jellyfin restart: check `GET /Sessions` for 0 NowPlayingItem sessions.** If
  anyone is watching → HOLD + tell team-lead. (Family-critical live TV.)
- The external daemon runs as a **USER** systemd unit on hp: `systemctl --user`
  `live-now-guide.service` (active; PID was 2715216). NOT a system unit (system-scope
  `is-active` misleadingly says inactive). Stop it with `systemctl --user stop live-now-guide`
  (+ disable) — its graceful teardown reverts any decorations + clears its owned favorites
  (currently owned=[], nothing decorated → clean).
- Existing FT inject to remove: `POST /Plugins/5e87cc92571a4d8d8d98d2d4147f9f90/Configuration`
  with `{"DebugLoggingState":"Disabled","Transformations":[]}` → web UI back to [Home|Favorites].

Deploy order: (1) re-check 0 viewers → (2) `systemctl --user stop live-now-guide` (daemon
teardown reverts cleanly) → (3) clear FT transform → (4) swap plugin dir
`Live Now_1.0.0.0` → `Live Now_2.0.0.0` (DLL+meta.json) → (5) `docker restart jellyfin` →
(6) verify health + the loop's startup log + a warm-channel float E2E. Keep the daemon's
files in place (disabled) as a fallback until E2E passes; only then leave it retired.

## Definition of done
- One plugin install → warm channel floats INSTANTLY + 🔥 badge across all users, reverts cold.
- Safety holds: a non-jeff user's own favorite is never touched; cold → clean unfavorite.
- Survives a Jellyfin restart (reconcile strips stale; owned-favs reload from plugin data dir).
- Native dashboard config page renders. NO web tab, NO File Transformation dependency, zero-config.
- C# unit tests green. No hardcoded jeff/host/path specifics.
- Version-bumped, release DLL/zip pushed to the public repo with "install this one plugin"
  instructions. Knowledge updated. External daemon retired + FT inject removed from Jeff's box.

## Status (2026-06-30, end of session — HOLDING)
- DONE: v2.0.0.0 in-process plugin built; 23 xUnit tests green; both engine code reviews clean
  (fixes #1/#3/I-1/I-2/M-1/#7 applied); committed on branch live-now-guide-daemon
  (32307f3 code, d67a726 README). NOT pushed (publish gated).
- DEPLOYED + VERIFIED on Jeff's live Jellyfin (earlier 0-viewer window): loaded clean, 🔥 badge
  in ~3s (in-process, vs daemon ~2min), favorite-float worked 22/23 users, cold-revert cleared
  all 22 plugin-added favs + reverted name, non-jeff user's OWN favorite SURVIVED. Daemon
  stopped+disabled (user systemd unit), FT inject cleared.
- HOLD (team-lead): TV in use (stevens watching). A clean-E2E that fired pre-hold was stopped +
  the live system fully restored (0 decorated, 0 stale favs). v2 installed + idle (harmless).
- REMAINING (all gated on team-lead all-clear, viewers gone):
  1. Clean-conditions E2E for exact float-latency numbers (first-float / all-23-users-float).
  2. git tag v2.0.0 + push branch + create GitHub Release with live-now_2.0.0.0.zip (manifest
     already points at it w/ checksum 517afe0fa1ae0921291bc4bf8fdbb35b).
  3. Leave daemon retired (files remain as fallback) once E2E re-confirms no regression.
- ABI reflection done: IHostedService via IPluginServiceRegistrator, IUserDataManager
  (GetUserData/SaveUserData UpdateUserRating), IUserManager.Users (Jellyfin.Database...User),
  ILibraryManager.GetItemById/GetItemList/UpdateToRepositoryAsync, ISessionManager.
