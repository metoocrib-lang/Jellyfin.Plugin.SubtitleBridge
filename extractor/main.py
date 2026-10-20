"""One-episode Disney+ extraction probe; run in an interactive Chrome profile.

User logs into Disney+ directly in the browser. The process never accepts
streaming-site passwords. One legitimate playback URL is required for this
initial compatibility test; catalog-wide discovery is future work.
"""
from __future__ import annotations
import os
from pathlib import Path
import re

from .bazarr import BazarrClient
from .disney import DisneyBrowserExtractor
from .hls import SubtitleError, allowed_disney_url


def run():
    # Import only at runtime; unit tests need no Playwright installation.
    from playwright.sync_api import sync_playwright

    episode_url = os.environ.get("DISNEY_EPISODE_URL", "").strip()
    if not allowed_disney_url(episode_url, episode=True):
        raise SubtitleError("Configura DISNEY_EPISODE_URL con una URL de episodio de disneyplus.com")
    language = os.environ.get("SUBTITLE_LANGUAGE", "es").strip()
    if not re.fullmatch(r"[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,4})?", language):
        raise SubtitleError("SUBTITLE_LANGUAGE no es válido")
    profile = Path(os.environ.get("BROWSER_PROFILE", "/data/browser")).resolve()
    output_dir = Path(os.environ.get("OUTPUT_DIR", "/data/output")).resolve()
    profile.mkdir(parents=True, exist_ok=True, mode=0o700)
    output_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
    grace = max(0, min(int(os.environ.get("LOGIN_GRACE_SECONDS", "120")), 900))
    series_id = int(os.environ.get("SONARR_SERIES_ID", "0"))
    episode_id = int(os.environ.get("SONARR_EPISODE_ID", "0"))
    auto_upload = os.environ.get("UPLOAD_TO_BAZARR", "false").lower() == "true"

    print("Iniciando perfil de navegador privado. Inicia sesión directamente en Disney+ mediante noVNC.", flush=True)
    with sync_playwright() as p:
        context = p.chromium.launch_persistent_context(
            user_data_dir=str(profile), headless=False, accept_downloads=False,
            args=["--no-default-browser-check", "--disable-dev-shm-usage"],
        )
        try:
            page = context.pages[0] if context.pages else context.new_page()
            page.goto("https://www.disneyplus.com/", wait_until="domcontentloaded", timeout=60_000)
            if grace:
                print(f"Ventana de autenticación: {grace} segundos. No pegues contraseñas en Jellyfin.", flush=True)
                page.wait_for_timeout(grace * 1000)
            print("Intentando reproducir el episodio y detectar WebVTT accesible…", flush=True)
            result = DisneyBrowserExtractor(context, page).extract(episode_url, language)
            name = f"Disney-subtitle-{series_id or 'test'}-{episode_id or 'test'}.{language}.srt"
            output = output_dir / name
            # Refuse to overwrite existing results unexpectedly.
            with output.open("xb") as stream:
                stream.write(result)
            os.chmod(output, 0o600)
            print(f"SRT generado localmente: {output.name}; {len(result)} bytes.", flush=True)
            if auto_upload:
                if series_id <= 0 or episode_id <= 0:
                    raise SubtitleError("Para importar en Bazarr se requieren SONARR_SERIES_ID y SONARR_EPISODE_ID")
                BazarrClient(os.environ.get("BAZARR_URL", ""), os.environ.get("BAZARR_API_KEY", "")).upload(
                    series_id, episode_id, language, result
                )
                print("Bazarr aceptó el archivo SRT para el episodio indicado.", flush=True)
            else:
                print("UPLOAD_TO_BAZARR=false: no se ha modificado Bazarr.", flush=True)
        finally:
            context.close()


if __name__ == "__main__":
    try:
        run()
    except (SubtitleError, OSError, ValueError) as exc:
        # Do not print signed CDN URLs or browser session contents.
        print("Error de extracción:", type(exc).__name__, str(exc).split("?", 1)[0][:160], flush=True)
        raise SystemExit(1) from None
