# Claude Code Agent Plan — Working the Issue Backlog

This describes how to use Claude Code sessions/agents to execute the workstreams defined
in issue #170. It covers session scoping, branch/PR strategy, and review checkpoints so
multiple issues can be worked through safely and in parallel where dependencies allow.

## Principles

- **One issue (or tightly-coupled pair) per session/branch.** Keep diffs reviewable
  and bisectable. Don't let a session wander into unrelated workstreams.
- **Respect the dependency order in issue #170.** Several workstreams are gated on a
  decision or an unlanded prerequisite rather than on effort; starting one early means
  doing it twice.
- **Draft PR per issue, referencing the issue number.** Branch name convention:
  `claude/issue-<n>-<short-slug>` (e.g. `claude/issue-18-js-execution`).
- **A session should end with either a pushed fix + draft PR, or a written note on
  why the issue is blocked** (e.g. needs a product decision) — never silently drop it.

## Sequencing

**The sequencing lives in issue #170 — "Backlog plan: re-sequenced workstreams across the
62 open issues".** It replaces both the tier list this section used to duplicate and the
inline plan that `ACTION_PLAN.md` used to carry.

Two copies of a sequencing plan drift apart, and these had: the phases listed here still
described #52 and #54 as work in flight long after both issues closed, and still gated
Phase 3 behind a safety net that is now complete. The plan belongs in one place that
moves with the backlog.

What has not changed is the dependency *discipline*: respect the order #170 gives,
and if an issue depends on work that is not merged yet, report the blocker rather than
working around it.

## Running it with Claude Code

For each issue:
1. Start a session (or spawn a subagent — see `issue-resolver` below) with a prompt
   that includes: the issue number/title/body, **the relevant excerpt from issue #170
   pasted in full**, and the target branch name. A subagent has no GitHub API access and
   cannot fetch #170 itself, so whatever plan context it needs has to arrive in the brief.
2. The session should: reproduce the bug/confirm the gap, implement the fix, add
   tests **and confirm they fail without it**, run the full suite, commit and push.
3. **Opening the PR is the calling session's job.** A subagent has no GitHub API
   access — `gh` is absent and `/repos/...` returns 403 — so it can push a branch but
   cannot file the PR. Have it write the PR body to a file and report the path; three
   agent runs in one session ended with finished work and no PR because of this.
4. Before opening the PR, **rebase the branch onto current `main`** and re-run the
   suites. Agent branches go stale quickly when several land in a session, and a
   diff against a moved `main` shows other people's merges as deletions.
5. **Verify the agent's central claim yourself** rather than relaying it. For a fix,
   that means reverting it and watching the new test fail; for a detector, stubbing it
   out and watching the suite go red. Both Phase 2 agents' headline claims held up
   under that check — but the check is what makes the claim worth repeating.
6. Human review gate: PRs from Phase 1 and Phase 6 (security-sensitive) get a manual
   review before merge; later phases can use lighter review if CI is green and the
   regression suite (Phase 2) passes.

### Parallelism guidance
- Within a phase marked "independent" or "parallelizable" above, multiple sessions
  can run concurrently.
- Across phases, respect the dependency arrows — e.g. don't parallelize Phase 3 work
  with Phase 1, since Phase 3's font-size fix (#29) assumes JS/OCR pipelines aren't
  simultaneously changing underneath it.

### Subagent
A repo-specific subagent (`.claude/agents/issue-resolver.md`) is provided to
standardize how any session picks up a single issue from this plan: it reads the
issue, works from the plan context it was handed, implements the fix on a
correctly-named branch, and pushes it. Invoke it per-issue rather than re-deriving this
process from scratch each time.
