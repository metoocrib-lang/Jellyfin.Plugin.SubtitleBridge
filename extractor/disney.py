"""Capture visible Disney+ HLS subtitle manifests from Playwright playback.

The browser profile belongs to the user and is never uploaded or logged.
No extraction of media keys, protected tracks, or DRM-licensed content.
"""
from __future__ import annotations
import re
import time
from urllib.parse import urlsplit

from .hls import (
    MAX_BYTES, SubtitleError, allowed_disney_url, choose,
    segment_urls, to_srt, tracks,
)


class DisneyBrowserExtractor:
    def __init__(self, context, page, wait_seconds: int = 65):
        self.context = context
        self.page = page
        self.wait_seconds = wait_seconds

    def _fetch(self, url: str) -> str:
        if not allowed_disney_url(url):
            raise SubtitleError("Dominio de pista no autorizado")
        response = self.context.request.get(url, timeout=20_000, max_redirects=0)
        try:
            if response.status != 200 or not allowed_disney_url(response.url):
                raise SubtitleError("La pista no es accesible para la sesión")
            raw = response.body()
            if len(raw) > MAX_BYTES:
                raise SubtitleError("Respuesta de subtítulos demasiado grande")
            return raw.decode("utf-8-sig", "replace")
        finally:
            response.dispose()

    def extract(self, episode_url: str, language: str) -> bytes:
        if not allowed_disney_url(episode_url, episode=True):
            raise SubtitleError("La URL debe ser una reproducción de Disney+")
        observed: list[str] = []
        inspected: set[str] = set()

        def capture(response):
            try:
                url = response.url
                if response.status == 200 and urlsplit(url).path.lower().endswith(".m3u8") and allowed_disney_url(url):
                    if url not in observed:
                        observed.append(url)
            except (AttributeError, ValueError):
                pass

        self.page.on("response", capture)
        try:
            self.page.goto(episode_url, wait_until="domcontentloaded", timeout=40_000)
            if not allowed_disney_url(self.page.url, episode=True):
                raise SubtitleError("Disney+ redirigió fuera de la reproducción")
            try:
                play = self.page.get_by_role("button", name=re.compile(r"^(play|reproducir|ver ahora|watch now)$", re.I)).first
                if play.is_visible(timeout=1300):
                    play.click(timeout=2500)
            except Exception:
                pass
            stop = time.monotonic() + self.wait_seconds
            last_issue = "No se detectaron subtítulos HLS accesibles"
            while time.monotonic() < stop:
                for url in list(observed):
                    if url in inspected:
                        continue
                    inspected.add(url)
                    try:
                        manifest = self._fetch(url)
                        available = tracks(manifest, url)
                        if not available:
                            continue
                        selected = choose(available, language)
                        playlist = self._fetch(selected.url)
                        pieces = segment_urls(playlist, selected.url)
                        documents, total = [], 0
                        for piece in pieces:
                            data = self._fetch(piece)
                            total += len(data.encode("utf-8"))
                            if total > MAX_BYTES:
                                raise SubtitleError("La pista supera el tamaño permitido")
                            documents.append(data)
                        return to_srt(documents)
                    except SubtitleError as exc:
                        if "cifrada" in str(exc):
                            raise
                        last_issue = str(exc)
                self.page.wait_for_timeout(1_000)
            raise SubtitleError(last_issue + ". Verifica sesión y reproducción del episodio.")
        finally:
            self.page.remove_listener("response", capture)
