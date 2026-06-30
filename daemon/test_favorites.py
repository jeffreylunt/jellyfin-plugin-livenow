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


if __name__ == "__main__":
    unittest.main()
