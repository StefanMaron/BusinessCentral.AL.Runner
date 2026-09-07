#!/usr/bin/env python3
"""Give each agent a private directory in the shared session scratchpad, and
refuse a path that belongs to somebody else.

The session scratchpad LOOKS per-agent and is not: every subagent of one session
gets the same directory injected into its prompt. Measured on this box on
2026-09-07, in one session's scratchpad: 200 entries, of which exactly ONE
carried an agent identity. Agents do disambiguate -- by PR or issue number
(`pr3388.body`, `c-3391.md`, `corpus-3405`) -- which works within one agent and
fails across two, because two agents handling two corpus PRs both reach for
`corpus/`. Bare `body.md`, `pr-body.md`, `corpus/`, `probe/`, `red/`, `green/`
and `backup/` were all present unqualified.

What that has cost, all recorded on #2980:

  * PRs #2973 and #2974 published with another agent's body, both declaring
    `Closes #2743` -- an issue belonging to a third agent's work.
  * #3073 published with one defect's title and an unrelated defect's body; the
    body's bug was then buried inside a closed issue behind a merged PR.
  * PR #3181 published with #3101's body; `closingIssuesReferences` read [2771]
    instead of [3057] until an agent re-read it.
  * A shared corpus clone checked out onto another agent's branch mid-run, so a
    full-corpus run silently executed WITHOUT the tests under measurement and
    reported an unchanged failure count.

Why a mechanism and not another line in the agent definition
------------------------------------------------------------
Three agents in one session each knew the scratchpad was shared and were caught
anyway, because the sharing is invisible at the moment of use: the path in the
prompt looks private, `gh pr create --body-file` reads whatever is there, and a
`cp -r` onto an existing clone succeeds. The issue's own "cheapest version" was
a documented convention; the 1-in-200 namespacing rate above is that convention's
measured adoption. So this module makes the private path the one that is easy to
get, and makes the shared path REFUSE rather than answer.

The failure mode of everything here is raising `ScratchpadCollision`. Nothing in
this file ever falls back to a shared path, because a fallback is precisely the
silent wrong answer being prevented -- the same reasoning as
`.claude/rules/loud-failures.md`, applied to tooling instead of to AL surfaces.

Usage
-----
    # a private path, created for you
    tools/agent_scratchpad.py path pr-body.md --agent-id stma-auto-23
    #   -> <scratchpad>/agent-stma-auto-23/pr-body.md

    # refuse before reading a file somebody else may own
    tools/agent_scratchpad.py check <path> --agent-id stma-auto-23

    # what in this scratchpad is shared and owned by nobody
    tools/agent_scratchpad.py scan

See docs/agent-scratchpad.md for the full rationale and the incident list.
"""
from __future__ import annotations

import argparse
import os
import re
import sys

# Every private directory carries this prefix, so `scan` can tell an owned
# directory from a bare one without a registry, and a human reading the
# scratchpad can attribute a stray file to the agent that wrote it.
AGENT_DIR_PREFIX = "agent-"

# An identity becomes a directory name, so it may not contain a separator or a
# `..`. Matching the shape used for branches and worktrees (`stma-auto-23`,
# `impl-4`) rather than accepting anything path-safe: an identity that does not
# look like an identity is far more likely to be a bug in the caller than a new
# naming scheme.
_VALID_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")

# The harness injects the scratchpad path into the prompt rather than the
# environment, so an agent normally passes --scratchpad. These are checked
# first for the case where a wrapper has exported one.
_SCRATCHPAD_ENV = ("CLAUDE_SCRATCHPAD_DIR", "AL_RUNNER_SCRATCHPAD")
_AGENT_ENV = "AL_RUNNER_AGENT_ID"


class ScratchpadCollision(Exception):
    """A path is shared, escapes its owner's directory, or has no owner.

    Raised instead of returning a shared path. Callers are expected to let it
    propagate: there is no correct recovery that still writes somewhere.
    """


