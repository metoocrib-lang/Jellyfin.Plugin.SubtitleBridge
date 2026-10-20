"""Conservative matching of Disney+ episode cards to Sonarr season/episode indices."""
from __future__ import annotations

import re
from urllib.parse import urljoin, urlsplit

from .hls import SubtitleError, allowed_disney_series_url, allowed_disney_url

_SEASON_BUTTON = re.compile(r"^\s*(?:Temporada|Season)\s+\d+\s*$", re.IGNORECASE)
_NUMBERED_EPISODE = re.compile(r"^\s*(\d{1,3})\s*[.):\-\u2013]\s*\S")


def _select_season(page, season: int) -> bool:
    """Select the season on current Disney+ UI; return True only if UI confirms it.

    A standalone number is not a safe episode label unless the season has been
    verified explicitly. Old layouts without a selector still allow S01E01 labels.
    """
    selector = page.get_by_role("button", name=_SEASON_BUTTON).first
    if selector.count() == 0:
        return False
    current = " ".join(selector.inner_text().split())
    desired = f"Temporada {season}" if current.lower().startswith("temporada") else f"Season {season}"
    if current.casefold() != desired.casefold():
        selector.click(timeout=6_000)
        option = page.get_by_text(desired, exact=True)
        if option.count() == 0:
            raise SubtitleError("El catálogo no ofrece la temporada solicitada")
        option.last.click(timeout=6_000)
        page.wait_for_timeout(3_000)
    # Do not trust card numbers if the dropdown failed to change seasons.
    selected = " ".join(selector.inner_text().split())
    if selected.casefold() != desired.casefold():
        raise SubtitleError("El catálogo no confirmó la temporada solicitada")
    # Explicitly allow time for dynamically loaded episode cards.
    page.wait_for_timeout(2_000)
    return True


def _labelled_season_episode(label: str, season: int, episode: int) -> bool:
    patterns = (
        rf"\bs\s*0*{season}\s*e\s*0*{episode}\b",
        rf"\b[st]\s*0*{season}\s*:\s*e\s*0*{episode}\b",
        rf"\b0*{season}\s*x\s*0*{episode}\b",
        rf"\btemporada\s+0*{season}\s*[,;: -]*\s*episodio\s+0*{episode}\b",
        rf"\bseason\s+0*{season}\s*[,;: -]*\s*episode\s+0*{episode}\b",
    )
    return any(re.search(pattern, label, re.IGNORECASE) for pattern in patterns)


def match_episode(page, series_url: str, season: int, episode: int) -> str:
    """Find one unambiguous playback URL without guessing from list positions."""
    if not allowed_disney_series_url(series_url):
        raise SubtitleError("URL de serie Disney+ inválida")
    page.goto(series_url, wait_until="domcontentloaded", timeout=40_000)
    if not allowed_disney_series_url(page.url):
        raise SubtitleError("La página se redirigió fuera de Disney+")

    # Disney+ hydrates season navigation after the DOMContentLoaded event.
    # Without this delay the selector may not exist yet and numbered episode
    # labels are deliberately ignored. A five-second settle was verified on
    # the user\u0027s real series page before selecting season 1.
    page.wait_for_timeout(5_000)
    if not allowed_disney_series_url(page.url):
        raise SubtitleError("La página se redirigió fuera de Disney+")

    season_verified = _select_season(page, season)
    anchor_data = page.locator("a[href]").evaluate_all(
        """els => els.slice(0, 900).map(e => ({href:e.getAttribute('href') || '',
           label:[e.innerText || '',e.getAttribute('aria-label') || ''].join(' ')}))"""
    )
    explicitly_labelled = []
    numbered = {}
    for anchor in anchor_data:
        href = urljoin(page.url, str(anchor.get("href", "")))
        path = urlsplit(href).path.lower()
        path = re.sub(r"^/[a-z]{2}(?:-[a-z0-9]{2,4})?/", "/", path)
        if not path.startswith("/play/") or not allowed_disney_url(href, episode=True):
            continue
        label = str(anchor.get("label", "")).strip()
        if _labelled_season_episode(label, season, episode):
            explicitly_labelled.append(href)
        if season_verified:
            numbered_match = _NUMBERED_EPISODE.match(label)
            if numbered_match:
                number = int(numbered_match.group(1))
                numbered.setdefault(number, set()).add(href)

    # Keep a unique candidate, including cases where the same URL appears twice.
    candidates = set(explicitly_labelled)
    if season_verified and episode in numbered:
        # Cross-check an adjacent numbered card; do not mistake e.g. a
        # "1. Continue" recommendation for an episode card.
        if episode - 1 in numbered or episode + 1 in numbered:
            candidates.update(numbered[episode])
    if len(candidates) != 1:
        raise SubtitleError(
            "El catálogo no identifica de forma inequívoca ese episodio; requiere validación del selector"
        )
    return next(iter(candidates))
