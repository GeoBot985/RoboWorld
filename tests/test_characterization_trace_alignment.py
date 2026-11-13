import importlib.util
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "tools" / "evaluate-characterization-campaign.py"
SPEC = importlib.util.spec_from_file_location("campaign_evaluator", SCRIPT)
assert SPEC and SPEC.loader
EVALUATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(EVALUATOR)
SENSOR_SPEC = importlib.util.spec_from_file_location("sensor_evaluator", Path(__file__).parents[1] / "tools" / "evaluate-sensor-trace.py")
assert SENSOR_SPEC and SENSOR_SPEC.loader
SENSOR_EVALUATOR = importlib.util.module_from_spec(SENSOR_SPEC)
SENSOR_SPEC.loader.exec_module(SENSOR_EVALUATOR)


class RenodeAdvanceBracketTests(unittest.TestCase):
    def setUp(self):
        self.boundaries = [
            {"HostMonotonicTicks": 100, "SimTimeNs": 10_000_000},
            {"HostMonotonicTicks": 200, "SimTimeNs": 20_000_000},
            {"HostMonotonicTicks": 300, "SimTimeNs": 30_000_000},
        ]

    def test_input_is_bracketed_by_adjacent_completed_renode_advances(self):
        self.assertEqual(EVALUATOR.renode_advance_bracket(250, self.boundaries), (20_000_000, 30_000_000))

    def test_boundary_timestamp_uses_that_frontier_as_lower_bound(self):
        self.assertEqual(EVALUATOR.renode_advance_bracket(200, self.boundaries), (20_000_000, 30_000_000))

    def test_unbracketed_start_or_end_is_reported_unknown(self):
        self.assertIsNone(EVALUATOR.renode_advance_bracket(50, self.boundaries))
        self.assertIsNone(EVALUATOR.renode_advance_bracket(300, self.boundaries))

    def test_regressing_simulation_frontier_is_rejected(self):
        broken = [self.boundaries[0], {"HostMonotonicTicks": 200, "SimTimeNs": 5_000_000}]
        with self.assertRaises(ValueError):
            EVALUATOR.renode_advance_bracket(150, broken)


class CausalSensorMatchTests(unittest.TestCase):
    def setUp(self):
        self.samples = [
            {"SimTimeNs": 100, "DistanceMeters": 1.0},
            {"SimTimeNs": 200, "DistanceMeters": -1.0},
            {"SimTimeNs": 300, "DistanceMeters": 2.0},
        ]
        self.rises = [{"SimTimeNs": 100, "High": True}, {"SimTimeNs": 300, "High": True}]
        self.falls = [{"SimTimeNs": 110, "High": False}, {"SimTimeNs": 320, "High": False}]

    def test_uses_latest_echo_that_fell_before_the_authoritative_frontier(self):
        match = SENSOR_EVALUATOR.latest_completed_echo(self.samples, self.rises, self.falls, 250)
        self.assertEqual(match["sample"]["DistanceMeters"], 1.0)

    def test_does_not_use_a_future_or_no_hit_sensor_sample(self):
        self.assertIsNone(SENSOR_EVALUATOR.latest_completed_echo(self.samples, self.rises, self.falls, 105))

    def test_selects_new_sample_only_after_its_echo_fall(self):
        match = SENSOR_EVALUATOR.latest_completed_echo(self.samples, self.rises, self.falls, 320)
        self.assertEqual(match["sample"]["DistanceMeters"], 2.0)


class ComplexityAwareSelectionTests(unittest.TestCase):
    @staticmethod
    def events(output_for):
        return [{"sequence_id": f"train-{sequence}", "input": [value, value],
                 "output": [output_for(value), output_for(value)]}
                for sequence in range(4) for value in (-100, -50, 0, 50, 100)]

    def test_affine_wins_when_constant_fails_and_units_exclude_pass_through(self):
        selected, report = EVALUATOR.select_operator(self.events(lambda value: value / 100), 0)
        self.assertEqual(selected, "affine")
        self.assertFalse(report["constant"]["meets_cv_threshold"])
        self.assertEqual(report["not_considered"]["pass_through"],
                         "input percentage points and output signed fraction have different units")

    def test_simplest_qualifying_constant_wins_over_affine(self):
        selected, report = EVALUATOR.select_operator(self.events(lambda _: 0.25), 0)
        self.assertEqual(selected, "constant")
        self.assertTrue(report["affine"]["meets_cv_threshold"])


if __name__ == "__main__":
    unittest.main()