def resolve_agent_id(agent_id: str | None = None, env: dict | None = None) -> str:
    """The identity, from the argument or the environment. Never guessed.

    A default -- the login name, a PID, a constant -- would put two agents back
    on one path, which is the entire defect. So a missing identity is an error.
    """
    env = os.environ if env is None else env
    value = agent_id if agent_id is not None else env.get(_AGENT_ENV)
    if value is None or not str(value).strip():
        raise ScratchpadCollision(
            "no agent identity: refusing to guess one, because a default would put "
            "two agents on one path -- which is the collision this exists to prevent. "
            f"Pass --agent-id <id>, or export {_AGENT_ENV}=<id>. Use the identity from "
            "your invoking prompt, the same string as your `agent:` label and your "
            "branch prefix (e.g. stma-auto-23)."
        )
    value = str(value).strip()
    if not _VALID_ID.match(value):
        raise ScratchpadCollision(
            f"agent identity {value!r} is not a plain name. It becomes a directory, so "
            "a separator or `..` in it could resolve into another agent's space. Expected "
            "the shape used for branches and worktrees, e.g. `stma-auto-23` or `impl-4`."
        )
    return value


def resolve_scratchpad(scratchpad: str | None = None, env: dict | None = None) -> str:
    env = os.environ if env is None else env
    if scratchpad:
        return os.path.realpath(scratchpad)
    for key in _SCRATCHPAD_ENV:
        if env.get(key):
            return os.path.realpath(env[key])
    raise ScratchpadCollision(
        "no scratchpad directory known. Pass --scratchpad <dir> (the path from your "
        f"prompt), or export one of {', '.join(_SCRATCHPAD_ENV)}."
    )


def agent_dir(agent_id: str | None = None, scratchpad: str | None = None,
              env: dict | None = None, create: bool = True) -> str:
    """This agent's private directory inside the shared scratchpad."""
    root = resolve_scratchpad(scratchpad, env)
    path = os.path.join(root, AGENT_DIR_PREFIX + resolve_agent_id(agent_id, env))
    if create:
        os.makedirs(path, exist_ok=True)
    return path


def agent_path(name: str, agent_id: str | None = None, scratchpad: str | None = None,
               env: dict | None = None, create: bool = True) -> str:
    """A private path for `name`, with its parent directory created.

    `name` is relative and may contain subdirectories. Anything that resolves
    outside this agent's directory is refused rather than clamped -- a clamp
    would silently write somewhere the caller did not ask for, and the caller
    would never learn its path was wrong.
    """
    base = agent_dir(agent_id, scratchpad, env, create=create)
    if os.path.isabs(name):
        raise ScratchpadCollision(
            f"{name!r} is absolute. Pass a name relative to your agent directory; "
            f"this function decides where that is ({base})."
        )
    full = os.path.realpath(os.path.join(base, name))
    if full != os.path.realpath(base) and not full.startswith(os.path.realpath(base) + os.sep):
        raise ScratchpadCollision(
            f"{name!r} resolves to {full}, outside your agent directory {base}. "
            "A `..` here lands in the shared scratchpad or in another agent's "
            "directory, which is the collision this exists to prevent."
        )
    if create:
        os.makedirs(os.path.dirname(full), exist_ok=True)
    return full


def owner_of(path: str, scratchpad: str | None = None, env: dict | None = None) -> str | None:
    """Which agent owns `path`: an identity, or None for shared/outside.

    None means one of two very different things -- outside the scratchpad
    entirely, or directly inside it and therefore owned by nobody. Callers that
    care about the difference use `assert_private`, which does.
    """
    root = os.path.realpath(resolve_scratchpad(scratchpad, env))
    full = os.path.realpath(path)
    if full == root or not full.startswith(root + os.sep):
        return None
    first = full[len(root) + 1:].split(os.sep)[0]
    if first.startswith(AGENT_DIR_PREFIX):
        return first[len(AGENT_DIR_PREFIX):] or None
    return None


