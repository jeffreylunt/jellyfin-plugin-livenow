#!/usr/bin/env python3
"""
Live Now guide daemon — surfaces currently-watched ("warm") Live TV channels in the
NATIVE Jellyfin guide on every client (incl. the Google TV / Android TV app) by
decorating the channel's DISPLAY NAME with the live viewer count.

While someone is watching a channel, its Jellyfin name becomes:
    "🔥x3 ESPN HD"
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
  - Idempotent: strips any existing "🔥xN " (or legacy "🔥 N · " / "🔴 N · ") prefix
    before (re)applying, so the count updates cleanly and double-prefixing can't happen.
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
from concurrent.futures import ThreadPoolExecutor, as_completed

# ---- config (env-overridable) ----
BASE = os.environ.get("JELLYFIN_URL", "http://localhost:8096/jellyfin").rstrip("/")
TOKEN = os.environ.get("JELLYFIN_TOKEN", "4dc796216cbe4ed99a1a0c4ed244afe2")
POLL_SECONDS = int(os.environ.get("POLL_SECONDS", "45"))
# Decoration: "🔥x{n} {name}" (fire + lowercase x + count + space). PREFIX_RE must round-trip
# whatever we write, AND also match the older "🔥 N · " / "🔴 N · " formats so any existing
# decoration is stripped/reverted cleanly across a format change.
PREFIX_RE = re.compile(
    r"^[\U0001F525\U0001F534]"      # 🔥 or legacy 🔴
    r"(?:x\d+ |\s+\d+ · )"          # new "x<num> " OR legacy " <num> · "
)
# A user we can pass for the /Items read (any valid user id works for metadata read).
USER_ID = os.environ.get("JELLYFIN_USER_ID", "727583f8f86b42b4ac500a28ddfe5f56")  # jeff

# Auto-favorite warm channels for ALL users so they float to the top of the native TV
# guide's favorites block (the only top-of-screen lever — the guide is number-sorted).
# OFF by default; enable via ENABLE_AUTO_FAVORITE=true.
ENABLE_AUTO_FAVORITE = os.environ.get("ENABLE_AUTO_FAVORITE", "false").lower() == "true"
# Persistent record of every (userId, channelId) favorite the DAEMON added. We only ever
# remove favorites listed here — NEVER a favorite a user set themselves.
OWNED_FAVORITES_FILE = os.environ.get(
    "OWNED_FAVORITES_FILE", "/home/jeff/live-now-guide/owned-favorites.json"
)

HEADERS = {
    "Authorization": f'MediaBrowser Token="{TOKEN}"',
    "Content-Type": "application/json",
    "Accept": "application/json",
}


# ---------------------------------------------------------------------------
# Owned-favorites state (persistent) + the pure add/remove decision logic.
# The decision logic is intentionally side-effect-free so it can be unit-tested
# (test_favorites.py) — this is the safety-critical "never delete a user's own
# favorite" guarantee.
# ---------------------------------------------------------------------------
def load_owned():
    """Load the set of (userId, channelId) favorites the daemon added. Never crashes."""
    try:
        with open(OWNED_FAVORITES_FILE, "r") as f:
            return set(tuple(pair) for pair in json.load(f))
    except (FileNotFoundError, ValueError, json.JSONDecodeError):
        return set()
    except Exception:
        return set()


def save_owned(owned):
    tmp = OWNED_FAVORITES_FILE + ".tmp"
    with open(tmp, "w") as f:
        json.dump(sorted(list(t) for t in owned), f)
    os.replace(tmp, OWNED_FAVORITES_FILE)


def plan_favorite_changes(warm, users, current_favs, owned):
    """
    Pure decision: given the warm channel set, the user list, the current favorite state
    {(user,channel): bool}, and the set of daemon-owned favorites, return (adds, removes)
    of (userId, channelId) pairs to write.

    Rules (safety-critical):
      - ADD a (user, warm-channel) favorite only if the user does NOT already have it.
        (If they already favorited it themselves, leave it; do not mark it owned.)
      - REMOVE a (user, channel) favorite ONLY if it is in `owned` AND the channel is no
        longer warm. NEVER remove a favorite that isn't daemon-owned.
    """
    warm = set(warm)
    adds = set()
    removes = set()

    # Adds: warm channels, for every user we CONFIRMED does not already have them favorited.
    # If a user's favorite state is UNKNOWN (read failed -> key absent), we skip them this
    # cycle — we never claim ownership we're unsure of (avoids deleting a real favorite later).
    for cid in warm:
        for uid in users:
            key = (uid, cid)
            if key not in current_favs:
                continue  # unknown -> skip (safe)
            if current_favs[key] is False and key not in owned:
                adds.add(key)

    # Removes: only daemon-owned favorites whose channel is no longer warm.
    for (uid, cid) in owned:
        if cid not in warm:
            removes.add((uid, cid))

    return adds, removes


def log(msg):
    print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}", flush=True)


def api(method, path, body=None, timeout=20, retries=1):
    """One API call. Retries once on transient connection errors (the server occasionally
    drops connections under the favorite-read burst — a single retry clears it)."""
    url = BASE + path
    data = None
    if body is not None:
        # ensure_ascii=True so the bytes we send are pure ASCII-escaped JSON (the server
        # decodes \uXXXX back to the emoji). Avoids any transport encoding ambiguity.
        data = json.dumps(body, ensure_ascii=True).encode("ascii")
    last = None
    for attempt in range(retries + 1):
        try:
            req = urllib.request.Request(url, data=data, headers=HEADERS, method=method)
            with urllib.request.urlopen(req, timeout=timeout) as r:
                raw = r.read()
                return json.loads(raw.decode("utf-8")) if raw else None
        except (urllib.error.URLError, ConnectionError, OSError) as e:
            last = e
            if attempt < retries:
                time.sleep(0.5)
    raise last


def strip_prefix(name):
    """Remove a leading '🔥 N · ' (or legacy '🔴 N · ') decoration if present."""
    return PREFIX_RE.sub("", name or "")


def decorate(name, n):
    return f"\U0001F525x{n} {name}"  # 🔥x<n> <name>, e.g. "🔥x3 ESPN HD"


def get_users():
    """All user ids on the server (fetched fresh each cycle so new users are covered)."""
    users = api("GET", "/Users")
    return [u["Id"] for u in (users or []) if u.get("Id")]


FAV_READ_TIMEOUT = int(os.environ.get("FAV_READ_TIMEOUT", "40"))
# Gentle concurrency: enough to beat the per-call latency across ~24 users without
# overwhelming the Jellyfin server (which drops connections under a heavy read burst).
FAV_READ_WORKERS = int(os.environ.get("FAV_READ_WORKERS", "4"))


def _user_fav_for(uid, ids_csv, want):
    """One user's IsFavorite for the channels-of-interest. Targeted /Items?Ids= query
    (only the few channels we care about — far cheaper than the full live-tv channel list)."""
    out = {}
    data = api("GET", f"/Items?userId={uid}&Ids={ids_csv}&Fields=", timeout=FAV_READ_TIMEOUT)
    for ch in (data or {}).get("Items", []):
        cid = ch.get("Id")
        if cid in want:
            out[(uid, cid)] = bool(ch.get("UserData", {}).get("IsFavorite"))
    return out


def get_favorite_state(users, channel_ids):
    """Return {(userId, channelId): bool} IsFavorite for users x channels-of-interest.
    Runs the per-user reads CONCURRENTLY (the API is slow per call under load; 24 sequential
    reads blow the timeout, but in parallel they finish in ~one slow-call's time)."""
    want = set(channel_ids)
    ids_csv = ",".join(want)
    state = {}
    with ThreadPoolExecutor(max_workers=FAV_READ_WORKERS) as ex:
        futs = {ex.submit(_user_fav_for, uid, ids_csv, want): uid for uid in users}
        for fut in as_completed(futs):
            uid = futs[fut]
            try:
                state.update(fut.result())
            except Exception as e:
                # If we can't read a user's state, treat unknown as "assume already favorited"
                # → we will NOT add (so we never claim ownership we're unsure of) and NOT
                # remove (only owned removes). Safe-by-omission for that user this cycle.
                log(f"WARN: fav read failed for user {uid[:8]}: {e}")
    return state


def set_favorite(uid, cid):
    api("POST", f"/UserFavoriteItems/{cid}?userId={uid}")


def clear_favorite(uid, cid):
    api("DELETE", f"/UserFavoriteItems/{cid}?userId={uid}")


def sync_favorites(warm_ids, owned, prev_warm, full=False):
    """
    Set IsFavorite=true for warm channels across ALL users (only where not already set),
    and remove daemon-OWNED favorites whose channel went cold. Returns updated owned set.
    Safe: never removes a favorite a user set themselves (see plan_favorite_changes).

    full=True (the DEFAULT every cycle — see main()): re-evaluate ALL currently-warm channels
    against the CURRENT user set, so a NEW user, a dropped favorite-read, or a failed write
    self-heals next cycle. plan_favorite_changes() skips already-set favorites, so steady state
    issues ZERO redundant WRITES — only the per-cycle reads. full=False is the cheaper
    delta-only path (newly-warm only) kept for the rare case read load must be trimmed; it does
    NOT revisit an already-warm channel, so new users/dropped reads would be missed under it.
    """
    if not ENABLE_AUTO_FAVORITE:
        return owned

    warm_ids = set(warm_ids)
    # Channels we must (re)evaluate this cycle:
    #  - newly warm (need to add favorites) — or ALL warm channels on a full pass
    #  - newly cold but owned (need to remove favorites)
    newly_warm = warm_ids if full else (warm_ids - prev_warm)
    cold_owned = {cid for (_u, cid) in owned if cid not in warm_ids}
    if not (newly_warm | cold_owned):
        return owned  # nothing changed — no per-user queries, cheap cycle

    try:
        users = get_users()
    except Exception as e:
        log(f"WARN: could not read /Users ({e}); skipping favorite sync this cycle")
        return owned

    new_owned = set(owned)
    n_add = n_rem = 0

    # FAST-PATH: do the ADDS for newly-warm channels FIRST (this is what floats the
    # just-tuned channel to the top). Its read covers only the newly-warm channels, so it
    # isn't delayed by the (potentially larger) cold-remove read. Removes can lag a cycle —
    # a cold channel lingering one extra cycle is harmless.
    if newly_warm:
        try:
            current = get_favorite_state(users, newly_warm)
            adds, _ = plan_favorite_changes(
                warm=warm_ids & newly_warm, users=users,
                current_favs=current,
                owned={(u, c) for (u, c) in owned if c in newly_warm},
            )
            # Issue the add-writes CONCURRENTLY (same gentle pool as the reads) so the
            # just-tuned channel floats within ~one cycle instead of waiting for ~24
            # sequential POSTs. Record owned + persist only for confirmed sets.
            def _do_add(pair):
                set_favorite(*pair)
                return pair
            with ThreadPoolExecutor(max_workers=FAV_READ_WORKERS) as ex:
                futs = {ex.submit(_do_add, p): p for p in adds}
                for fut in as_completed(futs):
                    p = futs[fut]
                    try:
                        fut.result()
                        new_owned.add(p)
                        # Persist as each add confirms (main thread only — save_owned is not
                        # called from worker threads, so the file write stays single-writer).
                        save_owned(new_owned)
                        n_add += 1
                    except Exception as e:
                        log(f"WARN: set_favorite failed u={p[0][:8]} c={p[1][:8]}: {e}")
            if n_add:
                log(f"favorites: floated +{n_add} (newly-warm), owned now {len(new_owned)}")
        except Exception as e:
            log(f"WARN: could not read favorite state for adds ({e}); skipping adds")

    # REMOVES: unfavorite daemon-owned favorites whose channel went cold. Can lag.
    removes = set()
    if cold_owned:
        try:
            current = get_favorite_state(users, cold_owned)
            _, removes = plan_favorite_changes(
                warm=set(), users=users, current_favs=current,
                owned={(u, c) for (u, c) in owned if c in cold_owned},
            )
        except Exception as e:
            log(f"WARN: could not read favorite state for removes ({e}); skipping removes")
    for (uid, cid) in removes:
        try:
            clear_favorite(uid, cid)
            # Only stop tracking on a CONFIRMED clear. If the DELETE failed the favorite is
            # still set on the server, so we keep it owned → the next cycle retries the
            # removal (never orphan a daemon favorite on a transient failure).
            new_owned.discard((uid, cid))
            save_owned(new_owned)
            n_rem += 1
        except Exception as e:
            log(f"WARN: clear_favorite failed u={uid[:8]} c={cid[:8]}: {e}")

    if n_add or n_rem:
        log(f"favorites: +{n_add} / -{n_rem} (owned now {len(new_owned)})")
        save_owned(new_owned)
    return new_owned


def remove_all_owned_favorites():
    """Graceful-stop / teardown: delete every daemon-owned favorite. RETAINS any entry whose
    DELETE failed (don't wipe state on a transient failure — that would orphan the favorite
    on the server with no record of it; the next teardown/cycle retries those)."""
    owned = load_owned()
    if not owned:
        return
    log(f"favorites: removing all {len(owned)} daemon-owned favorite(s)")
    remaining = set(owned)
    for (uid, cid) in list(owned):
        try:
            clear_favorite(uid, cid)
            remaining.discard((uid, cid))
        except Exception as e:
            log(f"WARN: clear_favorite failed u={uid[:8]} c={cid[:8]}: {e} (kept owned)")
    save_owned(remaining)
    if remaining:
        log(f"favorites: {len(remaining)} entr(ies) could not be cleared — kept for retry")


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


def sync_once(decorated, owned=None, prev_warm=None, full_favorites=False):
    """
    decorated: dict {channelId: original_name} we are currently decorating (in-memory hint).
    owned: set of (userId, channelId) favorites the daemon added (loaded from disk if None).
    prev_warm: warm channel-id set from the previous cycle (for cheap favorite diffing).
    full_favorites: re-evaluate favorites for ALL warm channels (not just the delta) — used
      periodically to catch new users / users skipped by a transient failure.
    Returns (decorated, owned, warm_ids).

    Cold-revert is STATELESS for NAMES: we scan the live channel list for anything still
    decorated and revert any that aren't warm. Favorites are tracked via the persistent
    owned-set (favorites aren't self-identifying like the 🔥 name prefix is).
    """
    if owned is None:
        owned = load_owned()
    if prev_warm is None:
        prev_warm = set()
    try:
        warm = get_warm_channels()
    except Exception as e:
        log(f"WARN: could not read /Sessions ({e}); skipping cycle (no changes)")
        return decorated, owned, prev_warm

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

    # 3) Sync favorites across all users (warm -> favorite; cold owned -> unfavorite).
    #    Delta-only by default (cheap); periodic full pass heals new users / skipped reads.
    warm_ids = set(warm.keys())
    owned = sync_favorites(warm_ids, owned, prev_warm, full=full_favorites)

    return decorated, owned, warm_ids


def main():
    log(f"live-now-guide starting | base={BASE} poll={POLL_SECONDS}s "
        f"auto_favorite={ENABLE_AUTO_FAVORITE}")
    try:
        reconcile_all()
    except Exception as e:
        log(f"WARN: reconcile failed ({e}); continuing")
    decorated = {}
    owned = load_owned()
    prev_warm = set()
    # RELIABILITY: the per-cycle DELTA path favorites a just-tuned channel for all CURRENT
    # users in ~1 cycle (the common case — effectively instant). Every FULL_FAVORITE_EVERY
    # cycles we ALSO do a FULL pass: re-evaluate ALL currently-warm channels against the CURRENT
    # user set, so the rare edges self-heal — a NEW user account created mid-watch, a transient
    # favorite-read failure, a failed set_favorite, or an external un-favorite all get re-added
    # on the next full pass. plan_favorite_changes() skips already-set favorites, so the full
    # pass issues ZERO redundant WRITES — only reads. Default every 10 cycles (~7.5 min at 45s
    # poll) keeps the per-cycle read load gentle (respects the connection-drop concern).
    full_every = int(os.environ.get("FULL_FAVORITE_EVERY", "10"))
    cycle = 0
    while True:
        full = (cycle % full_every == 0)
        decorated, owned, prev_warm = sync_once(decorated, owned, prev_warm, full_favorites=full)
        cycle += 1
        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    # one-shot mode for testing: `live_now_guide.py once`
    if len(sys.argv) > 1 and sys.argv[1] == "once":
        log("one-shot sync")
        sync_once({})
    elif len(sys.argv) > 1 and sys.argv[1] == "reconcile":
        # Reconcile NAMES (strip stale decorations). Owned favorites are reconciled
        # automatically on the next sync (removes are stateless via the owned-set).
        reconcile_all()
    elif len(sys.argv) > 1 and sys.argv[1] == "teardown":
        # Graceful stop: strip all name decorations + remove ALL daemon-owned favorites.
        log("teardown: reverting all names + removing all daemon-owned favorites")
        reconcile_all()
        remove_all_owned_favorites()
    else:
        main()
