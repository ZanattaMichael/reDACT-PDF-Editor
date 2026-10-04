# GitHub Issues Action Plan

**The plan now lives in issue #170 — "Backlog plan: re-sequenced workstreams across the
62 open issues".** Read it there; it is the single source of truth for tier membership,
sequencing and dependencies.

This file used to carry the plan inline. It was generated on 2026-07-28 against the 40
issues open at the time (#17–#56), and it went stale: 20 of those are now closed, and
five workstreams opened since — the iText-to-OfficeIMO engine migration, batch
redaction, security/delivery, maintainability, and docs — were never in it. A document
that disagrees with the backlog is worse than no document, so the content moved to an
issue where it can be revised as the backlog moves, and commented on in place.

## If you cannot reach the issue

A Claude Code subagent has no GitHub API access and cannot fetch #170. Whoever starts
that subagent has to paste the relevant excerpt into its brief — see "Running it with
Claude Code" in `AGENT_PLAN.md`. Do not reconstruct the sequencing from this file; it no
longer contains any.

## What stayed here

Nothing about *how* to work the backlog has changed. `AGENT_PLAN.md` still holds that:
one issue per session and branch, `claude/issue-<number>-<short-slug>`, a draft PR per
issue, and proving a new test fails before the fix lands.
