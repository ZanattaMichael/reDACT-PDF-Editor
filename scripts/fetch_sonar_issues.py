#!/usr/bin/env python3
"""Wait for a SonarQube Cloud analysis to finish, then download its issues as JSON.

The scanner's `end` step only *submits* the analysis -- SonarQube Cloud processes it
asynchronously on a Compute Engine task, so querying issues straight afterwards returns the
previous run's results (or none at all). The scanner leaves the task's URL in report-task.txt;
this polls that until the task settles.

A settled task is not yet enough: the issue search is served from an index that catches up a
moment after the task reports SUCCESS, and a query in that gap still returns the previous
analysis's issues (seen on PR #179, where a push's findings were the ones it had just fixed). The
analysis's own `violations` measure is recorded before the task finishes, so this waits until the
search's total agrees with it, then pages through api/issues/search.

Standard library only, and every request is an explicit HTTPS GET with a bearer token -- no shell
interpolation, no `curl | sh`, nothing unpinned, in keeping with the supply-chain hardening in
issue #56.

Usage:
    SONAR_TOKEN=... fetch_sonar_issues.py --output issues.json [--pull-request 68]
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

from sonar_paths import safe_path

DEFAULT_REPORT_TASK = Path(".sonarqube/out/.sonar/report-task.txt")
PAGE_SIZE = 500          # api/issues/search maximum
MAX_ISSUES = 10_000      # the API refuses paging past this
SETTLED = {"SUCCESS", "FAILED", "CANCELED", "CANCELLED"}
# The measures that count an analysis's open issues: `violations` on a branch and on a pull
# request, `new_violations` as a fallback (on a pull request every issue is new).
ISSUE_COUNT_METRICS = ("violations", "new_violations")


def read_report_task(path: Path) -> dict[str, str]:
    """Parse the scanner's report-task.txt (key=value per line), searching if it moved."""
    if not path.exists():
        found = sorted(Path(".").rglob("report-task.txt"))
        if not found:
            raise SystemExit(f"error: {path} not found — did the scanner's `end` step run?")
        path = found[0]
        print(f"note: using {path}", file=sys.stderr)
    values: dict[str, str] = {}
    for line in safe_path(str(path), must_exist=True).read_text(encoding="utf-8").splitlines():
        key, sep, value = line.partition("=")
        if sep:
            values[key.strip()] = value.strip()
    return values


def get_json(url: str, token: str, timeout: int = 60) -> dict[str, Any]:
    request = urllib.request.Request(url, headers={
        "Authorization": f"Bearer {token}",
        "Accept": "application/json",
    })
    if not url.lower().startswith("https://"):
        raise SystemExit(f"error: refusing to call a non-HTTPS URL: {url}")
    with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310 - https enforced
        return json.loads(response.read().decode("utf-8"))


def wait_for_task(task_url: str, token: str, timeout_seconds: int = 600) -> str:
    """Block until the Compute Engine task settles; returns its final status."""
    deadline = time.monotonic() + timeout_seconds
    delay = 3
    while True:
        status = str(get_json(task_url, token).get("task", {}).get("status", "")).upper()
        if status in SETTLED:
            return status
        if time.monotonic() >= deadline:
            raise SystemExit(f"error: analysis did not finish within {timeout_seconds}s "
                             f"(last status: {status or 'unknown'})")
        time.sleep(delay)
        delay = min(delay * 2, 30)  # back off rather than hammering the API


def scoped(query: dict[str, str], pull_request: str | None, branch: str | None) -> dict[str, str]:
    """`query` narrowed to the analysed pull request or branch."""
    if pull_request:
        return {**query, "pullRequest": pull_request}
    if branch:
        return {**query, "branch": branch}
    return query


def api_url(base_url: str, path: str, query: dict[str, str]) -> str:
    return f"{base_url.rstrip('/')}/{path}?{urllib.parse.urlencode(query)}"


def analysis_issue_count(base_url: str, project_key: str, token: str,
                         pull_request: str | None, branch: str | None) -> int | None:
    """How many open issues the finished analysis recorded; None when the server does not say."""
    query = scoped({"component": project_key, "metricKeys": ",".join(ISSUE_COUNT_METRICS)},
                   pull_request, branch)
    try:
        payload = get_json(api_url(base_url, "api/measures/component", query), token)
    except urllib.error.HTTPError as error:
        print(f"note: could not read the analysis's issue count ({error.code} {error.reason})",
              file=sys.stderr)
        return None
    values: dict[str, str] = {}
    for measure in (payload.get("component") or {}).get("measures") or []:
        # A plain measure has a value; a new-code one may carry it in its period instead.
        period = measure.get("period") or next(iter(measure.get("periods") or []), {})
        value = measure.get("value", period.get("value"))
        if value is not None:
            values[str(measure.get("metric"))] = str(value)
    for metric in ISSUE_COUNT_METRICS:
        if values.get(metric, "").isdigit():
            return int(values[metric])
    return None


