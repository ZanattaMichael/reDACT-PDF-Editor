#!/usr/bin/env python3
"""Tests for the SonarQube Cloud findings download (run: python3 scripts/test_fetch_sonar_issues.py).

The real API needs a token, so these drive the fetcher against a stand-in that reproduces the race
it has to survive: the analysis task reports SUCCESS while the issue search still returns the
previous analysis's issues for a moment.
"""

import contextlib
import io
import json
import os
import sys
import tempfile
import unittest
import urllib.error
import urllib.parse
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import fetch_sonar_issues  # noqa: E402

BASE = "https://sonarcloud.io"
PROJECT = "ZanattaMichael_Chromium-PDF-Editor"


class FakeSonar:
    """api/ce/task, api/measures/component and api/issues/search, answering from canned data.

    `searches` is what successive issue searches return, oldest first; the last one repeats.
    """

    def __init__(self, measures, searches):
        self.measures = measures
        self.searches = list(searches)
        self.urls = []

    def __call__(self, url, _token, _timeout=60):
        self.urls.append(url)
        parsed = urllib.parse.urlparse(url)
        query = urllib.parse.parse_qs(parsed.query)
        if parsed.path == "/api/ce/task":
            return {"task": {"status": "SUCCESS"}}
        if parsed.path == "/api/measures/component":
            if isinstance(self.measures, Exception):
                raise self.measures
            return {"component": {"key": PROJECT, "measures": self.measures}}
        if parsed.path == "/api/issues/search":
            issues = self.searches[0] if len(self.searches) == 1 else self.searches.pop(0)
            size = int(query["ps"][0])
            return {"total": len(issues), "paging": {"total": len(issues)}, "issues": issues[:size]}
        raise AssertionError(f"unexpected request: {url}")


def issue(rule):
    return {"key": rule, "rule": rule, "component": f"{PROJECT}:src/Foo.cs", "line": 1, "message": rule}


STALE = [issue(f"csharpsquid:S{n}") for n in range(7)]
FRESH = [issue("csharpsquid:S1751")]


class NoSleep(unittest.TestCase):
    def setUp(self):
        patcher = mock.patch.object(fetch_sonar_issues.time, "sleep")
        self.sleep = patcher.start()
        self.addCleanup(patcher.stop)


class AnalysisIssueCountTests(NoSleep):
    def count(self, measures):
        with mock.patch.object(fetch_sonar_issues, "get_json", FakeSonar(measures, [[]])):
            return fetch_sonar_issues.analysis_issue_count(BASE, PROJECT, "t", "179", None)

    def test_reads_the_violations_measure(self):
        self.assertEqual(self.count([{"metric": "violations", "value": "3"}]), 3)

    def test_prefers_violations_over_new_violations(self):
        self.assertEqual(self.count([{"metric": "new_violations", "period": {"value": "9"}},
                                     {"metric": "violations", "value": "3"}]), 3)

    def test_falls_back_to_new_violations_held_in_a_period(self):
        self.assertEqual(self.count([{"metric": "new_violations", "period": {"index": 1, "value": "2"}}]), 2)
        self.assertEqual(self.count([{"metric": "new_violations", "periods": [{"index": 1, "value": "4"}]}]), 4)

    def test_is_none_when_the_server_reports_no_count(self):
        self.assertIsNone(self.count([]))

    def test_is_none_when_the_measures_cannot_be_read(self):
        error = urllib.error.HTTPError(f"{BASE}/api/measures/component", 403, "Forbidden", {}, None)
        self.assertIsNone(self.count(error))

    def test_asks_for_the_pull_request_or_the_branch(self):
        fake = FakeSonar([{"metric": "violations", "value": "0"}], [[]])
        with mock.patch.object(fetch_sonar_issues, "get_json", fake):
            fetch_sonar_issues.analysis_issue_count(BASE, PROJECT, "t", "179", None)
            fetch_sonar_issues.analysis_issue_count(BASE, PROJECT, "t", None, "main")
        pr, branch = (urllib.parse.parse_qs(urllib.parse.urlparse(u).query) for u in fake.urls)
        self.assertEqual(pr["pullRequest"], ["179"])
        self.assertNotIn("branch", pr)
        self.assertEqual(branch["branch"], ["main"])


