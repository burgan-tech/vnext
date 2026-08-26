"""Shared JSON protocol for every vNext Python execution mode.

Workflow code is trusted. The optional import allow-list is a governance control,
not a security sandbox; trusted code can deliberately escape Python-level hooks.
"""

from __future__ import annotations

import ast
import builtins
import contextlib
import io
import json
import platform
from typing import Any


class EntryPointError(Exception):
    pass


class OutputSerializationError(Exception):
    pass


class OutputLimitError(Exception):
    pass


class ImportPolicyError(Exception):
    pass


class _LimitedTextBuffer(io.StringIO):
    def __init__(self, limit_bytes: int) -> None:
        super().__init__()
        self._limit_bytes = limit_bytes
        self._size_bytes = 0
        self.truncated = False

    def write(self, value: str) -> int:
        encoded = value.encode("utf-8")
        remaining = self._limit_bytes - self._size_bytes
        if remaining <= 0:
            self.truncated = True
            return len(value)

        accepted = encoded[:remaining]
        while accepted:
            try:
                decoded = accepted.decode("utf-8")
                break
            except UnicodeDecodeError:
                accepted = accepted[:-1]
        else:
            decoded = ""

        super().write(decoded)
        self._size_bytes += len(accepted)
        if len(accepted) < len(encoded):
            self.truncated = True
        return len(value)


def _allowed_root(module_name: str, allowed_modules: list[str]) -> bool:
    if "*" in allowed_modules:
        return True
    return module_name.split(".", 1)[0] in set(allowed_modules)


def _validate_static_imports(source: str, location: str, allowed_modules: list[str]) -> None:
    if "*" in allowed_modules:
        return

    tree = ast.parse(source, filename=location, mode="exec")
    for node in ast.walk(tree):
        names: list[str] = []
        if isinstance(node, ast.Import):
            names = [alias.name for alias in node.names]
        elif isinstance(node, ast.ImportFrom) and node.module:
            names = [node.module]

        for name in names:
            if not _allowed_root(name, allowed_modules):
                raise ImportPolicyError(f"Import of module '{name}' is not allowed.")


def _build_builtins(allowed_modules: list[str]) -> dict[str, Any]:
    scoped_builtins = dict(vars(builtins))
    original_import = builtins.__import__

    def guarded_import(
        name: str,
        globals_: dict[str, Any] | None = None,
        locals_: dict[str, Any] | None = None,
        fromlist: tuple[str, ...] = (),
        level: int = 0,
    ) -> Any:
        if level == 0 and not _allowed_root(name, allowed_modules):
            raise ImportPolicyError(f"Import of module '{name}' is not allowed.")
        return original_import(name, globals_, locals_, fromlist, level)

    scoped_builtins["__import__"] = guarded_import
    return scoped_builtins


def execute_request(request: dict[str, Any]) -> dict[str, Any]:
    stdout = _LimitedTextBuffer(int(request["maxStdoutBytes"]))
    stderr = _LimitedTextBuffer(int(request["maxStderrBytes"]))
    runtime_version = platform.python_version()

    try:
        source = request["script"]
        location = request.get("location") or "inline"
        allowed_modules = request.get("allowedModules") or ["*"]
        _validate_static_imports(source, location, allowed_modules)

        scope: dict[str, Any] = {
            "__name__": "__vnext_workflow__",
            "__builtins__": _build_builtins(allowed_modules),
        }

        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            exec(compile(source, location, "exec"), scope, scope)
            entrypoint = scope.get("main")
            if not callable(entrypoint):
                raise EntryPointError("Python script must define a callable main(input).")
            output = entrypoint(request.get("input"))

        try:
            output_json = json.dumps(
                output,
                allow_nan=False,
                ensure_ascii=False,
                separators=(",", ":"),
            )
        except (TypeError, ValueError) as exc:
            raise OutputSerializationError(
                "Python main(input) must return a strict JSON-serializable value."
            ) from exc

        if len(output_json.encode("utf-8")) > int(request["maxOutputBytes"]):
            raise OutputLimitError("Python output exceeds the configured size limit.")

        return {
            "success": True,
            "outputJson": output_json,
            "stdout": stdout.getvalue(),
            "stderr": stderr.getvalue(),
            "runtimeVersion": runtime_version,
            "stdoutTruncated": stdout.truncated,
            "stderrTruncated": stderr.truncated,
        }
    except BaseException as exc:  # runner must normalize SystemExit and other script failures
        return {
            "success": False,
            "error": str(exc) or type(exc).__name__,
            "exceptionType": type(exc).__name__,
            "stdout": stdout.getvalue(),
            "stderr": stderr.getvalue(),
            "runtimeVersion": runtime_version,
            "stdoutTruncated": stdout.truncated,
            "stderrTruncated": stderr.truncated,
        }


def execute_json(request_json: str) -> str:
    try:
        request = json.loads(request_json)
        response = execute_request(request)
    except BaseException as exc:
        response = {
            "success": False,
            "error": str(exc) or type(exc).__name__,
            "exceptionType": type(exc).__name__,
            "stdout": "",
            "stderr": "",
            "runtimeVersion": platform.python_version(),
            "stdoutTruncated": False,
            "stderrTruncated": False,
        }
    return json.dumps(response, allow_nan=False, ensure_ascii=False, separators=(",", ":"))
