"""Scoped Bazarr upload with explicit Sonarr series/episode verification."""
from __future__ import annotations

import json
import re
import secrets
from urllib.parse import urlencode
import urllib.error
import urllib.request

from .hls import SubtitleError, MAX_BYTES

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        return None


class BazarrClient:
    def __init__(self, url: str, api_key: str):
        if not url.startswith(("http://", "https://")) or not api_key:
            raise SubtitleError("BAZARR_URL y BAZARR_API_KEY deben configurarse localmente")
        self.url = url.rstrip("/")
        self.api_key = api_key
        self.opener = urllib.request.build_opener(NoRedirect)

    def _request(self, path: str, *, body: bytes | None = None, content_type: str | None = None) -> bytes:
        headers = {"X-API-KEY": self.api_key}
        if content_type:
            headers["Content-Type"] = content_type
        request = urllib.request.Request(self.url + path, data=body, headers=headers,
                                         method="GET" if body is None else "POST")
        try:
            with self.opener.open(request, timeout=25) as response:
                raw = response.read(MAX_BYTES + 1)
                if len(raw) > MAX_BYTES:
                    raise SubtitleError("Bazarr devolvió demasiados datos")
                return raw
        except urllib.error.HTTPError as exc:
            raise SubtitleError(f"Bazarr respondió HTTP {exc.code}") from None
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            raise SubtitleError(f"No se pudo comunicar con Bazarr ({type(exc).__name__})") from None

    def validate_episode(self, series_id: int, episode_id: int):
        if series_id <= 0 or episode_id <= 0:
            raise SubtitleError("IDs de Sonarr inválidos")
        query = urlencode({"seriesid[]": series_id})
        data = json.loads(self._request("/api/episodes?" + query))
        if not any(int(ep.get("sonarrSeriesId") or 0) == series_id
                   and int(ep.get("sonarrEpisodeId") or 0) == episode_id
                   and ep.get("path") for ep in data.get("data", [])):
            raise SubtitleError("Bazarr no confirma que el episodio pertenezca a la serie")

    def upload(self, series_id: int, episode_id: int, language: str, srt: bytes):
        self.validate_episode(series_id, episode_id)
        if len(srt) > MAX_BYTES or not srt:
            raise SubtitleError("SRT vacío o demasiado grande")
        if not re.fullmatch(r"[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,4})?", language):
            raise SubtitleError("Código de idioma inválido")
        filename = f"Sonarr-{series_id}-Episode-{episode_id}.{language}.srt"
        boundary = "----subtitle-bridge-" + secrets.token_hex(12)
        chunks = []
        fields = {"seriesid": str(series_id), "episodeid": str(episode_id),
                  "language": language, "forced": "false", "hi": "false"}
        for k, v in fields.items():
            chunks.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode())
        chunks.extend([
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\nContent-Type: application/x-subrip\r\n\r\n".encode(),
            srt,
            f"\r\n--{boundary}--\r\n".encode(),
        ])
        self._request("/api/episodes/subtitles", body=b"".join(chunks),
                      content_type="multipart/form-data; boundary=" + boundary)
