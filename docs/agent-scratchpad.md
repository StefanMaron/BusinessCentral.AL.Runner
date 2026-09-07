# The session scratchpad is shared, and the private path is one command away

The scratchpad directory injected into an agent's prompt looks per-agent. It is
not: **every subagent of one session gets the same directory.** Nothing in the
path says so, and nothing fails when two agents use the same name.

## The measurement

One session's scratchpad on the development box, 2026-09-07:

| | |
|---|---|
| entries | **200** |
| entries carrying an agent identity | **1** |

Agents *do* disambiguate — but by PR or issue number, not by identity:
`pr3385.body`, `pr3386.body`, `pr3387.body`, `pr3388.body`; `c-3379.md` …
`c-3393.md`; `corpus-267`, `corpus-3405`. That works within one agent and fails
across two, because two agents each handling a corpus PR both reach for
`corpus/`. Left bare and unqualified in the same directory: `body.md`,
`pr-body.md`, `prbody.md`, `corpus/`, `probe/`, `red/`, `green/`, `backup/`,
`err.txt`.

## What it has cost

All on [#2980](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/2980):

- **PRs #2973 and #2974** published carrying another agent's body, both declaring
  `Closes #2743` — an issue belonging to a third agent's work.
- **#3073** published with one defect's title and an unrelated defect's body. A PR
  then fixed the title's defect and closed the issue, so an undiagnosed reproducer
  ended up **inside a closed issue behind a merged PR**, where it reads as fixed.
  Re-filed as #3105.
- **PR #3181** published with #3101's body; `closingIssuesReferences` read `[2771]`
  instead of `[3057]` until the agent re-read it. Had it merged in that window,
  #2771 would have been closed by the wrong PR and #3057 left open, silently.
- **A shared corpus clone** checked out onto another agent's branch mid-task. The
  second agent's full-corpus runs then ran *without its own tests*: the failure
  count was identical before and after its fix, because its codeunit was not
  there. A confident, wrong RED→GREEN.
- **A corpus commit on the wrong branch** — `eaf1f6e` landed on
  `agent/stma-auto-3/…` (corpus PR #267) instead of its author's branch, so a
  runner PR read as having upstream proof it did not have.

The class matters more than any instance. This is not "two agents overwrite a
file and notice". It is **silent wrong answers in the shape of results** — the
same family as the `grep`-function and `rg --hidden` traps in `CLAUDE.md`.

## The mechanism

`tools/agent_scratchpad.py`. Every path it returns contains the agent identity,
so two identities cannot collide; every path it is asked to *check* is refused
unless it is outside the scratchpad or owned by the caller.

```bash
# a private path, parent directory created
p=$(tools/agent_scratchpad.py path pr-body.md --agent-id stma-auto-23)
gh pr create --body-file "$p"

# your private directory, e.g. to clone into
tools/agent_scratchpad.py dir --agent-id stma-auto-23

# refuse before reading something another agent may own -- exit 1 if shared
tools/agent_scratchpad.py check "$SOME_PATH" --agent-id stma-auto-23

# audit: what in this scratchpad is owned by nobody
tools/agent_scratchpad.py scan
```

Layout: `<scratchpad>/agent-<id>/…`. The `agent-` prefix is what lets `scan`
tell an owned directory from a bare one without a registry, and what lets a
human attribute a stray file.

Options are accepted on either side of the subcommand, because
`path pr-body.md --agent-id x` is what people type.

### It refuses rather than falling back

There is no path on which this module returns a shared location. A missing
identity is an error, not a default — a default would put two agents back on one
path, which is the whole defect. `..` is refused rather than clamped, because a
clamp writes somewhere the caller did not ask for and never tells them. This is
`.claude/rules/loud-failures.md` applied to tooling: a tool that silently reads
another agent's file is the same defect class as a patch that silently returns a
default.

`assert_private` deliberately **allows** any path outside the scratchpad — a
worktree file, a package cache — so the guard can be wrapped around real commands
without becoming useless.

### The hook, for the moment of use

`.claude/hooks/shared-scratchpad-guard.py` warns when a Bash command writes to,
clones onto, or reads a `--body-file` from a scratchpad path with no `agent-`
owner in it. It is **advisory** and never blocks: reading another agent's log to
diagnose a collision is legitimate, and a hook that blocks legitimate work gets
switched off, taking the warning with it. The refusal that *can* stop something
is `agent_scratchpad.py check`, which a caller opts into.

It stays quiet on ordinary reads (`ls`, `sed -n`, `grep` of a log) and on paths
that already carry an agent directory. That is deliberate: a warning that fires
on everything is not a mechanism.

## Why a mechanism and not a rule

The issue's own cheapest proposal was a documented convention. The 1-in-200
namespacing rate above **is** that convention's measured adoption — three agents
in one session each knew the scratchpad was shared and were caught anyway,
because the sharing is invisible at the moment of use. So the private path is
made the easy one to get, and the shared one is made to refuse.

## Testing note

`tools/test_agent_scratchpad.py` is picked up by `pr-gate.yml`'s `tools-tests`
job, which globs `tools/test_*.py`. **No CI job globs `.claude/hooks/test_*.py`**
— so `test_prefer_code_navigation.py` had never run in CI — and that suite
therefore delegates to every `.claude/hooks/test_*.py`, which makes them gate by
the same discovery argument without a workflow change.
