"""HLS WebVTT parser and SRT converter for accessible subtitle tracks."""
from __future__ import annotations

from dataclasses import dataclass
from html import unescape
import re
from urllib.parse import urljoin, urlsplit

MAX_BYTES = 4_000_000
MAX_SEGMENTS = 600
# Restricted to known Disney-family media domains to prevent playlist SSRF.
DISNEY_HOSTS = (
    "dssott.com", "dssedge.com", "disneyplus.com",
    "disney.com", "bamgrid.com",
)

class SubtitleError(ValueError):
    pass


def allowed_disney_url(url: str, *, episode: bool = False) -> bool:
    try:
        parts = urlsplit(url)
        host = (parts.hostname or "").lower()
        if parts.scheme != "https" or parts.username or parts.password or parts.port not in (None, 443):
            return False
        if episode:
            return host in ("www.disneyplus.com", "disneyplus.com") and parts.path not in ("", "/")
        return host in DISNEY_HOSTS or any(host.endswith("." + suffix) for suffix in DISNEY_HOSTS)
    except ValueError:
        return False


def allowed_disney_series_url(url: str) -> bool:
    """Permit only Disney+ series detail pages, including localized browse/entity URLs."""
    if not allowed_disney_url(url, episode=True):
        return False
    parts = urlsplit(url)
    if parts.query or parts.fragment:
        return False
    # Disney+ currently uses /es-419/browse/entity-UUID for series in Latin America.
    # Keep the legacy /series/ form for installations that still serve it.
    return bool(re.fullmatch(
        r"/(?:[a-z]{2}(?:-[a-z0-9]{2,4})/)?(?:browse/entity-"
        r"[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}|"
        r"series/[a-z0-9][a-z0-9._~%/-]{0,255})/?",
        parts.path, flags=re.IGNORECASE,
    ))


def attributes(raw: str) -> dict[str, str]:
    # HLS attribute list with quoted comma-containing values.
    return {
        match.group(1).upper(): match.group(2) if match.group(2) is not None else match.group(3)
        for match in re.finditer(r'([A-Za-z0-9-]+)=(?:"([^"]*)"|([^,]*))(?:,|$)', raw)
    }


@dataclass(frozen=True)
class SubtitleTrack:
    language: str
    name: str
    url: str
    forced: bool = False
    characteristics: str = ""


def tracks(master: str, origin: str) -> list[SubtitleTrack]:
    if len(master) > MAX_BYTES or not master.lstrip().startswith("#EXTM3U"):
        raise SubtitleError("Manifiesto HLS no válido")
    found: list[SubtitleTrack] = []
    for line in master.splitlines():
        if not line.startswith("#EXT-X-MEDIA:"):
            continue
        data = attributes(line.split(":", 1)[1])
        if data.get("TYPE") != "SUBTITLES" or "URI" not in data:
            continue
        url = urljoin(origin, data["URI"])
        if allowed_disney_url(url):
            found.append(SubtitleTrack(
                language=data.get("LANGUAGE", ""),
                name=data.get("NAME", ""),
                url=url,
                forced=data.get("FORCED", "").upper() == "YES",
                characteristics=data.get("CHARACTERISTICS", ""),
            ))
    return found


def choose(tracks_list: list[SubtitleTrack], language: str) -> SubtitleTrack:
    language = language.lower().replace("_", "-")
    allowed = [track for track in tracks_list if not track.forced and
               (track.language.lower() == language or track.language.lower().startswith(language + "-"))]
    if not allowed:
        raise SubtitleError("El reproductor no expuso subtítulos en el idioma solicitado")
    def score(track: SubtitleTrack) -> tuple:
        label = (track.name + " " + track.characteristics).lower()
        hearing_impaired = int(any(x in label for x in ("sdh", "closed captions", "transcribes-spoken-dialog")))
        regional = 0 if track.language.lower() == language else (
            1 if track.language.lower().endswith("-419") else 2)
        return (hearing_impaired, regional, track.name)
    return min(allowed, key=score)


