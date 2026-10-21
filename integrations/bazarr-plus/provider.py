"""Moonfin Bridge provider for the Bazarr+ Provider Hub v1.

Search only offers SRTs that the private Moonfin agent has already cached.
When explicitly enabled, a cache miss queues one asynchronous extraction;
the subtitle becomes searchable on a subsequent Bazarr+ search.

This plugin neither uploads to Bazarr nor needs a Bazarr API key.
"""
from __future__ import annotations

import base64
import hashlib
import json
import re
import urllib.parse
import urllib.request

PROVIDER_ID = "moonfinbridge"
_MAX_RESPONSE = 4_000_000
_HTTP_TIMEOUT = 12
_RULE = re.compile(r"^(\d{1,3})x(\d{1,3})(?:-(\d{1,3}))?=(\d{1,3})x(\d{1,3})$")
_ID = re.compile(r"^[a-f0-9]{64}$")
_DISNEY_PATH = re.compile(
    r"/(?:[a-z]{2}(?:-[a-z0-9]{2,4})/)?"
    r"(?:browse/entity-[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}"
    r"|series/[a-z0-9][a-z0-9._~%/-]{0,255})/?", re.I
)


def parse_mapping(mapping):
    """Return nonoverlapping one-to-one S/E ranges; empty is identity."""
    rules = []
    src, dst = set(), set()
    for line in str(mapping or "").splitlines():
        value = line.strip()
        if not value:
            continue
        match = _RULE.fullmatch(value.replace(" ", ""))
        if not match:
            raise ValueError("Regla de equivalencia Moonfin inválida")
        source_season, first, end, dest_season, dest_first = (
            int(x) if x is not None else None for x in match.groups()
        )
        if end is None:
            end = first
        if not 0 <= source_season <= 100 or not 0 <= dest_season <= 100:
            raise ValueError("Temporada de equivalencia fuera de rango")
        if first < 1 or end < first or end > 999 or dest_first < 1 or dest_first + end - first > 999:
            raise ValueError("Episodios de equivalencia fuera de rango")
        for i in range(first, end + 1):
            from_key = (source_season, i)
            to_key = (dest_season, dest_first + i - first)
            if from_key in src or to_key in dst:
                raise ValueError("Equivalencias Moonfin duplicadas o solapadas")
            src.add(from_key)
            dst.add(to_key)
        rules.append((source_season, first, end, dest_season, dest_first))
    return rules


def map_episode(mapping, season, episode):
    rules = parse_mapping(mapping)
    if not rules:
        return (season, episode)
    for s, first, end, dest_s, dest_first in rules:
        if season == s and first <= episode <= end:
            return (dest_s, dest_first + episode - first)
    return None  # Strict mapping: never guess an unlisted episode.


def _series_config(video, config):
    if not isinstance(video, dict) or video.get("kind") != "episode":
        return None
    tvdb_raw = video.get("series_tvdb_id") or video.get("tvdb_id")
    if isinstance(tvdb_raw, bool):
        return None
    try:
        tvdb = int(tvdb_raw)
        season = int(video["season"])
        episode = int(video["episode"])
    except (ValueError, TypeError, KeyError):
        return None
    if not 1 <= tvdb <= 2_147_483_647 or not 0 <= season <= 100 or not 1 <= episode <= 999:
        return None
    raw = str((config or {}).get("series_json") or "")
    if not raw or len(raw) > 16384:
        raise ValueError("Configura series_json con las series Disney+ permitidas")
    try:
        series = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ValueError("series_json debe ser una lista JSON válida") from exc
    if not isinstance(series, list) or len(series) > 32:
        raise ValueError("series_json debe tener de 1 a 32 series")
    found = []
    for entry in series:
        if not isinstance(entry, dict) or type(entry.get("tvdb_id")) is not int:
            raise ValueError("Cada serie debe indicar tvdb_id numérico")
        if entry["tvdb_id"] != tvdb:
            continue
        series_url = entry.get("series_url")
        if not isinstance(series_url, str) or len(series_url) > 500 or not _valid_disney_url(series_url):
            raise ValueError("La serie debe utilizar una URL Disney+ de catálogo válida")
        mapped = map_episode(entry.get("mapping", ""), season, episode)
        if mapped:
            found.append((series_url, mapped))
    if len(found) > 1:
        raise ValueError("Hay duplicados del mismo TVDB en series_json")
    return ((tvdb, season, episode, found[0][0], found[0][1]) if found else None)


def _valid_disney_url(value):
    try:
        url = urllib.parse.urlsplit(value)
        return (url.scheme == "https"
            and url.hostname in ("www.disneyplus.com", "disneyplus.com")
            and url.port in (None, 443)
            and not url.username and not url.password and not url.query and not url.fragment
            and bool(_DISNEY_PATH.fullmatch(url.path)))
    except ValueError:
        return False


