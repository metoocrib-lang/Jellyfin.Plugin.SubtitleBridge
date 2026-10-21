"""Offline Bazarr+ adapter and private SRT cache regression tests."""
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import queue
import tempfile
import unittest
from unittest.mock import patch

from extractor import provider_bridge, server

MODULE = Path(__file__).resolve().parents[2] / "integrations" / "bazarr-plus" / "provider.py"
spec = importlib.util.spec_from_file_location("moonfin_bazarrplus_provider", MODULE)
provider = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provider)

SERIES_URL = "https://www.disneyplus.com/es-419/browse/entity-571475d5-8b1a-453f-a57f-26bb4dd13304"
TVDB = 123456
RULES = "17x1-13=1x1\n17x14-26=2x1\n17x27-40=3x1\n17x41-50=4x1"
VIDEO = {"kind": "episode", "series": "Bleach",
         "series_tvdb_id": TVDB, "season": 17, "episode": 3}
LANGUAGE = {"alpha3": "spa", "alpha2": "es", "hi": False, "forced": False}
TOKEN = "x" * 64
CONFIG = {
    "agent_url": "http://moonfin-streaming-agent:8787",
    "bridge_token": TOKEN,
    "series_json": json.dumps([{"tvdb_id": TVDB, "series_url": SERIES_URL, "mapping": RULES}]),
    "enable_requests": False,
}
SRT = b"1\n00:00:01,000 --> 00:00:02,000\nHola!\n"


class ProviderTests(unittest.TestCase):
    def test_mapper_strict_and_cours(self):
        self.assertEqual(provider.map_episode(RULES, 17, 3), (1, 3))
        self.assertEqual(provider.map_episode(RULES, 17, 14), (2, 1))
        self.assertEqual(provider.map_episode(RULES, 17, 41), (4, 1))
        self.assertIsNone(provider.map_episode(RULES, 16, 1))
        self.assertEqual(provider.map_episode("", 17, 3), (17, 3))
        with self.assertRaises(ValueError):
            provider.parse_mapping("17x1-3=1x1\n17x3-5=2x1")
        with self.assertRaises(ValueError):
            provider.parse_mapping("17x1=1x1\n17x2=1x1")

    def test_rejects_other_series_and_languages_without_network(self):
        candidate = provider.MoonfinBridgeProvider()
        with patch.object(candidate, "_post", side_effect=AssertionError("unexpected call")):
            self.assertEqual(candidate.search({**VIDEO, "series_tvdb_id": 88}, [LANGUAGE], CONFIG), [])
            self.assertEqual(candidate.search(VIDEO, [{**LANGUAGE, "forced": True}], CONFIG), [])
            self.assertEqual(candidate.search(VIDEO, [{**LANGUAGE, "hi": True}], CONFIG), [])
            self.assertEqual(candidate.search(VIDEO, [{"alpha3": "eng"}], CONFIG), [])

    def test_opt_in_off_and_no_false_positive_while_queued(self):
        candidate = provider.MoonfinBridgeProvider()
        calls = []
        def mock_post(path, data, config):
            calls.append((path, data))
            return b'{"state":"queued"}'
        with patch.object(candidate, "_post", side_effect=mock_post):
            self.assertEqual(candidate.search(VIDEO, [LANGUAGE], CONFIG), [])
        self.assertEqual(calls[0][0], "/v1/provider/lookup")
        self.assertFalse(calls[0][1]["enqueue"])
        self.assertEqual((calls[0][1]["streaming_season"], calls[0][1]["streaming_episode"]), (1, 3))
        self.assertNotIn("bridge_token", calls[0][1])

    def test_ready_then_download_integrity(self):
        candidate = provider.MoonfinBridgeProvider()
        ident = "a" * 64
        sha = hashlib.sha256(SRT).hexdigest()
        with patch.object(candidate, "_post", return_value=json.dumps(
            {"state": "ready", "id": ident, "sha256": sha}).encode("utf-8")):
            found = candidate.search(VIDEO, [LANGUAGE], CONFIG)
        self.assertEqual(len(found), 1)
        self.assertEqual(found[0]["provider"], "moonfinbridge")
        self.assertEqual(found[0]["matches"], ["series", "season", "episode", "tvdb_id"])
        self.assertNotIn(TOKEN, json.dumps(found))
        payload = found[0]["provider_payload"]
        with patch.object(candidate, "_post", return_value=SRT):
            downloaded = candidate.download(payload, LANGUAGE, CONFIG)
        self.assertEqual(base64.b64decode(downloaded["content_b64"]), SRT)
        self.assertEqual(downloaded["content_sha256"], sha)
        with patch.object(candidate, "_post", return_value=SRT+b"!"):
            with self.assertRaises(ValueError):
                candidate.download(payload, LANGUAGE, CONFIG)
        with self.assertRaises(ValueError):
            candidate.download({**payload, "id": "../secret"}, LANGUAGE, CONFIG)

    def test_bad_url_or_duplicate_mapping_rejected(self):
        cfg = dict(CONFIG)
        cfg["series_json"] = json.dumps([{"tvdb_id": TVDB,
                                         "series_url": "http://127.0.0.1/internal",
                                         "mapping": RULES}])
        with self.assertRaises(ValueError):
            provider._series_config(VIDEO, cfg)
        cfg["series_json"] = json.dumps([
            {"tvdb_id": TVDB, "series_url": SERIES_URL, "mapping": RULES},
            {"tvdb_id": TVDB, "series_url": SERIES_URL, "mapping": RULES}])
        with self.assertRaises(ValueError):
            provider._series_config(VIDEO, cfg)


class AgentCacheTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.state = patch.dict(os.environ, {
            "STATE_FILE": str(Path(self.dir.name) / "state" / "attempts.json")})
        self.state.start()
        self.queue = queue.Queue(maxsize=30)
        self.saved_queue = server.QUEUE
        self.saved_last = dict(server.LAST_RUN)
        server.QUEUE = self.queue
        server.LAST_RUN.clear()

    def tearDown(self):
        server.QUEUE = self.saved_queue
        server.LAST_RUN.clear()
        server.LAST_RUN.update(self.saved_last)
        self.state.stop()
        self.dir.cleanup()

    def request(self, enqueue=False):
        return {
            "tvdb_id": TVDB, "season": 17, "episode": 3,
            "streaming_season": 1, "streaming_episode": 3,
            "language": "es", "series_url": SERIES_URL, "enqueue": enqueue
        }

    def test_no_enqueue_by_default_and_exact_ready_cache(self):
        self.assertEqual(server.provider_lookup(self.request())["state"], "absent")
        self.assertTrue(self.queue.empty())
        response = server.provider_lookup(self.request(enqueue=True))
        self.assertEqual(response["state"], "queued")
        self.assertEqual(self.queue.qsize(), 1)
        job, url, key = self.queue.get_nowait()
        self.assertIsNone(url)
        self.assertIsNone(key)
        self.assertEqual(job["delivery"], "bazarr_plus")
        self.assertEqual(job["tvdb_id"], TVDB)
        provider_bridge.save_cached(job, SRT)
        ready = server.provider_lookup(self.request())
        self.assertEqual(ready["state"], "ready")
        self.assertEqual(ready["sha256"], hashlib.sha256(SRT).hexdigest())
        self.assertEqual(provider_bridge.get_cached(ready["id"]), SRT)
        self.assertEqual(server.provider_lookup({**self.request(), "episode": 4})["state"], "absent")

    def test_cooldown_cannot_create_duplicate_jobs(self):
        self.assertEqual(server.provider_lookup(self.request(enqueue=True))["state"], "queued")
        self.assertEqual(server.provider_lookup(self.request(enqueue=True))["state"], "pending")
        self.assertEqual(self.queue.qsize(), 1)

    def test_invalid_job_and_unsafe_cache_id(self):
        with self.assertRaises(ValueError):
            provider_bridge.validated_request({**self.request(), "series_url": "https://example.com/"})
        with self.assertRaises(ValueError):
            provider_bridge.validated_request({**self.request(), "tvdb_id": True})
        with self.assertRaises(ValueError):
            provider_bridge.get_cached("../../secret")


if __name__ == "__main__":
    unittest.main()
