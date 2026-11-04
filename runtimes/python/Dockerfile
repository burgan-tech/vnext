# vNext Python runtime base image. Domains build their own image FROM this one and add their
# handlers and pinned requirements; nothing is installed at container start.
FROM python:3.12.13-slim-bookworm

ENV PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PYTHONPATH=/opt/vnext-python:/opt/vnext-python/app \
    VNEXT_PYTHON_PORT=8080 \
    VNEXT_PYTHON_HANDLERS=handlers

WORKDIR /opt/vnext-python

COPY runtimes/python/vnext_runtime ./vnext_runtime

RUN groupadd --gid 65532 vnext-python \
    && useradd --uid 65532 --gid 65532 --no-create-home --home-dir /nonexistent vnext-python \
    && mkdir -p /opt/vnext-python/app/handlers \
    && touch /opt/vnext-python/app/handlers/__init__.py \
    && chmod -R a=rX /opt/vnext-python

USER 65532:65532
EXPOSE 8080
ENTRYPOINT ["python", "-m", "vnext_runtime"]
