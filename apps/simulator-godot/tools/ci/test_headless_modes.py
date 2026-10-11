"""Regression checks for CI outcome handling; runner outcomes are injected, not real Godot hangs.

    python3 -m unittest discover -s apps/simulator-godot/tools/ci -p 'test_*.py'
"""
import contextlib
import importlib.util
import io
import json
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


CI = Path(__file__).resolve().parent
CHARACTER = "character-smoke-test"
AUDIO = "audio-facade-test"


class HeadlessModesOutcomeTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("headless_modes", CI / "headless-modes.py")
        self.runner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.runner)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.reference = self.directory / "reference"
        self.out = self.directory / "out"
        committed = CI / "reference" / "godot-mac"
        for name in (CHARACTER, AUDIO):
            shutil.copytree(committed / name, self.reference / name)
        for name in ("runs.json", "unstable.json"):
            shutil.copyfile(committed / name, self.reference / name)
        self.summaries = []

    def snapshot_reference(self):
        return {path.relative_to(self.reference): path.read_bytes()
                for path in self.reference.rglob("*") if path.is_file()}

    def run_modes(self, outcomes, update=False, mark_unstable=False):
        """Each outcome is (exit code, timed out, write reports, change report)."""
        def run_mode(cmd, out, name, flag, env, timeout):
            code, timed_out, write_reports, change = outcomes[name]
            directory = out / name
            directory.mkdir(parents=True)
            if write_reports:
                for report in (self.reference / name).glob("*.json"):
                    value = json.loads(report.read_text())
                    if change:
                        value["reviewProbe"] = "new report result"
                    (directory / report.name).write_text(json.dumps(value))
            log = out / f"{name}.log"
            log.write_text("Injected runner outcome for CI regression test.\n")
            return code, timed_out, timeout if timed_out else 1, log

        argv = ["headless-modes.py", "--godot", "mock-godot", "--out", str(self.out),
                "--only", ",".join(outcomes)]
        if update:
            argv.append("--update-reference")
        if mark_unstable:
            argv.append("--mark-unstable")
        with patch.object(self.runner, "GODOT_MAC", self.reference), \
                patch.object(self.runner, "run_mode", run_mode), \
                patch.object(self.runner, "append_summary", self.summaries.append), \
                patch("sys.argv", argv), contextlib.redirect_stdout(io.StringIO()):
            code = self.runner.main()
        return code, json.loads((self.out / "headless-modes.json").read_text())

    def test_timeout_after_complete_reports_fails(self):
        code, results = self.run_modes({CHARACTER: (None, True, True, False)})
        self.assertEqual(code, 1)
        self.assertTrue(results[0]["timedOut"])
        self.assertTrue(all(not report.get("missing") for report in results[0]["reports"]))
        self.assertIn("after writing its reports", results[0]["failure"])
        self.assertIn("**FAIL**", self.summaries[0])
        self.assertNotIn("quit cleanly", self.summaries[0])

    def test_timeout_before_reports_fails(self):
        code, results = self.run_modes({CHARACTER: (None, True, False, False)})
        self.assertEqual(code, 1)
        self.assertEqual(results[0]["failure"], "timed out without its reports")

    def test_clean_completion_passes(self):
        code, results = self.run_modes({CHARACTER: (0, False, True, False)})
        self.assertEqual(code, 0)
        self.assertNotIn("failure", results[0])
        self.assertIn("quit cleanly", self.summaries[0])

    def test_report_difference_remains_informational(self):
        code, results = self.run_modes({CHARACTER: (0, False, True, True)})
        self.assertEqual(code, 0)
        self.assertNotIn("failure", results[0])
        self.assertTrue(results[0]["reports"][0]["godotMac"]["differences"])
        self.assertIn("**FINDING**", self.summaries[0])

    def test_expected_nonzero_exit_remains_accepted(self):
        runs = json.loads((self.reference / "runs.json").read_text())
        runs[CHARACTER]["exit"] = 1
        (self.reference / "runs.json").write_text(json.dumps(runs))
        code, results = self.run_modes({CHARACTER: (1, False, True, False)})
        self.assertEqual(code, 0)
        self.assertNotIn("failure", results[0])

    def test_unexpected_nonzero_exit_fails(self):
        code, results = self.run_modes({CHARACTER: (1, False, True, False)})
        self.assertEqual(code, 1)
        self.assertIn("exit code 1", results[0]["failure"])

    def test_timeout_cannot_replace_reference_after_reports(self):
        before = self.snapshot_reference()
        code, _ = self.run_modes({CHARACTER: (None, True, True, True)}, update=True)
        self.assertEqual(code, 1)
        self.assertEqual(self.snapshot_reference(), before)

    def test_timeout_cannot_replace_reference_before_reports(self):
        before = self.snapshot_reference()
        code, _ = self.run_modes({CHARACTER: (None, True, False, False)}, update=True)
        self.assertEqual(code, 1)
        self.assertEqual(self.snapshot_reference(), before)

    def test_timeout_cannot_mark_unstable_keys(self):
        before = self.snapshot_reference()
        code, _ = self.run_modes({CHARACTER: (None, True, True, True)},
                                 mark_unstable=True)
        self.assertEqual(code, 1)
        self.assertEqual(self.snapshot_reference(), before)

    def test_timeout_cannot_update_reference_and_mark_unstable_keys(self):
        before = self.snapshot_reference()
        code, _ = self.run_modes({CHARACTER: (None, True, True, True)},
                                 update=True, mark_unstable=True)
        self.assertEqual(code, 1)
        self.assertEqual(self.snapshot_reference(), before)

    def test_later_timeout_prevents_partial_reference_update(self):
        before = self.snapshot_reference()
        code, _ = self.run_modes({AUDIO: (0, False, True, True),
                                  CHARACTER: (None, True, True, True)}, update=True)
        self.assertEqual(code, 1)
        self.assertEqual(self.snapshot_reference(), before)

    def test_clean_reference_update_publishes_reports_and_exit(self):
        code, _ = self.run_modes({CHARACTER: (0, False, True, True)}, update=True)
        self.assertEqual(code, 0)
        report = json.loads((self.reference / CHARACTER / "characters.json").read_text())
        self.assertEqual(report["reviewProbe"], "new report result")
        runs = json.loads((self.reference / "runs.json").read_text())
        self.assertEqual(runs[CHARACTER], {"exit": 0, "seconds": 1})
        self.assertFalse((self.reference / "unstable.json").exists())


if __name__ == "__main__":
    unittest.main()
