import json
import unittest
from unittest.mock import Mock

from extractor.bazarr import BazarrClient
from extractor.hls import SubtitleError

class BazarrTests(unittest.TestCase):
    def test_validates_episode_before_upload(self):
        client = BazarrClient("http://localhost:6767", "test-key")
        messages = []
        def fake_request(path, **kwargs):
            messages.append((path, kwargs))
            if path.startswith("/api/episodes?"):
                return json.dumps({"data": [
                    {"sonarrSeriesId":11,"sonarrEpisodeId":50,"path":"/series/a.mkv"}
                ]}).encode()
            return b""
        client._request = fake_request
        client.upload(11, 50, "es", b"1\n00:00:01,000 --> 00:00:02,000\nPrueba\n")
        self.assertEqual(len(messages), 2)
        self.assertIn("multipart/form-data", messages[1][1]["content_type"])

    def test_does_not_upload_wrong_series(self):
        client = BazarrClient("http://localhost:6767", "test-key")
        writes = []
        def fake_request(path, **kwargs):
            writes.append(path)
            return b'{"data":[{"sonarrSeriesId":10,"sonarrEpisodeId":50,"path":"/tv/a.mkv"}]}'
        client._request = fake_request
        with self.assertRaisesRegex(SubtitleError, "no confirma"):
            client.upload(11, 50, "es", b"some content")
        self.assertEqual(len(writes), 1)

if __name__ == "__main__":
    unittest.main()
