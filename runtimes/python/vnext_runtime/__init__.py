"""vNext Python runtime: serves domain handlers to the vNext Orchestration host.

A domain registers handlers with ``@handler("name")`` and ships them in its own image built
``FROM ghcr.io/burgan-tech/vnext/python-runtime:<version>``. The Orchestration host calls
``POST /v1/invoke`` over Dapr service invocation; see docs/runtime/python-task.md for the contract.
"""

from .registry import HandlerContext, handler, registered_handlers

CONTRACT_VERSION = "1"
RUNTIME_VERSION = "1.0.0"

__all__ = ["CONTRACT_VERSION", "RUNTIME_VERSION", "HandlerContext", "handler", "registered_handlers"]
