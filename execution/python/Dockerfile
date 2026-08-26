FROM python:3.12.13-slim-bookworm

ENV PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    OMP_NUM_THREADS=1 \
    OPENBLAS_NUM_THREADS=1 \
    MKL_NUM_THREADS=1

WORKDIR /opt/vnext-python

COPY execution/python/requirements.lock ./requirements.lock
RUN python -m pip install --no-cache-dir --only-binary=:all: --require-hashes -r requirements.lock \
    && python -c "import numpy, pandas, sklearn" \
    && python -m pip uninstall -y pip setuptools wheel

COPY execution/python/runner.py execution/python/vnext_runner.py ./

RUN groupadd --gid 65532 vnext-python \
    && useradd --uid 65532 --gid 65532 --no-create-home --home-dir /nonexistent vnext-python \
    && chown -R 65532:65532 /opt/vnext-python

USER 65532:65532
ENTRYPOINT ["python", "-I", "/opt/vnext-python/runner.py"]
