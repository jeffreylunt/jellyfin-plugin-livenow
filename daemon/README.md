# Live Now guide daemon

Surfaces currently-watched ("warm") Live TV channels in the **native** Jellyfin guide on
every client — including the Google TV / Android TV app — by decorating the channel's
**display name** with the live viewer count:

```
ESPN HD   →   🔥x3 ESPN HD     (while 3 people are watching)
```

and reverting to the original name the moment the channel goes cold.

## Why decorate the name?

The native Android TV app can't render web plugins, but it **does** render the channel
name in the guide. Decorating the name is the only mechanism that shows the live count on
the native app **without**:

- polluting per-user Favorites (favorites are per-user; 23 users here), or
- disruptively renumbering channels.

Validated facts (see `knowledge/jellyfin-livetv-channel-rename-mechanism-2026-06-30.md`):

- `POST /Items/{id}` with a modified `Name` renames a Live TV channel **instantly** on all
  clients — no guide refresh, no EPG rebuild, no re-scan.
- **Tuning is unaffected**: the name is a display value in `jellyfin.db` `BaseItems`; the
  HDHR lineup (`GuideName`/`URL`/stream UUID) is untouched and Jellyfin tunes via the lineup.
- **Order is unchanged**: `SortName` keeps its `0000N.0-` channel-number prefix.

## How it works

1. Poll `GET /Sessions` every `POLL_SECONDS` (default 45).
2. A warm channel = a session whose `NowPlayingItem.Type == TvChannel`; the item's `Id` is
   the channel id (for live TV the item *is* the channel). Distinct sessions = viewer count.
3. For each warm channel set `Name = "🔥xN <original>"`. When cold, restore `<original>`.

Safety:

- **Idempotent** — strips any existing `🔥xN ` (or legacy `🔥 N · ` / `🔴 N · `) prefix before (re)applying.
- **Stateless cold-revert** — each cycle also scans the channel list for any decorated
  channel that isn't warm and reverts it, so a daemon restart never leaves a channel stuck.
- **Reconcile on startup** (and on `ExecStopPost`) — strips all stale decorations.
- **Fail-safe** — if `/Sessions` can't be read, the cycle is a no-op (never blanks names).

## Deploy (hp)

```
mkdir -p /home/jeff/live-now-guide
cp live_now_guide.py /home/jeff/live-now-guide/
cp live-now-guide.env.example /home/jeff/live-now-guide/live-now-guide.env   # edit token
chmod 600 /home/jeff/live-now-guide/live-now-guide.env
cp live-now-guide.service ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now live-now-guide.service
loginctl enable-linger jeff        # survive logout/reboot
```

Run it **on hp** (not over SSH args) so the emoji never round-trips through a shell
argument, which mangles the UTF-8.

### Manual modes

```
python3 live_now_guide.py once        # one sync cycle then exit
python3 live_now_guide.py reconcile   # strip all stale decorations then exit
```

### Logs

```
journalctl --user -u live-now-guide.service -f
```

## Config (env)

| var | default | meaning |
|---|---|---|
| `JELLYFIN_URL` | `http://localhost:8096/jellyfin` | server base (with base path) |
| `JELLYFIN_TOKEN` | — | API key, sent as `Authorization: MediaBrowser Token="..."` |
| `JELLYFIN_USER_ID` | jeff's id | any valid user id (used for the metadata read) |
| `POLL_SECONDS` | `45` | poll interval |

## Limitations

- Any client that lists channel names shows the decoration to everyone (it's a global
  display-name change, not per-user). That's the point — it surfaces warm channels to the
  whole household.
- It does **not** reorder/float channels to the top of the guide; it decorates names in
  place. (Float-to-top above Favorites is a separate, still-open design question.)
