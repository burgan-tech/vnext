#!/usr/bin/env python3
"""CLI wrapper for the vNext Python task JSON protocol."""

from __future__ import annotations

import argparse
import ctypes
import importlib.util
import json
import os
import sys
import tempfile
from pathlib import Path


_MODULE_PATH = Path(__file__).with_name("vnext_runner.py")
_SPEC = importlib.util.spec_from_file_location("vnext_runner", _MODULE_PATH)
if _SPEC is None or _SPEC.loader is None:
    raise RuntimeError(f"Unable to load Python runner module from {_MODULE_PATH}")
_MODULE = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(_MODULE)
execute_json = _MODULE.execute_json


def _read_request() -> str:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--input")
    parser.add_argument("--stdin-bytes", type=int)
    args = parser.parse_args()
    if args.input:
        with open(args.input, "r", encoding="utf-8") as input_file:
            return input_file.read()
    if args.stdin_bytes is not None:
        if args.stdin_bytes < 0:
            raise ValueError("--stdin-bytes cannot be negative")
        payload = sys.stdin.buffer.read(args.stdin_bytes)
        if len(payload) != args.stdin_bytes:
            raise EOFError(
                f"Expected {args.stdin_bytes} stdin bytes but received {len(payload)}"
            )
        return payload.decode("utf-8")
    return sys.stdin.read()


def _truncate_utf8(value: str, limit: int) -> tuple[str, bool]:
    encoded = value.encode("utf-8")
    if len(encoded) <= limit:
        return value, False
    accepted = encoded[:limit]
    while accepted:
        try:
            return accepted.decode("utf-8"), True
        except UnicodeDecodeError:
            accepted = accepted[:-1]
    return "", True


def _execute_with_native_output_capture(request_json: str) -> str:
    """Keep C-extension fd writes out of the JSON protocol stream."""
    request = json.loads(request_json)
    saved_stdout = os.dup(1)
    saved_stderr = os.dup(2)

    with tempfile.TemporaryFile() as native_stdout, tempfile.TemporaryFile() as native_stderr:
        try:
            sys.stdout.flush()
            sys.stderr.flush()
            os.dup2(native_stdout.fileno(), 1)
            os.dup2(native_stderr.fileno(), 2)
            response_json = execute_json(request_json)
            try:
                ctypes.CDLL(None).fflush(None)
            except (AttributeError, OSError):
                pass
            sys.stdout.flush()
            sys.stderr.flush()
        finally:
            os.dup2(saved_stdout, 1)
            os.dup2(saved_stderr, 2)
            os.close(saved_stdout)
            os.close(saved_stderr)

        native_stdout.seek(0)
        native_stderr.seek(0)
        stdout_text = native_stdout.read().decode("utf-8", errors="replace")
        stderr_text = native_stderr.read().decode("utf-8", errors="replace")

    response = json.loads(response_json)
    stdout, stdout_truncated = _truncate_utf8(
        response.get("stdout", "") + stdout_text,
        int(request["maxStdoutBytes"]),
    )
    stderr, stderr_truncated = _truncate_utf8(
        response.get("stderr", "") + stderr_text,
        int(request["maxStderrBytes"]),
    )
    response["stdout"] = stdout
    response["stderr"] = stderr
    response["stdoutTruncated"] = bool(response.get("stdoutTruncated")) or stdout_truncated
    response["stderrTruncated"] = bool(response.get("stderrTruncated")) or stderr_truncated
    return json.dumps(response, allow_nan=False, ensure_ascii=False, separators=(",", ":"))


sys.stdout.write(_execute_with_native_output_capture(_read_request()))
