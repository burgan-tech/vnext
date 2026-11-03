# Example domain image: pinned requirements and handlers on top of the vNext base image.
ARG VNEXT_PYTHON_RUNTIME=ghcr.io/burgan-tech/vnext/python-runtime:latest
FROM ${VNEXT_PYTHON_RUNTIME}

USER root
COPY requirements.txt /tmp/requirements.txt
# Wheels only (no setup scripts at build time) and hash-locked: generate requirements.txt with
# pip-compile --generate-hashes.
RUN pip install --no-cache-dir --only-binary :all: --require-hashes -r /tmp/requirements.txt \
    && rm /tmp/requirements.txt
COPY handlers /opt/vnext-python/app/handlers
USER 65532:65532
