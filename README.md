# Jellyfin "Live Now" Plugin

Surfaces which **Live TV channels are currently being watched** by someone on the
server ("warm" channels), so other household members can tune into an
already-open channel instead of opening a new upstream IPTV stream.

Jellyfin proxies one upstream IPTV stream to many local viewers, but each
*distinct* channel consumes one of only a few scarce upstream subscription slots.
This plugin nudges the household onto already-open channels.

![Live Now grid](screenshots/live-now-grid.png)

- **Target server:** Jellyfin 10.11.x (`targetAbi 10.11.0.0`, **.NET 9**)
- **Plugin GUID:** `b919b337-587b-4533-922e-c8f8c5c8c9b0`
- **Version:** 1.0.0.0

## What it does

1. Exposes an authenticated API endpoint:
   - `GET /LiveNow/Channels` → JSON array of warm channels:
     ```json
     [
       {
         "channelId": "ac1557e48ff5fa0b5623f724de6e21c1",
         "channelName": "CA: TSN 1 ᴿᴬᵂ",
         "currentProgramName": "Live: SportsCentre",
         "viewerCount": 1,
         "imageUrl": "Items/ac15.../Images/Primary?tag=..."
       }
     ]
     ```
     Reads active sessions via `ISessionManager`, keeps only live-TV sessions
     (`NowPlayingItem.Type == TvChannel`), groups by channel, counts viewers.
     Requires a normal authenticated Jellyfin user (reuses Jellyfin auth).
2. Ships a small, dependency-free **"Live Now" web page** (embedded in the DLL)
   that renders a mobile-friendly grid of warm channels with **tap-to-tune
   deep-links** into the Jellyfin web player
   (`/web/#/details?id=<channelId>&serverId=<serverId>`). Auto-refreshes every 15s.

## Install (manual)

1. Copy the plugin folder into your Jellyfin `plugins` directory:
   ```
   <config>/plugins/LiveNow_1.0.0/
     ├── Jellyfin.Plugin.LiveNow.dll
     └── meta.json
   ```
   (On this homelab: `/home/jeff/jellyfin/config/plugins/LiveNow_1.0.0/`.)
2. Restart Jellyfin (`docker restart jellyfin`). ⚠️ Live TV is family-critical —
   check `GET /Sessions` first and don't restart while someone is mid-playback.
3. Dashboard → Plugins should now list **Live Now**.

## Install (plugin repository — recommended)

In Jellyfin: **Dashboard → Plugins → Repositories → +** and add this repository
(name it "Live Now", URL):

```
https://raw.githubusercontent.com/jeffreylunt/jellyfin-plugin-livenow/main/manifest.json
```

Then open **Catalog**, find **Live Now** under *Live TV*, click **Install**, and
restart Jellyfin. The repo manifest points at the versioned ZIP attached to each
[GitHub Release](https://github.com/jeffreylunt/jellyfin-plugin-livenow/releases).

## Open the page

- From the dashboard plugin page (embedded), or directly by URL **while signed in
  to the Jellyfin web client**:
  `https://<server>/jellyfin/web/configurationpage?name=livenow`
- **Custom Tab:** add a Custom Tab in the Jellyfin web client pointing at that
  URL to get a persistent "Live Now" tab.
- The page uses your existing web-client session for auth — open it from inside
  the web client (it does not accept a token in the URL, to avoid leaking it).

## Tap to tune

Clicking a channel issues a `PlayNow` command to **your own** web session, so the
channel starts playing immediately in the client you're viewing from. If the page
is opened outside a web-client session, the card instead links to the channel's
detail page where you can press Play.

## Build from source

No local .NET needed — `./build.sh` builds in a .NET 9 SDK container and produces
the installable plugin folder + ZIP under `dist/`. Or build the DLL directly:

```bash
docker run --rm \
  -v "$PWD/src/Jellyfin.Plugin.LiveNow":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet build -c Release -o /src/bin/out
```

Output: `src/Jellyfin.Plugin.LiveNow/bin/out/Jellyfin.Plugin.LiveNow.dll`.

## Limitations (v1)

- A pure server plugin **cannot** inject a native row into the Live TV / Home
  screen that renders across all clients (Android TV / Roku / mobile apps). The
  "Live Now" page is a URL / Custom Tab reachable from any browser or the web
  client — not a native row.
- No "free upstream slots remaining" counter (that data is Dispatcharr-only and
  out of scope for v1). v1 shows only "this channel is warm + N watching."
- **Privacy:** any authenticated Jellyfin user can see which channels are being
  watched (and the viewer count) — it does not reveal *who* is watching, but it
  does surface household live-TV activity. Fine for a family server; worth noting
  for multi-tenant setups.
