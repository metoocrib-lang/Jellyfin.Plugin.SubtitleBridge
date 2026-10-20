#!/bin/sh
set -eu
: "${VNC_PASSWORD:?Set VNC_PASSWORD in your private extractor/.env}"
if [ "${#VNC_PASSWORD}" -lt 12 ]; then echo "VNC_PASSWORD must have 12+ characters" >&2; exit 1; fi
: "${BRIDGE_API_TOKEN:?Set BRIDGE_API_TOKEN in extractor/.env}"
if [ "${#BRIDGE_API_TOKEN}" -lt 32 ]; then echo "BRIDGE_API_TOKEN must have 32+ characters" >&2; exit 1; fi
x11vnc -storepasswd "$VNC_PASSWORD" /tmp/.vncpasswd >/dev/null 2>&1
chmod 600 /tmp/.vncpasswd
Xvfb :99 -screen 0 1366x900x24 -nolisten tcp >/tmp/bridge-xvfb.log 2>&1 &
sleep 2
openbox >/tmp/bridge-openbox.log 2>&1 &
x11vnc -display :99 -rfbport 5900 -localhost -forever -shared -rfbauth /tmp/.vncpasswd >/tmp/bridge-vnc.log 2>&1 &
websockify --web=/usr/share/novnc 0.0.0.0:6080 localhost:5900 >/tmp/bridge-novnc.log 2>&1 &
exec python -m extractor.server
