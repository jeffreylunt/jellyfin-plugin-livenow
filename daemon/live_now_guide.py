#!/usr/bin/env python3
"""
Live Now guide daemon — surfaces currently-watched ("warm") Live TV channels in the
NATIVE Jellyfin guide on every client (incl. the Google TV / Android TV app) by
decorating the channel's DISPLAY NAME with the live viewer count.

While someone is watching a channel, its Jellyfin name becomes:
    "🔥 3 · ESPN HD"
and reverts to the original ("ESPN HD") the moment it goes cold.

WHY display-name decoration (validated 2026-06-30):
  - The native Android TV app can't render web plugins, but it DOES render the channel
    NAME in the guide. Decorating the name is the only mechanism that shows the live
    count on the native app without per-user favorites pollution or disruptive renumber.
  - POST /Items/{id} with a modified Name renames a Live TV channel INSTANTLY (HTTP 204),
    reflected on all clients with NO guide refresh / EPG rebuild / re-scan.
  - Tuning is UNAFFECTED: the name lives in jellyfin.db BaseItems as a display value; the
    HDHR lineup (GuideName/URL/stream UUID) is untouched, and Jellyfin tunes via the lineup.
  - Channel ORDER is unchanged: SortName keeps its "0000N.0-" number prefix, so renaming
    does not reorder anything. Pure, reversible decoration.

Run ON the hp host (so curl/JSON never round-trips emoji through SSH args, which mangles
UTF-8). See systemd unit live-now-guide.service.

Safety:
  - Idempotent: strips any existing "🔥 N · " (or legacy "🔴 N · ") prefix before
    (re)applying, so the count updates cleanly and double-prefixing can't happen.
  - Crash-safe: on startup it strips stale prefixes off ALL channels (reconcile), so a
    crash mid-cycle never leaves a channel stuck showing a stale count.
  - Read-only fallback: if /Sessions can't be read, it does nothing this cycle (never
    blanks names on a transient API hiccup).
"""

import json
import os
import re
import sys
import time
import urllib.request
import urllib.error

# ---- config (env-overridable) ----
BASE = os.environ.get("JELLYFIN_URL", "http://localhost:8096/jellyfin").rstrip("/")
TOKEN = os.environ.get("JELLYFIN_TOKEN", "4dc796216cbe4ed99a1a0c4ed244afe2")
POLL_SECONDS = int(os.environ.get("POLL_SECONDS", "45"))
# Decoration: "🔥 {n} · {name}". The PREFIX_RE must round-trip whatever we write.
# Also matches the legacy 🔴 prefix so any older decoration is stripped/reverted cleanly.
PREFIX_RE = re.compile(r"^[\U0001F525\U0001F534] \d+ · ")  # "🔥 / 🔴 <num> · "
# A user we can pass for the /Items read (any valid user id works for metadata read).
USER_ID = os.environ.get("JELLYFIN_USER_ID", "727583f8f86b42b4ac500a28ddfe5f56")  # jeff

HEADERS = {
    "Authorization": f'MediaBrowser Token="{TOKEN}"',
    "Content-Type": "application/json",
    "Accept": "application/json",
}


def log(msg):
    print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}", flush=True)


def api(method, path, body=None):
    url = BASE + path
    data = None
    if body is not None:
        # ensure_ascii=True so the bytes we send are pure ASCII-escaped JSON (the server
        # decodes \uXXXX back to the emoji). Avoids any transport encoding ambiguity.
        data = json.dumps(body, ensure_ascii=True).encode("ascii")
    req = urllib.request.Request(url, data=data, headers=HEADERS, method=method)
    with urllib.request.urlopen(req, timeout=20) as r:
        raw = r.read()
        if not raw:
            return None
        return json.loads(raw.decode("utf-8"))


def strip_prefix(name):
    """Remove a leading '🔥 N · ' (or legacy '🔴 N · ') decoration if present."""
    return PREFIX_RE.sub("", name or "")


def decorate(name, n):
    return f"\U0001F525 {n} · {name}"  # 🔥


