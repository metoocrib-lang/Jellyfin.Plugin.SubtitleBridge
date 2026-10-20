"""Offline checks for current Disney+ browse/entity URLs and safe episode matching."""
import unittest

from extractor.episode_discovery import match_episode
from extractor.hls import SubtitleError, allowed_disney_series_url
from extractor.server import valid_job


BLEACH = "https://www.disneyplus.com/es-419/browse/entity-571475d5-8b1a-453f-a57f-26bb4dd13304"


class FakeSeasonButton:
    def __init__(self, page):
        self.page = page
        self.first = self

    def count(self):
        return int(self.page.selected_season is not None)

    def inner_text(self):
        if self.page.selected_season is None:
            raise AssertionError("No season button")
        return f"Temporada {self.page.selected_season}"

    def click(self, **kwargs):
        self.page.dropdown_open = True


class FakeOption:
    def __init__(self, page, label):
        self.page = page
        self.label = label
        self.last = self

    def count(self):
        return int(self.page.dropdown_open and self.label in
                   [f"Temporada {s}" for s in self.page.seasons])

    def click(self, **kwargs):
        self.page.dropdown_open = False
        if not self.page.ignore_selection:
            self.page.selected_season = int(self.label.split()[-1])


class FakePage:
    def __init__(self, anchors, redirect=None, *, seasons=None, selected=None, ignore_selection=False):
        self.anchors = anchors
        self.redirect = redirect
        self.url = None
        self.seasons = seasons or {}
        self.selected_season = selected
        self.ignore_selection = ignore_selection
        self.dropdown_open = False
        self.selected_any = False

    def goto(self, url, **kwargs):
        self.url = self.redirect or url

    def get_by_role(self, role, name=None):
        if role != "button":
            raise AssertionError("Unexpected role")
        return FakeSeasonButton(self)

    def get_by_text(self, label, exact=False):
        return FakeOption(self, label)

    def wait_for_timeout(self, ms):
        pass

    def locator(self, selector):
        if selector != "a[href]":
            raise AssertionError("Unexpected selector")
        return self

    def evaluate_all(self, script):
        if self.selected_season is not None:
            return self.seasons.get(self.selected_season, [])
        return self.anchors


