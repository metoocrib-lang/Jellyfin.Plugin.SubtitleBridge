import unittest

from extractor.hls import (
    SubtitleError, allowed_disney_url, attributes, choose, cues,
    segment_urls, to_srt, tracks
)

ORIGIN = "https://edge.dssott.com/path/master.m3u8"
MASTER = '''#EXTM3U
#EXT-X-INDEPENDENT-SEGMENTS
#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="main",NAME="Español, Latino",LANGUAGE="es-419",URI="es/main.m3u8"
#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="main",NAME="English SDH",LANGUAGE="en",URI="en/main.m3u8"
'''
MEDIA = '''#EXTM3U
#EXT-X-TARGETDURATION:5
#EXTINF:5,
one.vtt
#EXTINF:5,
two.vtt
#EXT-X-ENDLIST
'''
ONE = "WEBVTT\n\n00:00:01.000 --> 00:00:02.500\n<c.yellow>Hola &amp; adiós</c>\n"
TWO = "WEBVTT\n\n00:00:02.500 --> 00:00:05.000\nBuenos días\n"

class SubtitleHlsTests(unittest.TestCase):
    def test_tracks_and_language(self):
        available = tracks(MASTER, ORIGIN)
        self.assertEqual(choose(available, "es").language, "es-419")
        self.assertEqual(choose(available, "es").url, "https://edge.dssott.com/path/es/main.m3u8")
        self.assertEqual(attributes('NAME="Español, Latino",LANGUAGE="es"')["NAME"], "Español, Latino")

    def test_segments_resolve_paths(self):
        self.assertEqual(segment_urls(MEDIA, "https://edge.dssott.com/path/es/main.m3u8"),
                         ["https://edge.dssott.com/path/es/one.vtt",
                          "https://edge.dssott.com/path/es/two.vtt"])

    def test_valid_srt(self):
        result = to_srt([ONE, ONE, TWO]).decode()
        self.assertEqual(result.count("Hola & adiós"), 1)
        self.assertIn("00:00:01,000 --> 00:00:02,500", result)
        self.assertIn("2\n00:00:02,500 --> 00:00:05,000", result)

    def test_hls_timestamp_map(self):
        mapped = 'WEBVTT\nX-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:900000\n\n00:00:02.000 --> 00:00:03.000\nMapa\n'
        self.assertEqual(cues(mapped)[0][0], 12.0)

    def test_encrypted_subtitles_blocked(self):
        encrypted = '#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI="key.bin"\n#EXTINF:2,\npart.vtt\n'
        with self.assertRaisesRegex(SubtitleError, "cifrada"):
            segment_urls(encrypted, ORIGIN)

    def test_unrecognized_hosts_blocked(self):
        for url in ("https://dssott.com.evil.com/p/a.m3u8",
                    "http://edge.dssott.com/path/sub.vtt",
                    "https://127.0.0.1/private",
                    "https://edge.dssott.com:7777/sub.vtt"):
            self.assertFalse(allowed_disney_url(url))
        self.assertTrue(allowed_disney_url("https://www.disneyplus.com/play/abc", episode=True))
        self.assertFalse(allowed_disney_url("https://www.disneyplus.com.evil.net/play/abc", episode=True))

    def test_no_track_does_not_claim_success(self):
        with self.assertRaises(SubtitleError):
            choose(tracks(MASTER, ORIGIN), "fr")
        with self.assertRaises(SubtitleError):
            to_srt(["WEBVTT\n\n"])

if __name__ == "__main__":
    unittest.main()
