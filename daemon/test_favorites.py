#!/usr/bin/env python3
"""
Unit tests for the owned-favorites safety logic in live_now_guide.py.

The ONLY thing that must be perfect: the daemon must NEVER un-favorite a channel a user
favorited themselves. It may only remove favorites IT added (recorded in the owned-set).
These tests exercise that pure logic without touching the Jellyfin API.
"""
import os
import tempfile
import unittest

import live_now_guide as d


class OwnedFavoritesStore(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.NamedTemporaryFile(suffix=".json", delete=False)
        self.tmp.close()
        self.path = self.tmp.name
        d.OWNED_FAVORITES_FILE = self.path

    def tearDown(self):
        os.unlink(self.path)

    def test_empty_load(self):
        self.assertEqual(d.load_owned(), set())

    def test_round_trip(self):
        owned = {("u1", "c1"), ("u2", "c1")}
        d.save_owned(owned)
        self.assertEqual(d.load_owned(), owned)

    def test_corrupt_file_is_empty(self):
        with open(self.path, "w") as f:
            f.write("{not json")
        self.assertEqual(d.load_owned(), set())  # never crash on a bad state file


class FavoritePlan(unittest.TestCase):
    """plan_favorite_changes() decides per (user,channel) whether to add/remove/skip.
    It must respect: never touch a fav the user already had; only remove owned ones."""

    def test_add_for_user_without_it(self):
        # user u1 is NOT currently favoriting warm channel c1, and we don't own it yet
        adds, removes = d.plan_favorite_changes(
            warm={"c1"},
            users=["u1"],
            current_favs={("u1", "c1"): False},   # u1 does NOT have c1 favorited
            owned=set(),
        )
        self.assertIn(("u1", "c1"), adds)
        self.assertEqual(removes, set())

    def test_do_not_add_if_user_already_favorited(self):
        # u1 ALREADY favorites c1 (their own). We must NOT record it as owned, NOT re-add.
        adds, removes = d.plan_favorite_changes(
            warm={"c1"},
            users=["u1"],
            current_favs={("u1", "c1"): True},
            owned=set(),
        )
        self.assertEqual(adds, set())     # already favorited -> no write
        self.assertEqual(removes, set())  # and never marked owned

    def test_remove_only_owned_when_cold(self):
        # c1 went cold. u1's fav is OWNED (daemon added it) -> remove.
        adds, removes = d.plan_favorite_changes(
            warm=set(),                       # nothing warm
            users=["u1"],
            current_favs={("u1", "c1"): True},
            owned={("u1", "c1")},             # we added it
        )
        self.assertEqual(adds, set())
        self.assertIn(("u1", "c1"), removes)

    def test_never_remove_a_users_own_favorite(self):
        # c1 cold, u1 favorites c1 but it is NOT owned (their own) -> must NOT remove.
        adds, removes = d.plan_favorite_changes(
            warm=set(),
            users=["u1"],
            current_favs={("u1", "c1"): True},
            owned=set(),                      # NOT owned
        )
        self.assertEqual(removes, set())      # CRITICAL: leave the user's own favorite alone

    def test_keep_owned_favorite_while_still_warm(self):
        # c1 still warm, owned by daemon for u1 -> keep (no add since already set, no remove)
        adds, removes = d.plan_favorite_changes(
            warm={"c1"},
            users=["u1"],
            current_favs={("u1", "c1"): True},
            owned={("u1", "c1")},
        )
        self.assertEqual(adds, set())     # already set, no write
        self.assertEqual(removes, set())  # still warm, keep it

    def test_multi_user_multi_channel(self):
        # c1 warm, c2 cold(owned). users u1 (has c1 own), u2 (nothing).
        adds, removes = d.plan_favorite_changes(
            warm={"c1"},
            users=["u1", "u2"],
            current_favs={
                ("u1", "c1"): True,    # u1 own -> skip add, never own/remove
                ("u2", "c1"): False,   # u2 -> add (owned)
                ("u1", "c2"): True,    # owned cold -> remove
                ("u2", "c2"): True,    # owned cold -> remove
            },
            owned={("u1", "c2"), ("u2", "c2")},
        )
        self.assertEqual(adds, {("u2", "c1")})
        self.assertEqual(removes, {("u1", "c2"), ("u2", "c2")})

    def test_unknown_favorite_state_skips_add(self):
        # u1's state is UNKNOWN (read failed -> absent from current_favs). Must NOT add
        # (we'd risk claiming ownership of something we can't verify isn't theirs).
        adds, removes = d.plan_favorite_changes(
            warm={"c1"},
            users=["u1"],
            current_favs={},          # no reading for u1
            owned=set(),
        )
        self.assertEqual(adds, set())
        self.assertEqual(removes, set())

    def test_diff_only_no_redundant_writes(self):
        # Everything already in desired state -> zero writes (cheap cycle).
        adds, removes = d.plan_favorite_changes(
            warm={"c1"},
            users=["u1", "u2"],
            current_favs={("u1", "c1"): True, ("u2", "c1"): True},
            owned={("u1", "c1"), ("u2", "c1")},
        )
        self.assertEqual(adds, set())
        self.assertEqual(removes, set())


class Durability(unittest.TestCase):
    """The owned-favorites.json must always reflect what's actually set on the server, even
    across crashes and partial failures — otherwise daemon favorites orphan (set on server,
    untracked) and never get cleaned up."""

    def setUp(self):
        self.tmp = tempfile.NamedTemporaryFile(suffix=".json", delete=False)
        self.tmp.close()
        d.OWNED_FAVORITES_FILE = self.tmp.name
        d.save_owned(set())
        # stub the network + user list
        self._orig = {k: getattr(d, k) for k in
                      ("get_users", "get_favorite_state", "set_favorite", "clear_favorite",
                       "ENABLE_AUTO_FAVORITE")}
        d.ENABLE_AUTO_FAVORITE = True
        d.get_users = lambda: ["u1", "u2"]

    def tearDown(self):
        for k, v in self._orig.items():
            setattr(d, k, v)
        os.unlink(self.tmp.name)

    def test_add_is_persisted_before_next_call(self):
        # Simulate a crash: set_favorite succeeds for u1, then raises for u2 (process dies).
        # The owned file must already contain u1's add (persisted incrementally), so it's
        # never orphaned.
        d.get_favorite_state = lambda users, chans: {("u1", "c1"): False, ("u2", "c1"): False}
        calls = []
        def flaky_set(uid, cid):
            calls.append((uid, cid))
            if uid == "u2":
                raise RuntimeError("connection died")
        d.set_favorite = flaky_set
        d.clear_favorite = lambda uid, cid: None
        d.sync_favorites({"c1"}, set(), prev_warm=set())
        on_disk = d.load_owned()
        self.assertIn(("u1", "c1"), on_disk)      # u1's add survived the u2 crash
        self.assertNotIn(("u2", "c1"), on_disk)   # u2's failed add is NOT recorded

    def test_failed_clear_keeps_entry_owned(self):
        # c1 went cold; clear fails for u2 -> u2 must STAY owned so it's retried (not orphaned).
        d.save_owned({("u1", "c1"), ("u2", "c1")})
        d.get_favorite_state = lambda users, chans: {("u1", "c1"): True, ("u2", "c1"): True}
        def flaky_clear(uid, cid):
            if uid == "u2":
                raise RuntimeError("delete failed")
        d.clear_favorite = flaky_clear
        d.set_favorite = lambda uid, cid: None
        # c1 is cold (warm set empty); both are owned -> try to remove both
        owned = d.sync_favorites(set(), {("u1", "c1"), ("u2", "c1")}, prev_warm={"c1"})
        self.assertNotIn(("u1", "c1"), owned)     # cleared -> dropped
        self.assertIn(("u2", "c1"), owned)        # failed clear -> KEPT for retry
        self.assertEqual(d.load_owned(), {("u2", "c1")})  # persisted

    def test_teardown_retains_failed_deletes(self):
        d.save_owned({("u1", "c1"), ("u2", "c1")})
        def flaky_clear(uid, cid):
            if uid == "u2":
                raise RuntimeError("delete failed")
        d.clear_favorite = flaky_clear
        d.remove_all_owned_favorites()
        self.assertEqual(d.load_owned(), {("u2", "c1")})  # u1 cleared; u2 kept (not wiped)

    def test_full_pass_catches_new_user_on_already_warm_channel(self):
        # c1 already warm last cycle (in prev_warm). A new user u2 has no favorite. Delta path
        # would skip c1 entirely; the FULL pass must add it for u2.
        d.get_favorite_state = lambda users, chans: {("u1", "c1"): True, ("u2", "c1"): False}
        added = []
        d.set_favorite = lambda uid, cid: added.append((uid, cid))
        d.clear_favorite = lambda uid, cid: None
        # delta: prev_warm already has c1 -> no work
        d.sync_favorites({"c1"}, {("u1", "c1")}, prev_warm={"c1"}, full=False)
        self.assertEqual(added, [])               # delta misses u2
        # full: re-evaluates c1 -> adds u2
        d.sync_favorites({"c1"}, {("u1", "c1")}, prev_warm={"c1"}, full=True)
        self.assertIn(("u2", "c1"), added)        # full pass catches the new user


class FastPath(unittest.TestCase):
    """The newly-warm channel's favorites (the float) must be applied even if the cold-remove
    read fails — the add path must not be blocked by a slow/failing remove path."""

    def setUp(self):
        self.tmp = tempfile.NamedTemporaryFile(suffix=".json", delete=False)
        self.tmp.close()
        d.OWNED_FAVORITES_FILE = self.tmp.name
        d.save_owned(set())
        self._orig = {k: getattr(d, k) for k in
                      ("get_users", "get_favorite_state", "set_favorite", "clear_favorite",
                       "ENABLE_AUTO_FAVORITE")}
        d.ENABLE_AUTO_FAVORITE = True
        d.get_users = lambda: ["u1"]

    def tearDown(self):
        for k, v in self._orig.items():
            setattr(d, k, v)
        os.unlink(self.tmp.name)

    def test_add_applied_even_if_remove_read_fails(self):
        # c_new is newly warm; c_old is cold+owned. The remove-phase read raises, but the
        # add for c_new must still go through (the float isn't blocked by the remove path).
        added = []
        d.set_favorite = lambda uid, cid: added.append((uid, cid))
        d.clear_favorite = lambda uid, cid: None
        def fav_state(users, chans):
            chans = set(chans)
            if "c_old" in chans:      # the remove-phase read
                raise RuntimeError("remove read failed")
            return {("u1", "c_new"): False}   # the add-phase read
        d.get_favorite_state = fav_state
        owned = d.sync_favorites(
            warm_ids={"c_new"}, owned={("u1", "c_old")}, prev_warm=set())
        self.assertIn(("u1", "c_new"), added)          # add went through
        self.assertIn(("u1", "c_new"), owned)          # tracked
        self.assertIn(("u1", "c_old"), owned)          # remove skipped this cycle (retried later)


if __name__ == "__main__":
    unittest.main()