class DisneySeriesUrlTests(unittest.TestCase):
    def test_localized_browse_entity_and_old_series_url(self):
        self.assertTrue(allowed_disney_series_url(BLEACH))
        self.assertTrue(allowed_disney_series_url(
            "https://disneyplus.com/browse/entity-571475d5-8b1a-453f-a57f-26bb4dd13304"))
        self.assertTrue(allowed_disney_series_url("https://www.disneyplus.com/series/bleach/abc123"))

    def test_rejects_unknown_hosts_unsafe_or_unrelated_paths(self):
        bad = (
            BLEACH.replace("www.disneyplus.com", "www.disneyplus.com.evil.org"),
            BLEACH.replace("https:", "http:"),
            BLEACH.replace("/browse/entity-", "/play/entity-"),
            BLEACH.replace("571475d5-8b1a-453f-a57f-26bb4dd13304", "not-a-uuid"),
            BLEACH + "?next=https://evil.org/",
            "https://www.disneyplus.com/es-419/login",
            "https://www.disneyplus.com:444/es-419/browse/entity-571475d5-8b1a-453f-a57f-26bb4dd13304",
        )
        for url in bad:
            with self.subTest(url=url):
                self.assertFalse(allowed_disney_series_url(url))

    def test_agent_accepts_episode_job_with_entity_url(self):
        job = dict(provider="disney", series_url=BLEACH,
                   series_id=17, episode_id=91, season=1, episode=1, language="es")
        self.assertTrue(valid_job(job))
        job["series_url"] = "https://www.disneyplus.com/play/episode"
        self.assertFalse(valid_job(job))

    def test_localized_episode_label_and_play_link_without_season_dropdown(self):
        page = FakePage([
            {"href": "/es-419/play/episode-a", "label": "T1:E1 THE BLOOD WARFARE"},
            {"href": "/es-419/play/episode-b", "label": "T1:E2 FOUNDATION STONES"},
        ])
        self.assertEqual(match_episode(page, BLEACH, 1, 1),
                         "https://www.disneyplus.com/es-419/play/episode-a")

    def test_switches_season_and_identifies_bleach_numbered_episode(self):
        page = FakePage([], selected=4, seasons={
            4: [{"href": "/play/s4e1", "label": "1. EPISODIO DE TEMPORADA 4"}],
            1: [
                {"href": "/es-419/play/resume", "label": "CONTINUAR"},
                {"href": "/es-419/play/other", "label": ""},
                {"href": "/es-419/play/bleach-01", "label":
                 "1. LA GUERRA DE SANGRE La Sociedad de Almas observa un aumento ... (26 min)"},
                {"href": "/es-419/play/bleach-02", "label": "2. LAS PIEDRAS FUNDAMENTALES (26 min)"},
                {"href": "/es-419/play/bleach-03", "label": "3. LA MARCHA DE LAS CRUCES"},
            ]
        })
        self.assertEqual(match_episode(page, BLEACH, 1, 1),
                         "https://www.disneyplus.com/es-419/play/bleach-01")
        self.assertEqual(page.selected_season, 1)

    def test_waits_for_disney_season_ui_to_hydrate(self):
        class DelayedSeasonPage(FakePage):
            def __init__(self):
                super().__init__([], seasons={
                    4: [{"href": "/play/season4-1", "label": "1. OTRO EPISODIO"}],
                    1: [
                        {"href": "/es-419/play/bleach-01", "label": "1. LA GUERRA DE SANGRE"},
                        {"href": "/es-419/play/bleach-02", "label": "2. LAS PIEDRAS ANGULARES"},
                    ],
                })
                self.waited_for_hydration = False

            def wait_for_timeout(self, ms):
                if ms >= 5000 and self.selected_season is None:
                    self.waited_for_hydration = True
                    self.selected_season = 4

        page = DelayedSeasonPage()
        self.assertEqual(match_episode(page, BLEACH, 1, 1),
                         "https://www.disneyplus.com/es-419/play/bleach-01")
        self.assertTrue(page.waited_for_hydration)
        self.assertEqual(page.selected_season, 1)

    def test_rejects_number_without_verified_season(self):
        page = FakePage([
            {"href": "/play/e1", "label": "1. LA GUERRA DE SANGRE"},
            {"href": "/play/e2", "label": "2. LAS PIEDRAS FUNDAMENTALES"},
        ])
        with self.assertRaisesRegex(SubtitleError, "inequívoca"):
            match_episode(page, BLEACH, 1, 1)

    def test_rejects_unavailable_season(self):
        page = FakePage([], selected=4, seasons={4: []})
        with self.assertRaisesRegex(SubtitleError, "temporada solicitada"):
            match_episode(page, BLEACH, 1, 1)

    def test_rejects_season_that_did_not_change(self):
        page = FakePage([], selected=4, seasons={1: [
            {"href": "/play/e1", "label": "1. LA GUERRA DE SANGRE"},
            {"href": "/play/e2", "label": "2. LAS PIEDRAS FUNDAMENTALES"},
        ]}, ignore_selection=True)
        with self.assertRaisesRegex(SubtitleError, "no confirmó"):
            match_episode(page, BLEACH, 1, 1)

    def test_duplicate_numbered_episode_refuses_to_guess(self):
        page = FakePage([], selected=1, seasons={1: [
            {"href": "/play/sub", "label": "1. LA GUERRA DE SANGRE"},
            {"href": "/play/dub", "label": "1. LA GUERRA DE SANGRE"},
            {"href": "/play/e2", "label": "2. SIGUIENTE"},
        ]})
        with self.assertRaisesRegex(SubtitleError, "inequívoca"):
            match_episode(page, BLEACH, 1, 1)

    def test_numbered_episode_without_neighbor_is_not_enough(self):
        page = FakePage([], selected=1, seasons={1: [
            {"href": "/play/unverified", "label": "1. ALGUNA RECOMENDACIÓN"},
        ]})
        with self.assertRaisesRegex(SubtitleError, "inequívoca"):
            match_episode(page, BLEACH, 1, 1)

    def test_ambiguous_sub_and_dub_does_not_guess(self):
        page = FakePage([
            {"href": "/play/sub", "label": "S1:E1 (Sub) THE BLOOD WARFARE"},
            {"href": "/play/dub", "label": "S1:E1 (Dub) THE BLOOD WARFARE"},
        ])
        with self.assertRaisesRegex(SubtitleError, "inequívoca"):
            match_episode(page, BLEACH, 1, 1)

    def test_does_not_guess_episode_by_position_or_follow_redirect(self):
        page = FakePage([{"href": "/play/not-labeled", "label": "THE BLOOD WARFARE"}])
        with self.assertRaises(SubtitleError):
            match_episode(page, BLEACH, 1, 1)
        redirect = FakePage([], redirect="https://www.disneyplus.com/es-419/login")
        with self.assertRaises(SubtitleError):
            match_episode(redirect, BLEACH, 1, 1)


if __name__ == "__main__":
    unittest.main()
