import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('agent_diagnostics', Path(__file__).parents[1] / 'agent_diagnostics.py')
diagnostics = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnostics)


class DiagnosticHypothesesTests(unittest.TestCase):
    def test_same_failure_requires_three_occurrences_and_separates_goals(self):
        def row(goal=1, outcome='failed'):
            return dict(kind='step.after', tool='interact', execution=dict(ObjectiveRevision=goal, outcome=outcome, reason='MissingTool'))
        self.assertEqual(diagnostics.hypotheses([row(), row(), row(2)]), [])
        self.assertEqual(len(diagnostics.hypotheses([row(), row(), row()])), 1)
        self.assertEqual(diagnostics.hypotheses([row(), row(), row(outcome='completed'), row()]), [])

    def test_reference_rounds_do_not_trigger_but_repeated_turns_do(self):
        request = dict(kind='model.request.completed')
        self.assertEqual(diagnostics.hypotheses([request] * 4), [])
        self.assertEqual(len(diagnostics.hypotheses([request] * 8)), 1)
        self.assertEqual(diagnostics.hypotheses([request] * 4 + [dict(kind='step.sending')] + [request] * 4), [])


if __name__ == '__main__':
    unittest.main()