def indexed_issue_count(base_url: str, project_key: str, token: str,
                        pull_request: str | None, branch: str | None) -> int:
    """How many open issues the issue search currently returns for the PR or branch."""
    query = scoped({"componentKeys": project_key, "resolved": "false", "ps": "1"}, pull_request, branch)
    payload = get_json(api_url(base_url, "api/issues/search", query), token)
    return int((payload.get("paging") or {}).get("total", payload.get("total") or 0))


def wait_for_index(base_url: str, project_key: str, token: str, pull_request: str | None,
                   branch: str | None, expected: int, timeout_seconds: int = 120) -> bool:
    """Block until the issue search returns `expected` open issues; False if it never does."""
    deadline = time.monotonic() + timeout_seconds
    delay = 2
    while True:
        found = indexed_issue_count(base_url, project_key, token, pull_request, branch)
        if found == expected:
            return True
        if time.monotonic() >= deadline:
            return False
        print(f"  the issue search has {found}, the analysis {expected}; waiting for it to catch up…",
              file=sys.stderr)
        time.sleep(delay)
        delay = min(delay * 2, 10)


def fetch_issues(base_url: str, project_key: str, token: str,
                 pull_request: str | None, branch: str | None) -> list[dict[str, Any]]:
    """Page through every unresolved issue for the analysed PR or branch."""
    issues: list[dict[str, Any]] = []
    page = 1
    while True:
        query = scoped({
            "componentKeys": project_key,
            "resolved": "false",
            "ps": str(PAGE_SIZE),
            "p": str(page),
        }, pull_request, branch)
        payload = get_json(api_url(base_url, "api/issues/search", query), token)
        batch = payload.get("issues") or []
        issues.extend(batch)
        total = int(payload.get("total") or 0)
        if len(batch) < PAGE_SIZE or len(issues) >= min(total, MAX_ISSUES):
            return issues
        page += 1



def summarize(issues: list[dict[str, Any]]) -> None:
    """Print one line per issue, so the findings are readable in the job log.

    Everything downstream of this script publishes to somewhere else -- SonarQube Cloud's
    dashboard, or GitHub's Security tab -- and both need permissions the person reading a failed
    build may not have. The log is the one place everyone can see, so the findings go there too.
    """
    for issue in issues:
        component = str(issue.get("component") or "")
        # Component keys are "<project-key>:<path>"; only the path is useful here.
        _, _, path = component.rpartition(":")
        line = issue.get("line")
        where = f"{path or component}:{line}" if line else (path or component)
        rule = issue.get("rule") or "?"
        message = " ".join(str(issue.get("message") or "").split())
        print(f"  {where}  [{rule}] {message}", file=sys.stderr)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report-task", type=Path, default=DEFAULT_REPORT_TASK)
    parser.add_argument("--output", default="sonar-issues.json")
    parser.add_argument("--pull-request", default=None)
    parser.add_argument("--branch", default=None)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--index-timeout", type=int, default=120,
                        help="seconds to wait for the issue search to catch up with the analysis")
    args = parser.parse_args(argv)

    token = os.environ.get("SONAR_TOKEN")
    if not token:
        raise SystemExit("error: SONAR_TOKEN is not set")

    report = read_report_task(args.report_task)
    task_url = report.get("ceTaskUrl")
    base_url = report.get("serverUrl", "https://sonarcloud.io")
    project_key = report.get("projectKey")
    if not task_url or not project_key:
        raise SystemExit("error: report-task.txt has no ceTaskUrl/projectKey")

    print(f"Waiting for analysis of {project_key}…", file=sys.stderr)
    status = wait_for_task(task_url, token, args.timeout)
    if status != "SUCCESS":
        raise SystemExit(f"error: analysis finished with status {status}")

    expected = analysis_issue_count(base_url, project_key, token, args.pull_request, args.branch)
    if expected is None:
        print("note: the analysis reports no issue count, so the search is read without waiting",
              file=sys.stderr)
    elif not wait_for_index(base_url, project_key, token, args.pull_request, args.branch,
                            expected, args.index_timeout):
        # Publishing a possibly stale list is no worse than before this check existed, and a
        # failed step would hide the findings altogether; say so where the run summary shows it.
        print(f"::warning::The SonarQube Cloud issue search still disagreed with the analysis "
              f"({expected} open issues) after {args.index_timeout}s; the findings published to "
              f"code scanning may be the previous analysis's.")

    issues = fetch_issues(base_url, project_key, token, args.pull_request, args.branch)
    safe_path(args.output).write_text(
        json.dumps({"issues": issues}, indent=2), encoding="utf-8")
    print(f"Fetched {len(issues)} unresolved issue(s) -> {args.output}", file=sys.stderr)
    summarize(issues)

    # Hand the project key on so the converter can strip it off component keys.
    if step_output := os.environ.get("GITHUB_OUTPUT"):
        with open(step_output, "a", encoding="utf-8") as handle:
            handle.write(f"project-key={project_key}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
