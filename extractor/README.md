# Browser-based subtitle extractor (experimental)

**Goal:** user chooses a series/language in Jellyfin → authenticated provider browser can locate an accessible subtitle track → WebVTT/SRT → Bazarr. Two workflows coexist: **single-URL compatibility probe** (`compose.yml`) and the **Jellyfin-controlled resident agent** (`compose.agent.yml`). The resident agent can receive a specific episode from Jellyfin, with optional per-series season/episode mappings; episode discovery in Disney+ remains conservative and experimental.

## What is implemented

- Real Chromium/Playwright *persistent* private browser profile, with login performed by the user directly on Disney+.
- Detection of player-requested HLS playlists, matching a non-forced subtitle language track, retrieving accessible WebVTT fragments, and converting to SRT.
- Blocks encrypted subtitle playlists and any URL outside the known Disney media-domain allowlist. Does not request licenses, keys, DRM media, or bypass access controls.
- Optional Bazarr multipart SRT upload. Before writing, validates the numeric Sonarr series and episode IDs against Bazarr's /api/episodes endpoint.
- Offline unit tests with synthetic HLS/VTT, browser response mocks, and verification that blocked hosts or encryption are rejected.
- A shared interface design for Netflix, Prime Video, Max and Apple TV+; those providers remain **pending**. No claim of working unattended extraction.

## Start one-episode compatibility test

1. Install Docker on a machine where a browser can play Disney+ and you can access its local desktop. The official Playwright image uses Chromium; playback may fail due to DRM or site browser requirements.
2. In the repository root run: `cp extractor/.env.example extractor/.env` and edit the private `.env` with the Disney+ episode URL, language, `SONARR_SERIES_ID`, `SONARR_EPISODE_ID`, local `BAZARR_URL`, and a strong **VNC_PASSWORD**. Do **not** put Disney+ passwords in the file.
3. Keep `UPLOAD_TO_BAZARR=false` on your first try.
4. Run: `docker compose -f extractor/compose.yml up --build disney-probe`.
5. Visit `http://localhost:6080/vnc.html` on the Docker host. Enter the private VNC password. Log into Disney+ directly, select the proper profile and complete normal authentication, within the `LOGIN_GRACE_SECONDS` window.
6. The agent then opens the episode URL, attempts reproduction, detects accessible subtitle playlists, and saves a local SRT to `extractor/output/`. It exits after the attempt. Re-running uses the saved browser session in the private volume. For subsequent runs, set `LOGIN_GRACE_SECONDS=0`.
7. Inspect the SRT and verify timestamps, encoding and **episode association**. Only then change `UPLOAD_TO_BAZARR=true` to attempt sending the SRT to Bazarr, which may require correct path mapping and language configuration.

### Network boundaries

- noVNC is mapped only to `127.0.0.1:6080`; do NOT expose it on the Internet. VNC requires a separate local password, but noVNC itself uses HTTP. For remote access, use an SSH tunnel rather than opening this port publicly.
- Browsing profiles contain valuable active streaming sessions; protect the Docker volume and system backups. Never send those profiles, cookies, API keys, or signed subtitle URLs to ChatGPT or GitHub.
- The agent's browser runs in a container separate from Jellyfin. A valid Jellyfin install neither guarantees DRM playback in Playwright Chromium nor authenticates Disney+. This is a technical feasibility test, not proof of production compatibility.
- The **authenticated queue and new-episode listener** are implemented in the resident agent / Jellyfin plugin; they only process episodes that meet Sonarr/Bazarr eligibility checks. The probe described above remains a separate one-off test and does not use that queue.

## Unit tests

```sh
python -m unittest discover -s extractor/tests -v
```

## Bazarr+ como proveedor (prototipo)

El agente residente incluye una API privada de búsqueda en caché y descarga de SRT (`/v1/provider/lookup` y `/v1/provider/download`), protegida por el mismo `X-Bridge-Key`. Los trabajos que lleguen desde el [proveedor Bazarr+](../integrations/bazarr-plus/README.md) **no** se suben directamente a Bazarr: el SRT se almacena en el volumen `streaming_state` y Bazarr+ se encargaría de importarlo tras seleccionarlo. Esta funcionalidad todavía no está publicada en el Marketplace y requiere validación en un entorno Bazarr+ aislado.

## Agente residente controlado por Jellyfin (v0.6)

Además de la prueba manual de un episodio descrita arriba, el proyecto incluye `compose.agent.yml`: servidor autenticado con cola limitada y navegador privado. Jellyfin programa las revisiones, consulta Bazarr y envía los episodios faltantes. El agente intenta encontrar el enlace de reproducción por número de temporada/capítulo; no usa contraseñas del usuario ni elude DRM. Usa `/v1/status`, `/v1/login` y `/v1/scan` con el encabezado secreto `X-Bridge-Key`.

Para instalar el agente en NAS consulta la [guía multi-NAS (Synology, QNAP, TrueNAS SCALE, Unraid y Linux Docker)](../docs/NAS_INSTALLATION.md) o las instrucciones de [ZimaBoard](ZIMABOARD.md). Los otros cuatro proveedores aún no tienen extracción real. Las páginas de catálogo pueden cambiar y es posible que se necesite ajustar el selector después de una prueba real.
