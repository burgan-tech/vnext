"""HTTP server implementing the v1 vNext Python runtime contract (stdlib only)."""

from __future__ import annotations

import concurrent.futures
import json
import os
import platform
import sys
import time
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict, Optional, Tuple

from . import CONTRACT_VERSION, RUNTIME_VERSION
from .registry import HandlerContext, call_handler, discover, get_handler, registered_handlers

MAX_TIMEOUT_MS = 50_000


class RuntimeConfig:
    def __init__(self) -> None:
        self.port = int(os.environ.get("VNEXT_PYTHON_PORT", "8080"))
        self.handlers_package = os.environ.get("VNEXT_PYTHON_HANDLERS", "handlers")
        self.max_workers = int(os.environ.get("VNEXT_PYTHON_MAX_WORKERS", "4"))
        self.max_request_bytes = int(os.environ.get("VNEXT_PYTHON_MAX_REQUEST_BYTES", str(4 * 1024 * 1024)))
        self.max_output_bytes = int(os.environ.get("VNEXT_PYTHON_MAX_OUTPUT_BYTES", str(4 * 1024 * 1024)))


class Runtime:
    """Executes contract requests; transport-independent so it can be tested without sockets."""

    def __init__(self, config: RuntimeConfig) -> None:
        self.config = config
        self._pool = concurrent.futures.ThreadPoolExecutor(
            max_workers=config.max_workers, thread_name_prefix="vnext-handler")

    def invoke(self, raw: bytes) -> Tuple[int, Dict[str, Any]]:
        started = time.monotonic()
        try:
            request = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            return error(HTTPStatus.BAD_REQUEST, "bad_request", f"request is not valid JSON: {exc}")

        if not isinstance(request, dict):
            return error(HTTPStatus.BAD_REQUEST, "bad_request", "request must be a JSON object")
        if str(request.get("contractVersion")) != CONTRACT_VERSION:
            return error(HTTPStatus.BAD_REQUEST, "unsupported_contract",
                         f"contractVersion {request.get('contractVersion')!r} is not supported (expected {CONTRACT_VERSION})")

        name = request.get("handler")
        fn = get_handler(name) if isinstance(name, str) else None
        if fn is None:
            return error(HTTPStatus.NOT_FOUND, "unknown_handler", f"handler {name!r} is not registered")

        timeout_ms = request.get("timeoutMs")
        if not isinstance(timeout_ms, int) or timeout_ms <= 0:
            timeout_ms = MAX_TIMEOUT_MS
        timeout_ms = min(timeout_ms, MAX_TIMEOUT_MS)

        context = HandlerContext.from_request(request.get("context") or {}, timeout_ms)
        future = self._pool.submit(call_handler, fn, request.get("input"), context)
        try:
            output = future.result(timeout=timeout_ms / 1000)
        except concurrent.futures.TimeoutError:
            # A Python thread cannot be killed: the call keeps its worker until it returns.
            # Size VNEXT_PYTHON_MAX_WORKERS and keep handlers inside their timeout.
            return error(HTTPStatus.GATEWAY_TIMEOUT, "timeout", f"handler did not finish within {timeout_ms} ms")
        except Exception as exc:  # noqa: BLE001 - every handler failure is a contract error
            return error(HTTPStatus.UNPROCESSABLE_ENTITY, "handler_error", f"{type(exc).__name__}: {exc}")

        try:
            encoded_output = json.dumps(output, allow_nan=False)
        except (TypeError, ValueError) as exc:
            return error(HTTPStatus.UNPROCESSABLE_ENTITY, "invalid_output",
                         f"handler result is not strict JSON ({exc}); convert NumPy/pandas values with "
                         ".tolist(), .item() or .to_dict()")
        if len(encoded_output.encode("utf-8")) > self.config.max_output_bytes:
            return error(HTTPStatus.UNPROCESSABLE_ENTITY, "output_too_large",
                         f"handler result exceeds {self.config.max_output_bytes} bytes")

        return HTTPStatus.OK, {
            "contractVersion": CONTRACT_VERSION,
            "status": "success",
            "output": json.loads(encoded_output),
            "durationMs": int((time.monotonic() - started) * 1000),
        }

    def manifest(self) -> Dict[str, Any]:
        return {
            "contractVersion": CONTRACT_VERSION,
            "runtimeVersion": RUNTIME_VERSION,
            "pythonVersion": platform.python_version(),
            "handlers": sorted(registered_handlers()),
        }


def error(status: HTTPStatus, code: str, message: str) -> Tuple[int, Dict[str, Any]]:
    return status, {"contractVersion": CONTRACT_VERSION, "status": "error",
                    "error": {"code": code, "message": message}}


def make_handler(runtime: Runtime):
    class Handler(BaseHTTPRequestHandler):
        server_version = f"vnext-python-runtime/{RUNTIME_VERSION}"

        def do_POST(self) -> None:  # noqa: N802 - http.server naming
            if self.path.rstrip("/") != "/v1/invoke":
                self._send(*error(HTTPStatus.NOT_FOUND, "not_found", self.path))
                return
            length = int(self.headers.get("Content-Length") or 0)
            if length > runtime.config.max_request_bytes:
                self._send(*error(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "request_too_large",
                                  f"request exceeds {runtime.config.max_request_bytes} bytes"))
                return
            self._send(*runtime.invoke(self.rfile.read(length)))

        def do_GET(self) -> None:  # noqa: N802
            path = self.path.rstrip("/")
            if path == "/v1/health":
                self._send(HTTPStatus.OK, {"status": "ok"})
            elif path == "/v1/manifest":
                self._send(HTTPStatus.OK, runtime.manifest())
            else:
                self._send(*error(HTTPStatus.NOT_FOUND, "not_found", self.path))

        def _send(self, status: int, body: Dict[str, Any]) -> None:
            payload = json.dumps(body).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, fmt: str, *args: Any) -> None:
            # Access lines only; payloads are never logged.
            sys.stderr.write("%s %s\n" % (self.address_string(), fmt % args))

    return Handler


def serve(config: Optional[RuntimeConfig] = None) -> None:
    config = config or RuntimeConfig()
    discover(config.handlers_package)
    runtime = Runtime(config)
    server = ThreadingHTTPServer(("0.0.0.0", config.port), make_handler(runtime))
    sys.stderr.write(f"vnext-python-runtime {RUNTIME_VERSION} listening on :{config.port}, "
                     f"handlers: {', '.join(runtime.manifest()['handlers']) or '(none)'}\n")
    server.serve_forever()