def get_warm_channels():
    """Return {channelId: viewerCount} for channels with active live-TV playback."""
    sessions = api("GET", "/Sessions")
    warm = {}
    for s in sessions or []:
        npi = s.get("NowPlayingItem")
        if not npi:
            continue
        if npi.get("Type") != "TvChannel":
            continue
        # For live TV the item IS the channel: item.Id is the channel id.
        cid = npi.get("Id")
        if not cid:
            continue
        warm[cid] = warm.get(cid, 0) + 1
    return warm


def get_channel_item(cid):
    return api("GET", f"/Items/{cid}?userId={USER_ID}")


def set_channel_name(item, new_name):
    """POST the full item back with Name changed. Returns True on success."""
    if item.get("Name") == new_name:
        return True  # already correct, no write
    item = dict(item)
    item["Name"] = new_name
    api("POST", f"/Items/{item['Id']}", item)
    return True


def reconcile_all():
    """On startup: strip any stale decoration left by a previous crash."""
    log("reconcile: scanning all live-tv channels for stale decorations")
    data = api("GET", f"/LiveTv/Channels?userId={USER_ID}&Limit=1000")
    items = (data or {}).get("Items", [])
    cleaned = 0
    for ch in items:
        name = ch.get("Name", "")
        if PREFIX_RE.match(name):
            full = get_channel_item(ch["Id"])
            if full:
                set_channel_name(full, strip_prefix(name))
                cleaned += 1
    log(f"reconcile: cleaned {cleaned} stale-decorated channel(s)")


def list_decorated_channels():
    """Scan ALL live-tv channels for ones currently carrying a '🔥 N · ' decoration.
    Returns {channelId: stripped_original_name}. This makes cold-revert STATELESS /
    self-healing — we never depend only on in-memory state to undo a decoration."""
    data = api("GET", f"/LiveTv/Channels?userId={USER_ID}&Limit=1000")
    out = {}
    for ch in (data or {}).get("Items", []):
        name = ch.get("Name", "")
        if PREFIX_RE.match(name):
            out[ch["Id"]] = strip_prefix(name)
    return out


def sync_once(decorated):
    """
    decorated: dict {channelId: original_name} we are currently decorating (in-memory hint).
    Returns the updated set.

    Cold-revert is STATELESS: we scan the live channel list for anything still decorated
    and revert any that aren't warm. So a daemon restart (empty `decorated`) still cleans
    up correctly, and a one-shot run reverts cold channels too.
    """
    try:
        warm = get_warm_channels()
    except Exception as e:
        log(f"WARN: could not read /Sessions ({e}); skipping cycle (no changes)")
        return decorated

    # 1) Apply / update decoration on warm channels.
    for cid, count in warm.items():
        try:
            full = get_channel_item(cid)
            if not full:
                continue
            current = full.get("Name", "")
            original = decorated.get(cid) or strip_prefix(current)
            target = decorate(original, count)
            if current != target:
                set_channel_name(full, target)
                log(f"warm  {original!r} -> {target!r} ({count} viewer(s))")
            decorated[cid] = original
        except Exception as e:
            log(f"WARN: failed to decorate {cid}: {e}")

    # 2) Revert any decorated channel that is no longer warm (stateless scan + in-memory).
    to_check = dict(decorated)
    try:
        for cid, original in list_decorated_channels().items():
            to_check.setdefault(cid, original)
    except Exception as e:
        log(f"WARN: could not scan channels for stale decorations: {e}")

    for cid, original in to_check.items():
        if cid in warm:
            continue
        try:
            full = get_channel_item(cid)
            if full and PREFIX_RE.match(full.get("Name", "")):
                set_channel_name(full, original)
                log(f"cold  reverted -> {original!r}")
        except Exception as e:
            log(f"WARN: failed to revert {cid}: {e}")
        finally:
            decorated.pop(cid, None)

    return decorated


def main():
    log(f"live-now-guide starting | base={BASE} poll={POLL_SECONDS}s")
    try:
        reconcile_all()
    except Exception as e:
        log(f"WARN: reconcile failed ({e}); continuing")
    decorated = {}
    while True:
        decorated = sync_once(decorated)
        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    # one-shot mode for testing: `live_now_guide.py once`
    if len(sys.argv) > 1 and sys.argv[1] == "once":
        log("one-shot sync")
        sync_once({})
    elif len(sys.argv) > 1 and sys.argv[1] == "reconcile":
        reconcile_all()
    else:
        main()
