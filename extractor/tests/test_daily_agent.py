import unittest
from unittest.mock import patch

from extractor.episode_discovery import match_episode
from extractor.server import valid_job, enqueue
from extractor.hls import SubtitleError


def sample():
    return {
        "provider": "disney", "series_url": "https://www.disneyplus.com/series/demo",
        "series_id": 4, "episode_id": 7, "season": 1,
        "episode": 3, "language": "es"
    }


class FakeLinks:
    def __init__(self, anchors): self.anchors = anchors
    def evaluate_all(self, _): return self.anchors


class FakeMissingSeason:
    first = None

    def __init__(self):
        self.first = self

    def count(self):
        return 0


class FakePage:
    url = "https://www.disneyplus.com/series/demo"
    def __init__(self, anchors): self.anchors = anchors
    def goto(self, *args, **kwargs): pass
    def locator(self, _): return FakeLinks(self.anchors)
    def get_by_role(self, *_args, **_kwargs): return FakeMissingSeason()
    def wait_for_timeout(self, _ms): pass


class AgentTests(unittest.TestCase):
    def test_job_validation(self):
        self.assertTrue(valid_job(sample()))
        for override in ({"series_url": "http://127.0.0.1/secrets"},
                         {"provider": "invalid"}, {"episode_id": 0},
                         {"language": "es/../../"}):
            self.assertFalse(valid_job({**sample(), **override}))

    def test_exact_episode_matching(self):
        page = FakePage([{"href": "/play/id-3", "label": "S01E03 — Título"},
                         {"href": "/play/id-4", "label": "S01E04 — Título"}])
        self.assertEqual(match_episode(page, sample()["series_url"], 1, 3),
                         "https://www.disneyplus.com/play/id-3")

    def test_ambiguous_episode_rejected(self):
        page = FakePage([{"href": "/play/a", "label": "S01E03"},
                         {"href": "/play/b", "label": "S01E03"}])
        with self.assertRaises(SubtitleError):
            match_episode(page, sample()["series_url"], 1, 3)

    def test_no_episode_number_not_guessed(self):
        page = FakePage([{"href": "/play/a", "label": "Watch now"}])
        with self.assertRaises(SubtitleError):
            match_episode(page, sample()["series_url"], 1, 3)

    def test_unimplemented_source_no_queue(self):
        job = {**sample(), "provider": "netflix"}
        with patch("extractor.server.BazarrClient"):
            result = enqueue([job], "http://bazarr:6767", "dummy")
        self.assertEqual(result["queued"], 0)
        self.assertEqual(result["not_supported"], 1)


if __name__ == "__main__":
    unittest.main()
