# Live Now web tab (working) — File Transformation inject

Adds a working **"Live Now"** tab to the Jellyfin **web** client top bar (next to
Home / Favorites). Clicking it shows the warm-channel page
(`/web/configurationpage?name=livenow`) in a full-area overlay.

## Why this instead of the Custom Tabs plugin's own tab

The **Custom Tabs** plugin (0.2.10) on Jellyfin **10.11.8** injects only the tab BUTTON,
never a content PANE — clicking its tab shows a blank area (10.11.8's React-rendered home
tabs don't receive the plugin's pane injection; the pane is also hidden by Jellyfin's
`.pageTabContent { display:none !important }`). So Custom Tabs' own tab config is left
**empty**, and this self-contained inject does the whole job: button + a fixed overlay it
shows/hides itself (with `!important` styles, immune to Jellyfin's tab CSS).

`livenow-tab-inject.js` is the script. It is injected into `web/index.html` by the
**File Transformation** plugin.

## How it's wired (verified working 2026-06-30)

- File Transformation config (`POST /Plugins/{FT-guid}/Configuration`) holds one
  transformation:
  - `Id`: `11ee0000-1abe-4a0e-9001-11feed000001` (MUST be a GUID — a non-GUID Id → HTTP 500)
  - `FilenamePattern`: `index.html`
  - `SearchText`: `</body></html>`
  - `ReplaceText`: `<script defer> … livenow-tab-inject.js … </script></body></html>`
- FT applies it **on the fly** — no Jellyfin restart needed when you POST the config.
- FT guid here: `5e87cc92571a4d8d8d98d2d4147f9f90`. Custom Tabs guid (config cleared):
  `fbacd0b6fd464a05b0a42045d6a135b0`.

Re-apply / update the inject:
```
# build config JSON from the script + POST it (see register.sh)
./register.sh
```

## To REMOVE (revert cleanly)

Set the FT config back to no transformations (and optionally re-point Custom Tabs):
```
curl -s -X POST "$JF/Plugins/5e87cc92571a4d8d8d98d2d4147f9f90/Configuration" \
  -H 'Authorization: MediaBrowser Token="<key>"' -H 'Content-Type: application/json' \
  -d '{"DebugLoggingState":"Disabled","Transformations":[]}'
```
That's the only change to the server — nothing is written to the web files on disk (FT
transforms the response in memory). Removing the transformation fully reverts the web UI.
If a future Custom Tabs version fixes the 10.11.8 pane bug, drop this inject and use the
plugin's own tab config instead.

## Notes

- The overlay is `position:fixed; top:7.2em; bottom:0` (below the header). Lazy-loads the
  iframe on first open. Idempotent (guarded by `window.__liveNowTab`).
- Verified: tab appears [Home | Favorites | Live Now]; clicking Live Now renders the page
  (showed the live "FOOD NETWORK HD … 🔥 1 watching" demo card); clicking Home hides it.
  Screenshot: ~/.superbot2/uploads/livenow-web-tab-working.png.
