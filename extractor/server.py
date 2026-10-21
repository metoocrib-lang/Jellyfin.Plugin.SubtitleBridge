"""Authenticated local queue for daily subtitling tasks from the Jellyfin plugin.

HTTP API is private to the Docker network and protected by X-Bridge-Key.
The user authenticates streaming accounts in noVNC; no passwords are collected.
"""
from __future__ import annotations
from collections import deque
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import secrets
import threading
import time

from .bazarr import BazarrClient
from .disney import DisneyBrowserExtractor
from .episode_discovery import match_episode
from .hls import SubtitleError, allowed_disney_series_url
from .providers import SUPPORTED_SITES
from .provider_bridge import get_cached, job_from_request, save_cached, validated_request

MAX_JSON = 180_000
MAX_JOBS = 30
RETRY_SECONDS = 24 * 3600
LOCK = threading.Lock()
BROWSER_LOCK = threading.Lock()
QUEUE = queue.Queue(maxsize=MAX_JOBS)
EVENTS = deque(maxlen=100)
LAST_RUN = {}
SESSION = {"disney": "no_validada", "netflix": "pendiente",
           "prime": "pendiente", "max": "pendiente", "apple": "pendiente"}
PERSIST = Path(os.environ.get("STATE_FILE", "/data/state/attempts.json"))
PROFILE = Path(os.environ.get("BROWSER_PROFILE", "/data/browser"))


def snapshot():
    with LOCK:
        return {"providers": dict(SESSION), "queue": QUEUE.qsize(), "events": list(EVENTS)[-30:],
                "notice": "Una sesion guardada no confirma que el proveedor permita reproducir subtitulos."}


def add_event(job, state):
    # No episode URLs or API keys are recorded in the public status.
    obj = {"provider": job.get("provider"), "series_id": job.get("series_id"),
           "episode_id": job.get("episode_id"), "status": state,
           "at": datetime.now(timezone.utc).isoformat(timespec="seconds")}
    with LOCK:
        EVENTS.append(obj)


def remember():
    PERSIST.parent.mkdir(parents=True, exist_ok=True)
    temporary = PERSIST.with_suffix(".tmp")
    with LOCK:
        data = dict(LAST_RUN)
    temporary.write_text(json.dumps(data), encoding="utf-8")
    os.chmod(temporary, 0o600)
    temporary.replace(PERSIST)


def load_previous():
    if not PERSIST.exists():
        return
    try:
        data = json.loads(PERSIST.read_text(encoding="utf-8"))
        if isinstance(data, dict):
            with LOCK:
                LAST_RUN.update({str(key): float(value) for key, value in data.items()
                                 if isinstance(value, (int, float))})
    except (ValueError, OSError):
        pass


def valid_job(job):
    if not isinstance(job, dict) or job.get("provider") not in SUPPORTED_SITES:
        return False
    if any(not isinstance(job.get(k), int) or isinstance(job.get(k), bool)
           or job[k] < 1 for k in ("series_id", "episode_id", "episode")):
        return False
    if not isinstance(job.get("season"), int) or not 0 <= job["season"] <= 100:
        return False
    if not isinstance(job.get("language"), str) or not re.fullmatch(
            r"[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,4})?", job["language"]):
        return False
    return job["provider"] != "disney" or (
        isinstance(job.get("series_url"), str)
        and allowed_disney_series_url(job["series_url"])
    )


def enqueue(jobs, bazarr_url, bazarr_key):
    accepted, cooled, unsupported = 0, 0, 0
    if not isinstance(jobs, list) or len(jobs) > MAX_JOBS:
        raise SubtitleError("Número de episodios inválido")
    if not isinstance(bazarr_url, str) or not isinstance(bazarr_key, str) or not bazarr_key:
        raise SubtitleError("Falta la conexión privada a Bazarr")
    # Validate URL early. Do not log or write Bazarr keys to disk.
    bazarr = BazarrClient(bazarr_url, bazarr_key)
    _ = bazarr
    now = time.time()
    for job in jobs:
        if not valid_job(job):
            raise SubtitleError("Datos del episodio o URL de proveedor no válidos")
        if job["provider"] != "disney":
            add_event(job, "conector_pendiente")
            unsupported += 1
            continue
        key = f'{job["provider"]}:{job["series_id"]}:{job["episode_id"]}:{job["language"]}'
        with LOCK:
            prior = LAST_RUN.get(key, 0)
            if now - prior < RETRY_SECONDS:
                cooled += 1
                continue
            if QUEUE.full():
                break
            LAST_RUN[key] = now
        QUEUE.put_nowait((dict(job), bazarr_url, bazarr_key))
        add_event(job, "en_cola")
        accepted += 1
    if accepted:
        remember()
    return {"queued": accepted, "cooldown": cooled, "not_supported": unsupported,
            "message": "Trabajos aceptados; todavía no confirma descarga"}


def provider_lookup(data):
    """Check SRT availability or opt-in queue exactly one provider-only job."""
    request = validated_request(data)
    job, identifier = job_from_request(request)
    cached = get_cached(identifier)
    if cached is not None:
        return {"state": "ready", "id": identifier,
                "sha256": hashlib.sha256(cached).hexdigest()}
    if not request["enqueue"]:
        return {"state": "absent"}
    key = "bazarrplus:" + identifier
    now = time.time()
    with LOCK:
        if now - LAST_RUN.get(key, 0) < RETRY_SECONDS:
            return {"state": "pending"}
        if QUEUE.full():
            return {"state": "busy"}
        LAST_RUN[key] = now
        # No Bazarr key/url is sent or stored in provider-only jobs.
        QUEUE.put_nowait((job, None, None))
    add_event(job, "en_cola_bazarr_plus")
    remember()
    return {"state": "queued"}