def _base(config):
    config = dict(config or {})
    raw = str(config.get("agent_url") or "").strip().rstrip("/")
    token = str(config.get("bridge_token") or "")
    parsed = urllib.parse.urlsplit(raw)
    if (parsed.scheme not in ("http", "https") or not parsed.hostname
        or parsed.username or parsed.password or parsed.query or parsed.fragment
        or not token or len(token) < 32):
        raise ValueError("Configura URL interna válida del agente y token privado")
    return raw, token


def _post(path, body, config, max_bytes=_MAX_RESPONSE):
    base, token = _base(config)
    payload = json.dumps(body, separators=(",", ":")).encode("utf-8")
    if len(payload) > 180_000:
        raise ValueError("Solicitud al agente demasiado grande")
    url = base + path
    headers = {"X-Bridge-Key": token, "Content-Type": "application/json",
               "Accept": "application/json, application/x-subrip"}
    request = urllib.request.Request(url, data=payload, headers=headers, method="POST")
    opener = urllib.request.build_opener(_NoRedirect)
    with opener.open(request, timeout=_HTTP_TIMEOUT) as response:
        data = response.read(max_bytes + 1)
    if len(data) > max_bytes:
        raise ValueError("Respuesta del agente supera tamaño máximo")
    return data


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        return None


def _wants_spanish(languages):
    for item in languages or []:
        if not isinstance(item, dict):
            continue
        lang = str(item.get("alpha3") or item.get("alpha2") or "").lower()
        if lang not in ("spa", "es") or item.get("hi") or item.get("forced"):
            continue
        yield {"alpha3": "spa", "alpha2": "es", "hi": False, "forced": False}


class MoonfinBridgeProvider:
    def search(self, video, languages, config):
        entry = _series_config(video, config)
        if not entry or not any(_wants_spanish(languages)):
            return []
        tvdb, season, episode, series_url, coords = entry
        source_season, source_episode = coords
        body = {"tvdb_id": tvdb, "season": season, "episode": episode,
                "streaming_season": source_season, "streaming_episode": source_episode,
                "language": "es", "series_url": series_url,
                "enqueue": (config or {}).get("enable_requests") is True}
        response = json.loads(self._post("/v1/provider/lookup", body, config).decode("utf-8"))
        if not isinstance(response, dict) or response.get("state") != "ready":
            # queued, pending, busy, absent: never advertise an unfinished SRT.
            return []
        ident = response.get("id")
        checksum = response.get("sha256")
        if not isinstance(ident, str) or not _ID.fullmatch(ident):
            raise ValueError("Respuesta de identificador inválida")
        if not isinstance(checksum, str) or not _ID.fullmatch(checksum):
            raise ValueError("Respuesta de integridad inválida")
        stem = f"Moonfin.S{season:02d}E{episode:02d}.es"
        return [{
            "provider": PROVIDER_ID,
            "id": f"moonfin-{ident}",
            "language": {"alpha3": "spa", "alpha2": "es", "hi": False, "forced": False},
            "release_info": stem + ".DisneyPlus",
            "filename": stem + ".srt",
            "matches": ["series", "season", "episode", "tvdb_id"],
            "score": 95,
            "score_without_hash": 95,
            "score_out_of": 100,
            "hash_verifiable": False,
            "hearing_impaired_verifiable": True,
            "hearing_impaired": False,
            "display": {"source": "Moonfin Bridge", "release": stem},
            "provider_payload": {
                "provider": PROVIDER_ID,
                "schema": 1,
                "id": ident,
                "sha256": checksum,
                "language": "spa"
            },
        }]

    def _post(self, path, body, config):
        return _post(path, body, config)

    def download(self, provider_payload, language, config):
        payload = dict(provider_payload or {})
        identifier = payload.get("id")
        checksum = payload.get("sha256")
        if (payload.get("provider") != PROVIDER_ID or payload.get("schema") != 1
            or payload.get("language") != "spa"
            or not isinstance(identifier, str) or not _ID.fullmatch(identifier)
            or not isinstance(checksum, str) or not _ID.fullmatch(checksum)):
            raise ValueError("Moonfin: referencia de subtítulo inválida")
        if not any(_wants_spanish([language])):
            raise ValueError("Moonfin: idioma solicitado no coincide")
        data = self._post("/v1/provider/download", {"id": identifier}, config)
        if not data or b"\x00" in data or b"-->" not in data:
            raise ValueError("Moonfin: subtítulo SRT no válido")
        digest = hashlib.sha256(data).hexdigest()
        if digest != checksum:
            raise ValueError("Moonfin: error de integridad del subtítulo")
        return {
            "content_b64": base64.b64encode(data).decode("ascii"),
            "content_sha256": digest,
            "content_type": "application/x-subrip",
            "format": "srt",
            "encoding": "utf-8",
            "empty": False,
        }
