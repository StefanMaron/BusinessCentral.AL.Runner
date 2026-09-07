#!/usr/bin/env python3
"""Unit tests for tools/agent_scratchpad.py.

The claim under test is not "the function returns a string". It is the two
properties that would have prevented the incidents on #2980:

  1. Two different agent identities NEVER resolve to the same path, for any
     relative name -- so a collision is impossible rather than unlikely.
  2. A path that IS shared -- a bare name directly in the session scratchpad --
     is REFUSED with a message naming the owner, rather than silently read or
     overwritten.

Both are asserted by mutation in the tests below: the namespacing test compares
across identities, and the guard tests assert on the refusal, so removing either
mechanism fails a test rather than quietly passing.

Run: python3 tools/test_agent_scratchpad.py
"""
from __future__ import annotations

import importlib.util
import os
import pathlib
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "agent_scratchpad", os.path.join(HERE, "agent_scratchpad.py"))
asp = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(asp)

FAILURES: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  ok   {name}")
    else:
        print(f"  FAIL {name}  {detail}")
        FAILURES.append(name)


def test_distinct_identities_never_share_a_path() -> None:
    """The property, over many names and many identities -- not one example."""
    with tempfile.TemporaryDirectory() as root:
        names = ["pr-body.md", "corpus", "body.md", "probe/run.log", "red", "a.json"]
        ids = ["stma-auto-1", "stma-auto-2", "stma-auto-23", "impl-4", "stma-auto-11"]
        seen: dict[str, tuple[str, str]] = {}
        collisions = []
        for agent in ids:
            for n in names:
                p = asp.agent_path(n, agent_id=agent, scratchpad=root)
                if p in seen and seen[p][0] != agent:
                    collisions.append((p, seen[p], (agent, n)))
                seen[p] = (agent, n)
        check("no two identities resolve to one path", not collisions, str(collisions[:2]))
        # ...and the same identity+name IS stable, or nothing could be resumed.
        a = asp.agent_path("pr-body.md", agent_id="stma-auto-23", scratchpad=root)
        b = asp.agent_path("pr-body.md", agent_id="stma-auto-23", scratchpad=root)
        check("same identity+name is stable across calls", a == b, f"{a} != {b}")
        # The identity must actually appear, so a reader can attribute a stray file.
        check("the identity appears in the path", "stma-auto-23" in a, a)


def test_the_directory_is_created_and_is_under_the_scratchpad() -> None:
    with tempfile.TemporaryDirectory() as root:
        p = asp.agent_path("probe/run.log", agent_id="stma-auto-23", scratchpad=root)
        check("parent directory is created", os.path.isdir(os.path.dirname(p)), p)
        check("path stays under the scratchpad root",
              os.path.realpath(p).startswith(os.path.realpath(root)), p)


def test_escaping_the_agent_directory_is_refused() -> None:
    """`../` would put one agent back in another's space -- the whole point."""
    with tempfile.TemporaryDirectory() as root:
        for bad in ["../body.md", "a/../../body.md", "/etc/passwd"]:
            try:
                asp.agent_path(bad, agent_id="stma-auto-23", scratchpad=root)
                check(f"refuses escaping name {bad!r}", False, "no exception raised")
            except asp.ScratchpadCollision:
                check(f"refuses escaping name {bad!r}", True)


def test_a_shared_bare_path_is_refused_loudly() -> None:
    """The #2980 shape: a bare name in the session scratchpad, owned by nobody."""
    with tempfile.TemporaryDirectory() as root:
        shared = os.path.join(root, "pr-body.md")
        with open(shared, "w") as fh:
            fh.write("Closes #2743\n")
        try:
            asp.assert_private(shared, agent_id="stma-auto-23", scratchpad=root)
            check("a bare shared path is refused", False, "no exception raised")
        except asp.ScratchpadCollision as exc:
            msg = str(exc)
            check("a bare shared path is refused", True)
            check("the refusal names the offending path", "pr-body.md" in msg, msg)
            check("the refusal offers the private path",
                  "stma-auto-23" in msg, msg)