class WaitForIndexTests(NoSleep):
    def test_waits_until_the_search_agrees_with_the_analysis(self):
        fake = FakeSonar([], [STALE, STALE, FRESH])
        with mock.patch.object(fetch_sonar_issues, "get_json", fake):
            self.assertTrue(fetch_sonar_issues.wait_for_index(BASE, PROJECT, "t", "179", None, expected=1))
        self.assertEqual(len(fake.urls), 3)
        self.assertEqual(self.sleep.call_count, 2)

    def test_does_not_wait_when_the_search_already_agrees(self):
        fake = FakeSonar([], [FRESH])
        with mock.patch.object(fetch_sonar_issues, "get_json", fake):
            self.assertTrue(fetch_sonar_issues.wait_for_index(BASE, PROJECT, "t", "179", None, expected=1))
        self.sleep.assert_not_called()

    def test_gives_up_after_the_timeout(self):
        fake = FakeSonar([], [STALE])
        clock = range(0, 1000, 50)  # each reading of the clock is 50 s later
        with mock.patch.object(fetch_sonar_issues, "get_json", fake), \
                mock.patch.object(fetch_sonar_issues.time, "monotonic", side_effect=clock):
            self.assertFalse(fetch_sonar_issues.wait_for_index(BASE, PROJECT, "t", "179", None,
                                                               expected=1, timeout_seconds=120))
        self.assertGreater(len(fake.urls), 1)


class MainTests(NoSleep):
    """The whole download, in a scratch working tree with the scanner's report-task.txt."""

    def run_main(self, fake, extra_args=()):
        """Runs the download; returns its exit code, the rules it wrote, and what it printed to stdout.

        Stdout is captured because that is where workflow commands go: a `::warning::` that
        escaped from here would be shown as a warning on the real CI run.
        """
        with tempfile.TemporaryDirectory() as work:
            report = Path(work, ".sonarqube/out/.sonar/report-task.txt")
            report.parent.mkdir(parents=True)
            report.write_text(f"projectKey={PROJECT}\nserverUrl={BASE}\n"
                              f"ceTaskUrl={BASE}/api/ce/task?id=AX\n", encoding="utf-8")
            cwd = os.getcwd()
            os.chdir(work)
            try:
                stdout = io.StringIO()
                with mock.patch.object(fetch_sonar_issues, "get_json", fake), \
                        mock.patch.dict(os.environ, {"SONAR_TOKEN": "t"}), \
                        contextlib.redirect_stdout(stdout):
                    os.environ.pop("GITHUB_OUTPUT", None)  # restored when the patch ends
                    code = fetch_sonar_issues.main(
                        ["--output", "issues.json", "--pull-request", "179", *extra_args])
                written = json.loads(Path(work, "issues.json").read_text(encoding="utf-8"))
            finally:
                os.chdir(cwd)
        return code, [i["rule"] for i in written["issues"]], stdout.getvalue()

    def test_publishes_this_analysis_issues_not_the_previous_ones(self):
        fake = FakeSonar([{"metric": "violations", "value": "1"}], [STALE, STALE, FRESH])
        code, rules, stdout = self.run_main(fake)
        self.assertEqual(code, 0)
        self.assertEqual(rules, ["csharpsquid:S1751"])
        self.assertNotIn("::warning::", stdout)

    def test_still_publishes_when_the_search_never_catches_up(self):
        fake = FakeSonar([{"metric": "violations", "value": "1"}], [STALE])
        code, rules, stdout = self.run_main(fake, ["--index-timeout", "0"])
        self.assertEqual(code, 0)
        self.assertEqual(len(rules), 7)
        # ...and says so where the run summary shows it.
        self.assertIn("::warning::", stdout)
        self.assertIn("(1 open issues)", stdout)

    def test_reads_the_search_straight_away_when_there_is_no_count(self):
        fake = FakeSonar([], [FRESH])
        code, rules, _ = self.run_main(fake)
        self.assertEqual((code, rules), (0, ["csharpsquid:S1751"]))
        self.sleep.assert_not_called()


if __name__ == "__main__":
    unittest.main(verbosity=2)