def start_session(provider: str):
    if provider != "disney":
        raise SubtitleError("Conector aún no implementado para esta plataforma")
    if not BROWSER_LOCK.acquire(blocking=False):
        raise SubtitleError("El navegador está ocupado; vuelve a intentarlo")
    with LOCK:
        SESSION["disney"] = "abriendo_navegador"

    def browser_login():
        try:
            from playwright.sync_api import sync_playwright
            PROFILE.mkdir(parents=True, exist_ok=True)
            with sync_playwright() as p:
                context = p.chromium.launch_persistent_context(
                    user_data_dir=str(PROFILE / "disney"),
                    headless=False, args=["--disable-dev-shm-usage"],
                    accept_downloads=False,
                )
                try:
                    page = context.pages[0] if context.pages else context.new_page()
                    page.goto("https://www.disneyplus.com/", wait_until="domcontentloaded", timeout=60_000)
                    with LOCK:
                        SESSION["disney"] = "autenticacion_en_navegador"
                    page.wait_for_timeout(240_000)
                finally:
                    context.close()
            with LOCK:
                SESSION["disney"] = "perfil_guardado_sin_verificar"
        except Exception:
            with LOCK:
                SESSION["disney"] = "error_de_navegador"
        finally:
            BROWSER_LOCK.release()
    threading.Thread(target=browser_login, daemon=True, name="disney-auth").start()
    return {"started": True, "message": "Abre noVNC por túnel SSH y entra a Disney+; ventana de 4 minutos"}


def worker():
    while True:
        job, bazarr_url, bazarr_key = QUEUE.get()
        try:
            if not BROWSER_LOCK.acquire(timeout=300):
                add_event(job, "navegador_ocupado")
                continue
            try:
                add_event(job, "procesando")
                from playwright.sync_api import sync_playwright
                with sync_playwright() as p:
                    context = p.chromium.launch_persistent_context(
                        user_data_dir=str(PROFILE / "disney"), headless=False,
                        accept_downloads=False, args=["--disable-dev-shm-usage"],
                    )
                    try:
                        page = context.pages[0] if context.pages else context.new_page()
                        episode_url = match_episode(
                            page, job["series_url"], job["season"], job["episode"]
                        )
                        srt = DisneyBrowserExtractor(context, page).extract(
                            episode_url, job["language"]
                        )
                    finally:
                        context.close()
                if job.get("delivery") == "bazarr_plus":
                    # Do not upload using synthetic IDs: Bazarr+ owns import.
                    save_cached(job, srt)
                    add_event(job, "srt_listo_bazarr_plus")
                else:
                    BazarrClient(bazarr_url, bazarr_key).upload(
                        job["series_id"], job["episode_id"], job["language"], srt
                    )
                    add_event(job, "enviado_a_bazarr")
            finally:
                BROWSER_LOCK.release()
        except SubtitleError as error:
            # Only known, safe static errors (no URLs or HTTP response bodies).
            if "catálogo" in str(error):
                add_event(job, "episodio_no_identificado")
            elif "cifrada" in str(error):
                add_event(job, "pista_cifrada_no_admitida")
            else:
                add_event(job, "sin_subtitulo_accesible_o_sesion")
        except Exception:
            add_event(job, "error_en_extraccion")
        finally:
            QUEUE.task_done()


class Handler(BaseHTTPRequestHandler):
    def respond(self, status, obj):
        data = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def authorized(self):
        provided = self.headers.get("X-Bridge-Key", "")
        expected = os.environ["BRIDGE_API_TOKEN"]
        if not secrets.compare_digest(provided, expected):
            self.respond(401, {"error": "No autorizado"})
            return False
        return True

    def do_GET(self):
        if not self.authorized(): return
        if self.path in ("/health", "/v1/status"):
            self.respond(200, snapshot())
        else:
            self.respond(404, {"error": "Ruta no encontrada"})

    def respond_srt(self, data: bytes):
        self.send_response(200)
        self.send_header("Content-Type", "application/x-subrip")
        self.send_header("Cache-Control", "private, no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        if not self.authorized(): return
        if self.path not in ("/v1/login", "/v1/scan",
                             "/v1/provider/lookup", "/v1/provider/download"):
            self.respond(404, {"error": "Ruta no encontrada"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= MAX_JSON: raise SubtitleError("Solicitud demasiado grande")
            obj = json.loads(self.rfile.read(length))
            if not isinstance(obj, dict): raise SubtitleError("JSON inválido")
            if self.path == "/v1/provider/lookup":
                self.respond(200, provider_lookup(obj))
            elif self.path == "/v1/provider/download":
                identifier = obj.get("id")
                content = get_cached(identifier)
                if content is None:
                    self.respond(404, {"error": "SRT todavía no disponible"})
                else:
                    self.respond_srt(content)
            elif self.path == "/v1/login":
                self.respond(202, start_session(obj.get("provider")))
            else:
                self.respond(202, enqueue(obj.get("jobs"), obj.get("bazarr_url"),
                                          obj.get("bazarr_api_key")))
        except (ValueError, TypeError, SubtitleError):
            self.respond(400, {"error": "Solicitud inválida, credenciales ausentes o navegador ocupado"})

    def log_message(self, *args):
        # Avoid logging authentication headers or user-controlled request paths.
        pass


def main():
    token = os.environ.get("BRIDGE_API_TOKEN", "")
    if len(token) < 32:
        raise SystemExit("BRIDGE_API_TOKEN debe contener al menos 32 caracteres")
    PROFILE.mkdir(parents=True, exist_ok=True)
    load_previous()
    threading.Thread(target=worker, daemon=True, name="subtitle-worker").start()
    ThreadingHTTPServer(("0.0.0.0", 8787), Handler).serve_forever()


if __name__ == "__main__":
    main()