def test_another_agents_private_path_is_refused_and_names_the_owner() -> None:
    with tempfile.TemporaryDirectory() as root:
        theirs = asp.agent_path("pr-body.md", agent_id="stma-auto-9", scratchpad=root)
        with open(theirs, "w") as fh:
            fh.write("Closes #3101\n")
        try:
            asp.assert_private(theirs, agent_id="stma-auto-23", scratchpad=root)
            check("another agent's path is refused", False, "no exception raised")
        except asp.ScratchpadCollision as exc:
            check("another agent's path is refused", True)
            check("the refusal names the OWNER, not just the path",
                  "stma-auto-9" in str(exc), str(exc))


def test_my_own_private_path_is_accepted() -> None:
    """A guard that refuses everything is as useless as one that refuses nothing."""
    with tempfile.TemporaryDirectory() as root:
        mine = asp.agent_path("pr-body.md", agent_id="stma-auto-23", scratchpad=root)
        with open(mine, "w") as fh:
            fh.write("Closes #2980\n")
        ok = True
        try:
            asp.assert_private(mine, agent_id="stma-auto-23", scratchpad=root)
        except asp.ScratchpadCollision as exc:
            ok = False
            detail = str(exc)
        check("my own private path is accepted", ok, "" if ok else detail)
        # A path outside the scratchpad entirely is not this tool's business.
        outside = os.path.join(root, "..", "elsewhere.md")
        ok2 = True
        try:
            asp.assert_private(outside, agent_id="stma-auto-23", scratchpad=root)
        except asp.ScratchpadCollision:
            ok2 = False
        check("a path outside the scratchpad is not refused", ok2)


def test_identity_is_required_and_never_guessed() -> None:
    """No identity must be an ERROR. A default would re-create the shared path."""
    with tempfile.TemporaryDirectory() as root:
        env = {k: v for k, v in os.environ.items() if k != "AL_RUNNER_AGENT_ID"}
        try:
            asp.agent_path("x.md", agent_id=None, scratchpad=root, env=env)
            check("a missing identity is refused, not defaulted", False,
                  "no exception raised")
        except asp.ScratchpadCollision as exc:
            check("a missing identity is refused, not defaulted", True)
            check("the refusal explains how to supply one",
                  "AL_RUNNER_AGENT_ID" in str(exc), str(exc))
        # ...and the environment variable IS honoured when present.
        env2 = dict(env, AL_RUNNER_AGENT_ID="stma-auto-23")
        p = asp.agent_path("x.md", agent_id=None, scratchpad=root, env=env2)
        check("AL_RUNNER_AGENT_ID supplies the identity",
              "stma-auto-23" in p, p)


def test_a_malformed_identity_cannot_forge_a_path() -> None:
    with tempfile.TemporaryDirectory() as root:
        for bad in ["../stma-auto-9", "a/b", "", "   "]:
            try:
                asp.agent_path("x.md", agent_id=bad, scratchpad=root)
                check(f"refuses malformed identity {bad!r}", False, "no exception")
            except asp.ScratchpadCollision:
                check(f"refuses malformed identity {bad!r}", True)


def test_scan_reports_shared_entries_with_no_owner() -> None:
    """The audit half: point it at a real scratchpad and it names the hazards."""
    with tempfile.TemporaryDirectory() as root:
        open(os.path.join(root, "body.md"), "w").close()
        open(os.path.join(root, "pr-body.md"), "w").close()
        os.makedirs(os.path.join(root, "corpus"))
        asp.agent_path("safe.md", agent_id="stma-auto-23", scratchpad=root)
        shared = asp.scan_shared(scratchpad=root)
        names = {os.path.basename(p) for p in shared}
        check("scan finds the bare shared files", {"body.md", "pr-body.md"} <= names, str(names))
        check("scan finds the bare shared directory", "corpus" in names, str(names))
        check("scan does not report an agent-owned directory",
              asp.AGENT_DIR_PREFIX + "stma-auto-23" not in names, str(names))


