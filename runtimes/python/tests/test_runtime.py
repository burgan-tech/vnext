import json
import os
import sys
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from vnext_runtime import registry  # noqa: E402
from vnext_runtime.server import Runtime, RuntimeConfig  # noqa: E402


def request(handler, input=None, **extra):
    body = {"contractVersion": "1", "handler": handler, "input": input, "timeoutMs": 2000,
            "context": {"domain": "core", "workflow": "calc", "instanceId": "i-1", "taskKey": "t"}}
    body.update(extra)
    return json.dumps(body).encode()


class RuntimeContractTests(unittest.TestCase):
    def setUp(self):
        registry.clear()
        config = RuntimeConfig()
        config.max_output_bytes = 1024
        self.runtime = Runtime(config)

        @registry.handler("sum_values")
        def sum_values(input):
            return {"total": sum(input["values"])}

        @registry.handler("with_context")
        def with_context(input, context):
            return {"instance": context.instance_id, "domain": context.domain}

        @registry.handler("boom")
        def boom(input):
            raise ValueError("bad input")

        @registry.handler("nan")
        def nan(input):
            return float("nan")

        @registry.handler("slow")
        def slow(input):
            time.sleep(1)
            return {}

        @registry.handler("huge")
        def huge(input):
            return "x" * 2048

    def test_success_returns_handler_output(self):
        status, body = self.runtime.invoke(request("sum_values", {"values": [2, 3, 5]}))
        self.assertEqual(200, status)
        self.assertEqual("success", body["status"])
        self.assertEqual({"total": 10}, body["output"])

    def test_handler_receives_context(self):
        status, body = self.runtime.invoke(request("with_context", {}))
        self.assertEqual(200, status)
        self.assertEqual({"instance": "i-1", "domain": "core"}, body["output"])

    def test_unknown_handler_is_404(self):
        status, body = self.runtime.invoke(request("missing"))
        self.assertEqual(404, status)
        self.assertEqual("unknown_handler", body["error"]["code"])

    def test_handler_exception_is_422(self):
        status, body = self.runtime.invoke(request("boom"))
        self.assertEqual(422, status)
        self.assertEqual("handler_error", body["error"]["code"])
        self.assertIn("bad input", body["error"]["message"])

    def test_non_strict_json_output_is_rejected(self):
        status, body = self.runtime.invoke(request("nan"))
        self.assertEqual(422, status)
        self.assertEqual("invalid_output", body["error"]["code"])

    def test_output_limit(self):
        status, body = self.runtime.invoke(request("huge"))
        self.assertEqual(422, status)
        self.assertEqual("output_too_large", body["error"]["code"])

    def test_timeout_is_504(self):
        status, body = self.runtime.invoke(request("slow", timeoutMs=100))
        self.assertEqual(504, status)
        self.assertEqual("timeout", body["error"]["code"])

    def test_wrong_contract_version_is_400(self):
        status, body = self.runtime.invoke(request("sum_values", contractVersion="2"))
        self.assertEqual(400, status)
        self.assertEqual("unsupported_contract", body["error"]["code"])

    def test_invalid_json_is_400(self):
        status, _ = self.runtime.invoke(b"{not json")
        self.assertEqual(400, status)

    def test_manifest_lists_handlers(self):
        self.assertIn("sum_values", self.runtime.manifest()["handlers"])

    def test_invalid_handler_name_is_refused_at_registration(self):
        with self.assertRaises(ValueError):
            registry.handler("has space")


if __name__ == "__main__":
    unittest.main()