def segment_urls(media: str, origin: str) -> list[str]:
    if len(media) > MAX_BYTES or not media.lstrip().startswith("#EXTM3U"):
        raise SubtitleError("Lista de segmentos inválida")
    result: list[str] = []
    for raw in media.splitlines():
        line = raw.strip()
        if line.startswith("#EXT-X-KEY:"):
            key = attributes(line.partition(":")[2])
            if key.get("METHOD", "NONE") != "NONE":
                raise SubtitleError("Pista cifrada: extracción no admitida")
        elif line and not line.startswith("#"):
            url = urljoin(origin, line)
            if not allowed_disney_url(url):
                raise SubtitleError("Segmento fuera de los dominios autorizados")
            if not urlsplit(url).path.lower().endswith((".vtt", ".webvtt")):
                raise SubtitleError("Esta pista no contiene WebVTT accesible")
            result.append(url)
            if len(result) > MAX_SEGMENTS:
                raise SubtitleError("Demasiados fragmentos VTT")
    if not result:
        raise SubtitleError("No se encontraron fragmentos VTT")
    return result


def seconds(value: str) -> float:
    parts = value.replace(",", ".").split(":")
    if len(parts) not in (2, 3):
        raise SubtitleError("Tiempo WebVTT inválido")
    try:
        return sum(float(part) * (60 ** i) for i, part in enumerate(reversed(parts)))
    except ValueError as exc:
        raise SubtitleError("Tiempo WebVTT inválido") from exc


def srt_time(value: float) -> str:
    if value < 0 or value > 360000:
        raise SubtitleError("Tiempo fuera de rango")
    ms = round(value * 1000)
    hour, rem = divmod(ms, 3_600_000)
    minute, rem = divmod(rem, 60_000)
    second, millis = divmod(rem, 1000)
    return f"{hour:02}:{minute:02}:{second:02},{millis:03}"


CUE = re.compile(r"^\s*((?:\d{2}:)?\d{2}:\d{2}[.,]\d{3})\s*-->\s*((?:\d{2}:)?\d{2}:\d{2}[.,]\d{3})(?:\s+.*)?$")
STAMP = re.compile(r"X-TIMESTAMP-MAP\s*=\s*LOCAL:([\d:.]+)\s*,\s*MPEGTS:(\d+)")


def cues(vtt: str) -> list[tuple[float, float, str]]:
    if len(vtt) > MAX_BYTES or not vtt.lstrip("\ufeff").startswith("WEBVTT"):
        raise SubtitleError("Segmento sin cabecera WebVTT")
    vtt = vtt.lstrip("\ufeff").replace("\r\n", "\n").replace("\r", "\n")
    stamp = STAMP.search(vtt)
    offset = (int(stamp.group(2)) / 90000 - seconds(stamp.group(1))) if stamp else 0.0
    result = []
    for block in re.split(r"\n\s*\n", vtt):
        lines = block.splitlines()
        if not lines or lines[0].startswith(("NOTE", "STYLE", "REGION")):
            continue
        match_line = next((i for i, line in enumerate(lines[:3]) if CUE.match(line)), None)
        if match_line is None:
            continue
        matched = CUE.match(lines[match_line])
        start = seconds(matched.group(1)) + offset
        end = seconds(matched.group(2)) + offset
        if start < 0 or end <= start:
            continue
        body = "\n".join(lines[match_line + 1:]).strip()
        body = re.sub(r"</?c(?:\.[\w-]+)*>", "", body)
        body = unescape(body)
        if body:
            result.append((start, end, body))
    return result


def to_srt(documents: list[str]) -> bytes:
    unique = {}
    for doc in documents:
        for cue in cues(doc):
            unique[(round(cue[0], 3), round(cue[1], 3), cue[2])] = cue
    if not unique:
        raise SubtitleError("La pista no contiene subtítulos válidos")
    ordered = sorted(unique.values())
    text = "\n\n".join(
        f"{index}\n{srt_time(start)} --> {srt_time(end)}\n{body}"
        for index, (start, end, body) in enumerate(ordered, start=1)
    ) + "\n"
    encoded = text.encode("utf-8")
    if len(encoded) > MAX_BYTES:
        raise SubtitleError("SRT demasiado grande")
    return encoded