def _cli(args: list[str]) -> tuple[int, str, str]:
    r = subprocess.run(
        [sys.executable, os.path.join(HERE, "agent_scratchpad.py")] + args,
        capture_output=True, text=True,
        env={k: v for k, v in os.environ.items() if k != "AL_RUNNER_AGENT_ID"})
    return r.returncode, r.stdout.strip(), r.stderr.strip()


def test_cli_accepts_options_on_either_side_of_the_subcommand() -> None:
    """Both real defects found by smoke-testing this CLI, pinned.

    1. Options only on the parent -> `path f --agent-id x` died with
       "unrecognized arguments" for exactly the spelling anyone types.
    2. Options on the subparser with default=None -> the subparser OVERWROTE the
       parent's parsed value, so `--scratchpad X path f` silently lost X and
       refused a command that had supplied one. That is a silent wrong answer in
       a tool whose entire job is to prevent one.
    """
    with tempfile.TemporaryDirectory() as root:
        rc_a, out_a, err_a = _cli(
            ["path", "pr-body.md", "--agent-id", "stma-auto-23", "--scratchpad", root])
        rc_b, out_b, err_b = _cli(
            ["--agent-id", "stma-auto-23", "--scratchpad", root, "path", "pr-body.md"])
        check("options AFTER the subcommand work", rc_a == 0, err_a)
        check("options BEFORE the subcommand work", rc_b == 0, err_b)
        check("both spellings give the SAME path", out_a == out_b and bool(out_a),
              f"{out_a!r} != {out_b!r}")


def test_cli_check_exits_1_and_explains_on_a_shared_path() -> None:
    """A wrapper reads the exit code, so it has to discriminate."""
    with tempfile.TemporaryDirectory() as root:
        shared = os.path.join(root, "pr-body.md")
        open(shared, "w").close()
        rc, out, err = _cli(["check", shared, "--agent-id", "stma-auto-23",
                             "--scratchpad", root])
        check("check exits 1 on a shared path", rc == 1, f"rc={rc}")
        check("the refusal goes to stderr", "REFUSED" in err, err)
        mine = asp.agent_path("pr-body.md", agent_id="stma-auto-23", scratchpad=root)
        open(mine, "w").close()
        rc2, _, err2 = _cli(["check", mine, "--agent-id", "stma-auto-23",
                             "--scratchpad", root])
        check("check exits 0 on my own path", rc2 == 0, err2)


def test_cli_refuses_rather_than_defaulting_without_an_identity() -> None:
    with tempfile.TemporaryDirectory() as root:
        rc, out, err = _cli(["path", "pr-body.md", "--scratchpad", root])
        check("no identity -> exit 1, not a path", rc == 1, f"rc={rc} out={out!r}")
        check("nothing is printed to stdout", out == "", out)


def test_the_hook_suite_runs_here_because_no_CI_job_globs_it() -> None:
    """`.claude/hooks/test_*.py` is discovered by NOTHING.

    `pr-gate.yml`'s tools-tests job globs `tools/test_*.py` only, so
    `.claude/hooks/test_prefer_code_navigation.py` has never run in CI and
    neither would the new hook's suite. That is the same defect the tools-tests
    job was created to fix, one directory over: a test file that exists, passes
    locally, and gates nothing.

    Rather than edit a workflow -- this box's token has no `workflow` scope, so a
    PR touching .github/workflows/ cannot be merged from here -- this delegates
    from a suite the glob DOES pick up. Any .claude/hooks/test_*.py now gates the
    day it lands, by the same discovery argument, with no workflow change.
    """
    hooks = sorted(pathlib.Path(HERE).parent.joinpath(".claude", "hooks").glob("test_*.py"))
    check("hook suites are discovered", len(hooks) >= 2, f"found {[h.name for h in hooks]}")
    for h in hooks:
        r = subprocess.run([sys.executable, str(h)], capture_output=True, text=True)
        check(f"{h.name} passes", r.returncode == 0,
              (r.stdout + r.stderr)[-600:])


def main() -> int:
    for fn in sorted(
        (v for k, v in globals().items() if k.startswith("test_")),
        key=lambda f: f.__name__,
    ):
        print(fn.__name__)
        fn()
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED: {FAILURES}")
        return 1
    print("\nall passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