def assert_private(path: str, agent_id: str | None = None, scratchpad: str | None = None,
                   env: dict | None = None) -> str:
    """Refuse `path` unless it is outside the scratchpad or owned by this agent.

    A path OUTSIDE the scratchpad is deliberately allowed: a worktree file, a
    package cache, `/etc/hosts` -- none of them are this tool's business, and
    refusing them would make the guard useless to wrap around real commands.
    What is refused is the shared scratchpad itself, and another agent's
    directory, which are the two places #2980's losses came from.
    """
    root = os.path.realpath(resolve_scratchpad(scratchpad, env))
    me = resolve_agent_id(agent_id, env)
    full = os.path.realpath(path)
    if full != root and not full.startswith(root + os.sep):
        return path                      # not in the scratchpad; not our concern
    owner = owner_of(full, scratchpad, env)
    if owner == me:
        return path
    suggested = os.path.join(root, AGENT_DIR_PREFIX + me, os.path.basename(full))
    if owner is None:
        raise ScratchpadCollision(
            f"{full} is directly in the SHARED session scratchpad, which every agent of "
            f"this session writes to -- it is owned by nobody and any agent may overwrite "
            f"it between your write and your read. That is how PRs #2973/#2974/#3181 were "
            f"published with another agent's body and a wrong closing reference (#2980).\n"
            f"Use your own path instead:\n  {suggested}\n"
            f"  tools/agent_scratchpad.py path {os.path.basename(full)} --agent-id {me}"
        )
    raise ScratchpadCollision(
        f"{full} belongs to agent {owner!r}, not to you ({me}). Reading it measures their "
        f"work; writing it destroys it -- a shared corpus clone checked out onto another "
        f"agent's branch is how a full-corpus run silently omitted the tests under "
        f"measurement (#2980).\nUse your own path instead:\n  {suggested}"
    )


def scan_shared(scratchpad: str | None = None, env: dict | None = None) -> list[str]:
    """Entries sitting directly in the scratchpad, i.e. owned by nobody.

    The audit half. Sorted so two runs are comparable.
    """
    root = resolve_scratchpad(scratchpad, env)
    if not os.path.isdir(root):
        return []
    return sorted(
        os.path.join(root, e) for e in os.listdir(root)
        if not e.startswith(AGENT_DIR_PREFIX)
    )


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Private per-agent paths in the shared session scratchpad (#2980).",
        epilog="Exit 0 = fine, 1 = collision refused, 2 = usage/setup problem.")
    # Both options are accepted BEFORE and AFTER the subcommand. argparse puts
    # parent options before it only, and `... path pr-body.md --agent-id x` is
    # what anyone actually types -- it failed with "unrecognized arguments",
    # which is a usage error for the correct intent. A tool whose safe path is
    # annoying to use gets worked around, and the workaround here is the shared
    # file this exists to prevent.
    # SUPPRESS, not None, is load-bearing: a subparser option with default=None
    # OVERWRITES a value the parent already parsed, so `--scratchpad X path f`
    # silently lost X and reported "no scratchpad directory known" for a command
    # that had supplied one. SUPPRESS leaves the attribute unset when the flag is
    # absent, so whichever position carried it wins and neither erases the other.
    def add_common(parser):
        parser.add_argument("--agent-id", default=argparse.SUPPRESS,
                            help=f"your identity, e.g. stma-auto-23 (or ${_AGENT_ENV})")
        parser.add_argument("--scratchpad", default=argparse.SUPPRESS,
                            help="the session scratchpad directory from your prompt")

    add_common(ap)
    sub = ap.add_subparsers(dest="cmd", required=True)

    p_path = sub.add_parser("path", help="print a private path, creating its parent")
    p_path.add_argument("name", help="a name relative to your agent directory")
    add_common(p_path)

    p_dir = sub.add_parser("dir", help="print your private directory")
    add_common(p_dir)

    p_check = sub.add_parser("check", help="refuse a path that is shared or foreign")
    p_check.add_argument("paths", nargs="+")
    add_common(p_check)

    add_common(sub.add_parser("scan", help="list scratchpad entries owned by nobody"))

    args = ap.parse_args(argv)
    agent_id = getattr(args, "agent_id", None)
    scratch = getattr(args, "scratchpad", None)
    try:
        if args.cmd == "path":
            print(agent_path(args.name, agent_id, scratch))
        elif args.cmd == "dir":
            print(agent_dir(agent_id, scratch))
        elif args.cmd == "check":
            for p in args.paths:
                assert_private(p, agent_id, scratch)
            print(f"OK -- {len(args.paths)} path(s) are yours or outside the scratchpad.")
        elif args.cmd == "scan":
            shared = scan_shared(scratch)
            if not shared:
                print("No shared entries: every entry is owned by an agent.")
                return 0
            print(f"{len(shared)} entry/entries owned by NOBODY in the shared scratchpad.")
            print("Any agent of this session can overwrite these between your write and "
                  "your read (#2980):")
            for p in shared:
                print(f"  {p}")
            return 0
    except ScratchpadCollision as exc:
        print(f"REFUSED: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
