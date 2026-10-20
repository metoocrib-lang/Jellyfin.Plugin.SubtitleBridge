import unittest
from unittest.mock import MagicMock

from extractor.disney import DisneyBrowserExtractor
from extractor.hls import SubtitleError

MASTER = '''#EXTM3U
#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="Spanish",LANGUAGE="es",URI="es.m3u8"
'''
PLAYLIST = "#EXTM3U\n#EXTINF:10,\none.vtt\n#EXT-X-ENDLIST\n"
VTT = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nPrueba\n"

class Response:
    def __init__(self, url, text, status=200):
        self.url, self._text, self.status = url, text, status
    def body(self): return self._text.encode()
    def dispose(self): pass

class Context:
    def __init__(self, responses):
        self.responses = responses
        self.request = self
    def get(self, url, **kwargs):
        if url not in self.responses:
            raise AssertionError("URL inesperada (no estaba entre los segmentos)")
        return Response(url, self.responses[url])

class Page:
    def __init__(self, manifest):
        self.manifest = manifest
        self.url = ""
        self.handlers = {}
    def on(self, event, fn): self.handlers[event] = fn
    def remove_listener(self, event, fn): self.handlers.pop(event, None)
    def goto(self, url, **kwargs):
        self.url = url
        self.handlers["response"](Response(self.manifest, ""))
    def get_by_role(self, *args, **kwargs): raise RuntimeError("Sin botón")
    def wait_for_timeout(self, *args): pass

class BrowserTests(unittest.TestCase):
    def test_capture_and_convert(self):
        url = "https://edge.dssott.com/disney/"
        response = {
            url + "master.m3u8": MASTER,
            url + "es.m3u8": PLAYLIST,
            url + "one.vtt": VTT,
        }
        page = Page(url + "master.m3u8")
        output = DisneyBrowserExtractor(Context(response), page, wait_seconds=2).extract(
            "https://www.disneyplus.com/play/test", "es"
        )
        self.assertIn(b"Prueba", output)
        self.assertNotIn("response", page.handlers)

    def test_wrong_episode_url_rejected_before_navigation(self):
        page = MagicMock()
        with self.assertRaises(SubtitleError):
            DisneyBrowserExtractor(MagicMock(), page).extract("http://localhost:1234/secret", "es")
        page.goto.assert_not_called()

if __name__ == "__main__":
    unittest.main()
