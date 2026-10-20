"""Check embedded plugin configuration page JavaScript syntax in CI."""
from __future__ import annotations

from pathlib import Path
import subprocess
import tempfile

html = Path("Jellyfin.Plugin.SubtitleBridge/Configuration/configPage.html").read_text("utf-8")
start = html.index("<script>") + len("<script>")
end = html.index("</script>", start)
source = html[start:end]
with tempfile.TemporaryDirectory() as work:
    script = Path(work) / "moonfin-config.js"
    script.write_text(source, encoding="utf-8")
    subprocess.run(["node", "--check", str(script)], check=True)
print("Jellyfin plugin page: JavaScript syntax OK")
