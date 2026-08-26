from __future__ import annotations

import json
import subprocess
import sys
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from vnext_runner import execute_json  # noqa: E402


def execute(script: str, input_value=None, allowed_modules=None, **limits):
    request = {
        "script": script,
        "location": "test.py",
        "input": input_value,
        "allowedModules": allowed_modules or ["*"],
        "maxOutputBytes": limits.get("max_output", 2 * 1024 * 1024),
        "maxStdoutBytes": limits.get("max_stdout", 32 * 1024),
        "maxStderrBytes": limits.get("max_stderr", 32 * 1024),
    }
    return json.loads(execute_json(json.dumps(request)))


class VNextRunnerTests(unittest.TestCase):
    def test_cli_separates_native_stdout_from_protocol(self):
        request = {
            "script": "import os\ndef main(input): os.write(1, b'native\\n'); return input",
            "location": "native.py",
            "input": {"ok": True},
            "allowedModules": ["*"],
            "maxOutputBytes": 1024,
            "maxStdoutBytes": 1024,
            "maxStderrBytes": 1024,
        }
        completed = subprocess.run(
            [sys.executable, "-I", str(Path(__file__).resolve().parents[1] / "runner.py")],
            input=json.dumps(request),
            text=True,
            capture_output=True,
            check=True,
        )

        response = json.loads(completed.stdout)
        self.assertTrue(response["success"])
        self.assertEqual("native\n", response["stdout"])
        self.assertEqual({"ok": True}, json.loads(response["outputJson"]))

    def test_cli_reads_exact_stdin_byte_count_for_kubernetes_attach(self):
        request = {
            "script": "def main(input): return input",
            "location": "attach.py",
            "input": {"message": "T\u00fcrk\u00e7e"},
            "allowedModules": ["*"],
            "maxOutputBytes": 1024,
            "maxStdoutBytes": 1024,
            "maxStderrBytes": 1024,
        }
        payload = json.dumps(request, ensure_ascii=False).encode("utf-8")
        completed = subprocess.run(
            [
                sys.executable,
                "-I",
                str(Path(__file__).resolve().parents[1] / "runner.py"),
                "--stdin-bytes",
                str(len(payload)),
            ],
            input=payload,
            capture_output=True,
            check=True,
        )

        response = json.loads(completed.stdout)
        self.assertTrue(response["success"])
        self.assertEqual({"message": "T\u00fcrk\u00e7e"}, json.loads(response["outputJson"]))

    def test_nested_json_and_stdout(self):
        result = execute(
            "def main(input):\n    print('ran')\n    return {'items': input['items'], 'ok': True}",
            {"items": [1, {"two": 2}]},
        )

        self.assertTrue(result["success"])
        self.assertEqual({"items": [1, {"two": 2}], "ok": True}, json.loads(result["outputJson"]))
        self.assertEqual("ran\n", result["stdout"])

    def test_missing_main(self):
        missing = execute("value = 1")
        non_callable = execute("main = 42")
        self.assertFalse(missing["success"])
        self.assertEqual("EntryPointError", missing["exceptionType"])
        self.assertEqual("EntryPointError", non_callable["exceptionType"])

    def test_syntax_error(self):
        result = execute("def main(: pass")
        self.assertFalse(result["success"])
        self.assertEqual("SyntaxError", result["exceptionType"])

    def test_python_exception_is_normalized(self):
        result = execute("def main(input):\n    raise ValueError('bad model')")
        self.assertFalse(result["success"])
        self.assertEqual("ValueError", result["exceptionType"])
        self.assertEqual("bad model", result["error"])

    def test_non_json_output_and_nan_are_rejected(self):
        non_json = execute("def main(input):\n    return {1, 2}")
        nan = execute("def main(input):\n    return float('nan')")

        self.assertEqual("OutputSerializationError", non_json["exceptionType"])
        self.assertEqual("OutputSerializationError", nan["exceptionType"])

    def test_output_and_stdout_limits(self):
        output = execute("def main(input):\n    return 'x' * 100", max_output=20)
        stdout = execute(
            "def main(input):\n    print('x' * 100)\n    return None",
            max_stdout=10,
        )

        self.assertEqual("OutputLimitError", output["exceptionType"])
        self.assertTrue(stdout["success"])
        self.assertTrue(stdout["stdoutTruncated"])
        self.assertLessEqual(len(stdout["stdout"].encode("utf-8")), 10)

    def test_stderr_limit_is_independent(self):
        result = execute(
            "import sys\ndef main(input):\n    print('e' * 100, file=sys.stderr)\n    return None",
            max_stdout=20,
            max_stderr=9,
        )

        self.assertTrue(result["success"])
        self.assertEqual("", result["stdout"])
        self.assertTrue(result["stderrTruncated"])
        self.assertLessEqual(len(result["stderr"].encode("utf-8")), 9)

    def test_import_policy_checks_static_and_dynamic_imports(self):
        static = execute(
            "import os\ndef main(input): return None",
            allowed_modules=["json"],
        )
        dynamic = execute(
            "def main(input):\n    __import__('os')\n    return None",
            allowed_modules=["json"],
        )

        self.assertEqual("ImportPolicyError", static["exceptionType"])
        self.assertEqual("ImportPolicyError", dynamic["exceptionType"])

    def test_numpy_pandas_and_sklearn_contract(self):
        result = execute(
            """
import numpy as np
import pandas as pd
from sklearn.linear_model import LinearRegression

def main(input):
    frame = pd.DataFrame(input)
    model = LinearRegression().fit(frame[["x"]], frame["y"])
    prediction = model.predict(np.array([[4.0]])).tolist()
    return {"rows": frame.to_dict(orient="records"), "prediction": prediction}
""",
            {"x": [1.0, 2.0, 3.0], "y": [2.0, 4.0, 6.0]},
        )

        self.assertTrue(result["success"], result)
        output = json.loads(result["outputJson"])
        self.assertEqual(3, len(output["rows"]))
        self.assertAlmostEqual(8.0, output["prediction"][0], places=5)


if __name__ == "__main__":
    unittest.main()
