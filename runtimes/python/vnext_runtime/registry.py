"""Handler registration and discovery."""

from __future__ import annotations

import importlib
import inspect
import pkgutil
import re
from dataclasses import dataclass
from typing import Any, Callable, Dict, Optional

HANDLER_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_.-]{0,127}$")

_handlers: Dict[str, Callable[..., Any]] = {}


@dataclass(frozen=True)
class HandlerContext:
    """Read-only call context the Orchestration host sends with every invocation."""

    domain: str = ""
    workflow: str = ""
    workflow_version: Optional[str] = None
    instance_id: str = ""
    task_key: str = ""
    correlation_id: Optional[str] = None
    timeout_ms: int = 0

    @staticmethod
    def from_request(context: Dict[str, Any], timeout_ms: int) -> "HandlerContext":
        return HandlerContext(
            domain=str(context.get("domain") or ""),
            workflow=str(context.get("workflow") or ""),
            workflow_version=context.get("workflowVersion"),
            instance_id=str(context.get("instanceId") or ""),
            task_key=str(context.get("taskKey") or ""),
            correlation_id=context.get("correlationId"),
            timeout_ms=timeout_ms,
        )


def handler(name: str) -> Callable[[Callable[..., Any]], Callable[..., Any]]:
    """Registers ``fn(input)`` or ``fn(input, context)`` under ``name``."""

    if not HANDLER_NAME.match(name):
        raise ValueError(f"invalid handler name {name!r}")

    def register(fn: Callable[..., Any]) -> Callable[..., Any]:
        if name in _handlers and _handlers[name] is not fn:
            raise ValueError(f"handler {name!r} is registered twice")
        _handlers[name] = fn
        return fn

    return register


def registered_handlers() -> Dict[str, Callable[..., Any]]:
    return dict(_handlers)


def get_handler(name: str) -> Optional[Callable[..., Any]]:
    return _handlers.get(name)


def call_handler(fn: Callable[..., Any], payload: Any, context: HandlerContext) -> Any:
    accepts_context = len(inspect.signature(fn).parameters) >= 2
    return fn(payload, context) if accepts_context else fn(payload)


def discover(package: str) -> None:
    """Imports ``package`` and every module below it so their ``@handler`` decorators run."""

    root = importlib.import_module(package)
    for module in pkgutil.walk_packages(getattr(root, "__path__", []), prefix=f"{package}."):
        importlib.import_module(module.name)


def clear() -> None:
    """Test hook."""

    _handlers.clear()
