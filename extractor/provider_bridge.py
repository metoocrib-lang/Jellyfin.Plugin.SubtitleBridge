"""Server-side Bazarr+ provider cache. No Bazarr credentials are required.

The cache is isolated from the legacy direct-to-Bazarr queue: a subtitle is
visible to Bazarr+ only once the entire SRT is atomically written to disk.
"""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
import re

from .hls import MAX_BYTES, SubtitleError, allowed_disney_series_url

_CACHE_NAME = re.compile(r"^[a-f0-9]{64}$")
LANGUAGES = {"es"}


def cache_dir() -> Path:
    state_file = Path(os.environ.get("STATE_FILE", "/data/state/attempts.json"))
    return state_file.parent / "provider_cache"


def _integer(value, smallest: int, largest: int) -> bool:
    return type(value) is int and smallest <= value <= largest


def validated_request(data: dict) -> dict:
    if not isinstance(data, dict):
        raise SubtitleError("Solicitud Bazarr+ inválida")
    required = {
        "tvdb_id": (1, 2_147_483_647),
        "season": (0, 100),
        "episode": (1, 999),
        "streaming_season": (0, 100),
        "streaming_episode": (1, 999),
    }
    if any(not _integer(data.get(key), *bounds) for key, bounds in required.items()):
        raise SubtitleError("Identificadores Bazarr+ inválidos")
    lang = data.get("language")
    if not isinstance(lang, str) or lang not in LANGUAGES:
        raise SubtitleError("Idioma no admitido por Moonfin Provider")
    url = data.get("series_url")
    if not isinstance(url, str) or len(url) > 500 or not allowed_disney_series_url(url):
        raise SubtitleError("URL de serie Disney+ inválida")
    if not isinstance(data.get("enqueue", False), bool):
        raise SubtitleError("Indicador enqueue inválido")
    return {key: data[key] for key in required} | {
        "language": lang, "series_url": url, "enqueue": data.get("enqueue", False)
    }


def cache_id(tvdb_id: int, season: int, episode: int, language: str) -> str:
    value = f"bazarrplus:v1:{tvdb_id}:{season}:{episode}:{language}"
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def get_cached(identifier: str) -> bytes | None:
    if not isinstance(identifier, str) or not _CACHE_NAME.fullmatch(identifier):
        raise SubtitleError("Identificador SRT no válido")
    target = cache_dir() / (identifier + ".srt")
    if not target.is_file() or target.is_symlink():
        return None
    if target.stat().st_size > MAX_BYTES:
        raise SubtitleError("SRT excede tamaño permitido")
    data = target.read_bytes()
    if not data or len(data) > MAX_BYTES or b"\x00" in data or b"-->" not in data:
        raise SubtitleError("SRT guardado inválido")
    return data


def save_cached(job: dict, data: bytes) -> None:
    if job.get("delivery") != "bazarr_plus":
        raise SubtitleError("No es un trabajo Bazarr+")
    if not isinstance(data, bytes) or not data or len(data) > MAX_BYTES:
        raise SubtitleError("SRT inválido")
    if b"\x00" in data or b"-->" not in data:
        raise SubtitleError("El contenido no parece un SRT")
    identifier = cache_id(job["tvdb_id"], job["tvdb_season"],
                          job["tvdb_episode"], job["language"])
    directory = cache_dir()
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    temporary = directory / (identifier + ".tmp")
    target = directory / (identifier + ".srt")
    try:
        with temporary.open("wb") as stream:
            os.fchmod(stream.fileno(), 0o600)
            stream.write(data)
        temporary.replace(target)
    finally:
        temporary.unlink(missing_ok=True)


def job_from_request(data: dict) -> tuple[dict, str]:
    request = validated_request(data)
    ident = cache_id(request["tvdb_id"], request["season"],
                     request["episode"], request["language"])
    # Synthetic IDs are for agent event display only; these MUST NEVER be
    # sent to Bazarr's /api/episodes/subtitles endpoint.
    job = {
        "provider": "disney",
        "series_url": request["series_url"],
        "series_id": request["tvdb_id"],
        "episode_id": request["season"] * 1000 + request["episode"],
        "season": request["streaming_season"],
        "episode": request["streaming_episode"],
        "language": request["language"],
        "delivery": "bazarr_plus",
        "tvdb_id": request["tvdb_id"],
        "tvdb_season": request["season"],
        "tvdb_episode": request["episode"],
    }
    return job, ident
