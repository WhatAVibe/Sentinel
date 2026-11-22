FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1
WORKDIR /app

COPY server/requirements.txt /app/requirements.txt
RUN pip install --no-cache-dir -r /app/requirements.txt

COPY server/ /app/

RUN useradd --system --uid 10001 sentinel \
    && mkdir -p /data \
    && chown -R sentinel /data

USER sentinel
VOLUME ["/data"]
EXPOSE 8765

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8765/api/v1/health', timeout=4)"

CMD ["python", "server.py", "--config", "/data/config.json"]
