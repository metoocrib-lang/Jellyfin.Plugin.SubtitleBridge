"""Offline checks for current Disney+ browse/entity series URLs and conservative matching."""
import unittest

from extractor.episode_discovery import match_episode
from extractor.hls import SubtitleError, allowed_disney_series_url
from extractor.server import valid_job


BLEACH = "https://www.disneyplus.com/es-419/browse/entity-571475d5-8b1a-453f-a57f-26bb4dd13304"


class FakePage:
    def __init__(self, anchors, redirect=None):
        self.anchors = anchors
        self.redirect = redirect
        self.url = None

    def goto(self, url, **kwargs):
        self.url = self.redirect or url

    def locator(self, selector):
        if selector != "a[href]":
            raise AssertionError("Unexpected selector")
        return self

    def evaluate_all(self, script):
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

    def test_localized_episode_label_and_play_link(self):
        page = FakePage([
            {"href": "/es-419/play/episode-a", "label": "T1:E1 THE BLOOD WARFARE"},
            {"href": "/es-419/play/episode-b", "label": "T1:E2 FOUNDATION STONES"},
        ])
        self.assertEqual(match_episode(page, BLEACH, 1, 1),
                         "https://www.disneyplus.com/es-419/play/episode-a")

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
