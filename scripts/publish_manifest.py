"""Add an existing GitHub Release ZIP to a Jellyfin plugin manifest.

Called only *after* GitHub Actions has successfully built and published the file.
No network access or credentials are needed for this script.
"""
import datetime as dt
import hashlib
import json
import os
from pathlib import Path

GUID = "b8e43169-c5f4-41d3-a9a0-8f4b9f85d792"
VERSION = "0.4.0.0"
ABI = "12.2.0.0"


def publish(manifest: Path, archive: Path, repo: str, tag: str):
    if not archive.is_file():
        raise FileNotFoundError(archive)
    checksum = hashlib.md5(archive.read_bytes()).hexdigest()
    versions = [{
        "version": VERSION,
        "changelog": "Conexión directa a Bazarr, Sonarr y Radarr; catálogo y preferencias en Jellyfin 12.2.",
        "targetAbi": ABI,
        "sourceUrl": f"https://github.com/{repo}/releases/download/{tag}/{archive.name}",
        "checksum": checksum,
        "timestamp": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
    }]
    entry = {
        "guid": GUID,
        "name": "Moonfin Subtitle Bridge",
        "description": "Selecciona series e idiomas y coordina subtítulos con Bazarr desde Jellyfin.",
        "overview": "Jellyfin + Bazarr + agente Disney+ experimental (servicio Docker separado).",
        "owner": repo.split("/")[0],
        "category": "Subtitles",
        "versions": versions,
    }
    current = json.loads(manifest.read_text(encoding="utf-8")) if manifest.exists() else []
    if current and not (len(current) == 1 and current[0].get("guid") == GUID):
        raise ValueError("El manifiesto actual contiene otros plugins; abortando.")
    # Preserve existing versions to support future upgrades. Replace only a matching release.
    if current:
        old = current[0].get("versions", [])
        entry["versions"] += [
            item for item in old
            if not (item.get("version") == VERSION and item.get("targetAbi") == ABI)
        ]
    manifest.write_text(json.dumps([entry], indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    publish(Path("manifest.json"), Path(os.environ["ARCHIVE"]), os.environ["GITHUB_REPOSITORY"], os.environ["TAG"])
