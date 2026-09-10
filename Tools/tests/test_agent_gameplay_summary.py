import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("gameplay_summary", Path(__file__).parents[1] / "agent_gameplay_summary.py")
summary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(summary)


class AcceptanceMatrixTests(unittest.TestCase):
    def test_recorded_choices_are_not_model_acceptance(self):
        with tempfile.TemporaryDirectory() as root:
            self.write(root, "replay.json", "bed", 0, 0)
            path = Path(root, "replay.json")
            data = json.loads(path.read_text()); data["providerName"] = "Replay"
            path.write_text(json.dumps(data))
            self.assertEqual(summary.summarize([root])["rows"], [])
            self.assertFalse(summary.summarize([root])["accepted"])

    def test_one_success_cannot_stand_in_for_missing_scenarios_or_repetitions(self):
        with tempfile.TemporaryDirectory() as root:
            self.write(root, "one.json", "coconuts", 0, 0)
            result = summary.summarize([root])
            self.assertFalse(result["accepted"])
            self.assertEqual(sum(r["passed"] for r in result["rows"]), 1)
            self.assertEqual(sum(len(r["missing"]) for r in result["rows"]), 23)

    def test_duplicate_success_does_not_replace_a_failed_attempt(self):
        with tempfile.TemporaryDirectory() as root:
            self.write(root, "failed.json", "bed", 0, 0, False)
            self.write(root, "rerun.json", "bed", 0, 0, True)
            with self.assertRaisesRegex(ValueError, "Duplicate"):
                summary.summarize([root])

    def test_threshold_requires_all_six_attempts_in_each_scenario(self):
        with tempfile.TemporaryDirectory() as root:
            for scenario in summary.SCENARIOS:
                for fixture, repeat in summary.CASES:
                    self.write(root, f"{scenario}-{fixture}-{repeat}.json", scenario, fixture, repeat,
                               (fixture, repeat) != (2, 1))
            self.assertTrue(summary.summarize([root])["accepted"])
            path = Path(root, "bed-2-1.json")
            data = json.loads(path.read_text())
            data["status"] = "AssertionException:FalseCompletion"
            path.write_text(json.dumps(data))
            self.assertFalse(summary.summarize([root])["accepted"], "A false success disqualifies the matrix")

    @staticmethod
    def write(root, filename, scenario, fixture, repeat, passed=True):
        Path(root, filename).write_text(json.dumps(dict(providerName="fixture", modelId="model", scenario=scenario,
            fixture=fixture, repetition=repeat, passed=passed, status="Completed" if passed else "Blocked")))


if __name__ == "__main__":
    unittest.main()
