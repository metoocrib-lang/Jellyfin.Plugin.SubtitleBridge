"""Conservative matching of provider episode cards to known Sonarr S/E indices."""
from __future__ import annotations
import re
from urllib.parse import urljoin, urlsplit
from .hls import allowed_disney_url, SubtitleError


def match_episode(page, series_url: str, season: int, episode: int) -> str:
    """Only accept a Disney playback link with explicit season+episode in its label."""
    if not allowed_disney_url(series_url, episode=True):
        raise SubtitleError("URL de serie Disney+ inválida")
    page.goto(series_url, wait_until="domcontentloaded", timeout=40_000)
    if not allowed_disney_url(page.url, episode=True):
        raise SubtitleError("La página se redirigió fuera de Disney+")
    candidates = []
    anchor_data = page.locator("a[href]").evaluate_all(
        """els => els.slice(0, 900).map(e => ({href:e.getAttribute('href') || '',
           label:[e.innerText || '',e.getAttribute('aria-label') || ''].join(' ')}))"""
    )
    for anchor in anchor_data:
        href = urljoin(series_url, str(anchor.get("href", "")))
        path = urlsplit(href).path.lower()
        if not path.startswith("/play/") or not allowed_disney_url(href, episode=True):
            continue
        label = str(anchor.get("label", "")).lower()
        patterns = (
            rf"\bs\s*0*{season}\s*e\s*0*{episode}\b",
            rf"\b0*{season}\s*x\s*0*{episode}\b",
            rf"\btemporada\s+0*{season}\s*[,;: -]*\s*episodio\s+0*{episode}\b",
            rf"\bseason\s+0*{season}\s*[,;: -]*\s*episode\s+0*{episode}\b",
        )
        if any(re.search(pattern, label) for pattern in patterns):
            candidates.append(href)
    distinct = list(dict.fromkeys(candidates))
    if len(distinct) != 1:
        raise SubtitleError(
            "El catálogo no identifica de forma inequívoca ese episodio; requiere validación del selector"
        )
    return distinct[0]
