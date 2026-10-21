FROM mcr.microsoft.com/playwright/python:v1.56.0-noble

ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 DISPLAY=:99
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    xvfb x11vnc openbox novnc websockify && rm -rf /var/lib/apt/lists/* \
    && python -m pip install --no-cache-dir playwright==1.56.0
WORKDIR /workspace
COPY extractor/__init__.py extractor/hls.py extractor/disney.py extractor/bazarr.py extractor/main.py extractor/providers.py extractor/server.py extractor/provider_bridge.py extractor/episode_discovery.py extractor/entrypoint.sh extractor/entrypoint-agent.sh /workspace/extractor/
RUN chmod +x /workspace/extractor/entrypoint.sh
ENV BROWSER_PROFILE=/data/browser OUTPUT_DIR=/data/output
EXPOSE 6080
CMD ["/workspace/extractor/entrypoint.sh"]
