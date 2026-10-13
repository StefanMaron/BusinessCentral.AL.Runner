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

# An identity is `<pool tag>-issue-<N>-<session token>` (impl-agent.md): the pool
# tag is shared by every loop holding it, so only the trailing token tells two
# concurrent agents apart (#3619).
ME = "stma-auto-23-issue-2980-1a2b3c4d"
OTHER = "stma-auto-9-issue-3101-5e6f7a8b"


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
        ids = [ME, "stma-auto-23-issue-2980-1a2b3c4e", "stma-auto-2-issue-7-00000001", "stma-auto-23-issue-9-00000002",
               "impl-4-issue-3-00000003", "stma-auto-11-issue-5-00000004"]
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
        a = asp.agent_path("pr-body.md", agent_id=ME, scratchpad=root)
        b = asp.agent_path("pr-body.md", agent_id=ME, scratchpad=root)
        check("same identity+name is stable across calls", a == b, f"{a} != {b}")
        # The identity must actually appear, so a reader can attribute a stray file.
        check("the identity appears in the path", ME in a, a)


def test_the_directory_is_created_and_is_under_the_scratchpad() -> None:
    with tempfile.TemporaryDirectory() as root:
        p = asp.agent_path("probe/run.log", agent_id=ME, scratchpad=root)
        check("parent directory is created", os.path.isdir(os.path.dirname(p)), p)
        check("path stays under the scratchpad root",
              os.path.realpath(p).startswith(os.path.realpath(root)), p)


def test_escaping_the_agent_directory_is_refused() -> None:
    """`../` would put one agent back in another's space -- the whole point."""
    with tempfile.TemporaryDirectory() as root:
        for bad in ["../body.md", "a/../../body.md", "/etc/passwd"]:
            try:
                asp.agent_path(bad, agent_id=ME, scratchpad=root)
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
            asp.assert_private(shared, agent_id=ME, scratchpad=root)
            check("a bare shared path is refused", False, "no exception raised")
        except asp.ScratchpadCollision as exc:
            msg = str(exc)
            check("a bare shared path is refused", True)
            check("the refusal names the offending path", "pr-body.md" in msg, msg)
            check("the refusal offers the private path",
                  ME in msg, msg)


def test_another_agents_private_path_is_refused_and_names_the_owner() -> None:
    with tempfile.TemporaryDirectory() as root:
        theirs = asp.agent_path("pr-body.md", agent_id=OTHER, scratchpad=root)
        with open(theirs, "w") as fh:
            fh.write("Closes #3101\n")
        try:
            asp.assert_private(theirs, agent_id=ME, scratchpad=root)
            check("another agent's path is refused", False, "no exception raised")
        except asp.ScratchpadCollision as exc:
            check("another agent's path is refused", True)
            check("the refusal names the OWNER, not just the path",
                  OTHER in str(exc), str(exc))


def test_my_own_private_path_is_accepted() -> None:
    """A guard that refuses everything is as useless as one that refuses nothing."""
    with tempfile.TemporaryDirectory() as root:
        mine = asp.agent_path("pr-body.md", agent_id=ME, scratchpad=root)
        with open(mine, "w") as fh:
            fh.write("Closes #2980\n")
        ok = True
        try:
            asp.assert_private(mine, agent_id=ME, scratchpad=root)
        except asp.ScratchpadCollision as exc:
            ok = False
            detail = str(exc)
        check("my own private path is accepted", ok, "" if ok else detail)
        # A path outside the scratchpad entirely is not this tool's business.
        outside = os.path.join(root, "..", "elsewhere.md")
        ok2 = True
        try:
            asp.assert_private(outside, agent_id=ME, scratchpad=root)
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
        env2 = dict(env, AL_RUNNER_AGENT_ID=ME)
        p = asp.agent_path("x.md", agent_id=None, scratchpad=root, env=env2)
        check("AL_RUNNER_AGENT_ID supplies the identity",
              ME in p, p)


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
        asp.agent_path("safe.md", agent_id=ME, scratchpad=root)
        shared = asp.scan_shared(scratchpad=root)
        names = {os.path.basename(p) for p in shared}
        check("scan finds the bare shared files", {"body.md", "pr-body.md"} <= names, str(names))
        check("scan finds the bare shared directory", "corpus" in names, str(names))
        check("scan does not report an agent-owned directory",
              asp.AGENT_DIR_PREFIX + ME not in names, str(names))


def test_a_bare_pool_tag_is_refused_because_concurrent_agents_share_it() -> None:
    """#3619: three agents under `stma-auto-2` all resolved to `agent-stma-auto-2`.

    Nothing in a bare tag says WHICH of its concurrent holders is asking, so the
    tool cannot answer "yours?" -- the third state. It must refuse, in every
    entry point, rather than hand back the directory they all share.
    """
    with tempfile.TemporaryDirectory() as root:
        for tag in ["stma-auto-2", "impl-4", "stma-auto-2-issue-3606", "stma-auto-2-3606",
                    "stma-auto-2-issue-3606-NOTHEX!!", "stma-auto-2-issue-3606-1a2b3c4",
                    "1a2b3c4d", "-1a2b3c4d"]:
            for label, call in (
                ("dir", lambda: asp.agent_dir(tag, scratchpad=root)),
                ("path", lambda: asp.agent_path("x.md", tag, scratchpad=root)),
                ("check", lambda: asp.assert_private(
                    os.path.join(root, "agent-" + tag, "corpus"), tag, scratchpad=root)),
            ):
                try:
                    call()
                    check(f"{label} refuses the unsessioned identity {tag!r}", False,
                          "no exception raised")
                except asp.ScratchpadCollision as exc:
                    check(f"{label} refuses the unsessioned identity {tag!r}", True)
                    if tag == "stma-auto-2" and label == "dir":
                        check("the refusal says what to append",
                              "<SESSION>" in str(exc) or "session" in str(exc).lower(), str(exc))
        check("a refused identity creates no directory", os.listdir(root) == [], str(os.listdir(root)))


def test_one_tag_in_two_sessions_gets_two_directories() -> None:
    """The issue's own reproduction: same pool tag, same issue, two live agents."""
    with tempfile.TemporaryDirectory() as root:
        a = asp.agent_dir("stma-auto-2-issue-3606-aaaaaaaa", scratchpad=root)
        b = asp.agent_dir("stma-auto-2-issue-3606-bbbbbbbb", scratchpad=root)
        c = asp.agent_dir("stma-auto-2-issue-3607-aaaaaaaa", scratchpad=root)
        check("two sessions on one issue resolve differently", a != b, f"{a} == {b}")
        check("two issues of one session resolve differently", a != c, f"{a} == {c}")
        check("one session re-resolving gets its own directory back",
              a == asp.agent_dir("stma-auto-2-issue-3606-aaaaaaaa", scratchpad=root), a)
        # ...and the guard turns the first agent's clone into the second one's refusal.
        clone = os.path.join(a, "corpus")
        os.makedirs(clone)
        try:
            asp.assert_private(clone, "stma-auto-2-issue-3606-bbbbbbbb", scratchpad=root)
            check("a sibling's clone is refused", False, "no exception raised")
        except asp.ScratchpadCollision as exc:
            check("a sibling's clone is refused", True)
            check("the refusal names the sibling", "aaaaaaaa" in str(exc), str(exc))
        asp.assert_private(clone, "stma-auto-2-issue-3606-aaaaaaaa", scratchpad=root)
        check("the owner's own clone is accepted", True)
        # the legacy shape the issue measured: a bare dir is nobody's in particular
        legacy = os.path.join(root, "agent-stma-auto-2", "corpus")
        os.makedirs(legacy)
        try:
            asp.assert_private(legacy, "stma-auto-2-issue-3606-aaaaaaaa", scratchpad=root)
            check("a legacy bare-tag directory is refused", False, "no exception raised")
        except asp.ScratchpadCollision:
            check("a legacy bare-tag directory is refused", True)


def test_scan_reports_a_bare_tag_directory_as_shared() -> None:
    with tempfile.TemporaryDirectory() as root:
        os.makedirs(os.path.join(root, "agent-stma-auto-2", "corpus"))
        asp.agent_dir(ME, scratchpad=root)
        names = {os.path.basename(p) for p in asp.scan_shared(scratchpad=root)}
        check("scan reports the directory every holder of the tag shares",
              "agent-stma-auto-2" in names, str(names))
        check("scan does not report a sessioned directory",
              "agent-" + ME not in names, str(names))


def test_cli_reproduces_the_issue_and_now_refuses() -> None:
    with tempfile.TemporaryDirectory() as root:
        rc, out, err = _cli(["--agent-id", "stma-auto-2", "--scratchpad", root, "dir"])
        check("`dir` on a bare tag exits 1 and prints no path", rc == 1 and out == "", f"{rc} {out!r}")
        check("the refusal goes to stderr and names the fix", "REFUSED" in err and "session" in err.lower(), err)
        rc, out, err = _cli(["--agent-id", "stma-auto-2", "--scratchpad", root, "check",
                             os.path.join(root, "agent-stma-auto-2", "corpus")])
        check("`check` on a sibling's path no longer says OK", rc == 1 and "OK" not in out, f"{rc} {out!r}")
        rc, out, err = _cli(["--agent-id", ME, "--scratchpad", root, "dir"])
        rc2, out2, _ = _cli(["--agent-id", ME, "--scratchpad", root, "dir"])
        check("a sessioned identity resolves, idempotently", rc == 0 and rc2 == 0 and out == out2 and out != "", f"{out!r} {out2!r}")


def test_the_hook_and_the_tool_agree_on_what_a_sessioned_identity_is() -> None:
    """Two regexes, one rule: drift would let the hook bless a directory the tool refuses
    (or the reverse), which is the silent wrong answer this exists to stop."""
    hook_spec = importlib.util.spec_from_file_location(
        "shared_scratchpad_guard",
        os.path.join(HERE, "..", ".claude", "hooks", "shared-scratchpad-guard.py"))
    hook = importlib.util.module_from_spec(hook_spec)
    hook_spec.loader.exec_module(hook)
    for ident in [ME, OTHER, "stma-auto-2", "impl-4", "stma-auto-2-issue-3606",
                  "stma-auto-2-issue-3606-1a2b3c4", "stma-auto-2-issue-3606-1A2B3C4D",
                  "stma-auto-2-issue-3606-1a2b3c4dd", "x-0a0a0a0a"]:
        try:
            asp.resolve_agent_id(ident)
            tool_ok = True
        except asp.ScratchpadCollision:
            tool_ok = False
        hook_ok = bool(hook.OWNED.search(f"/scratchpad/agent-{ident}/x"))
        check(f"tool and hook agree on {ident!r}", tool_ok == hook_ok,
              f"tool accepts={tool_ok} hook owns={hook_ok}")


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
            ["path", "pr-body.md", "--agent-id", ME, "--scratchpad", root])
        rc_b, out_b, err_b = _cli(
            ["--agent-id", ME, "--scratchpad", root, "path", "pr-body.md"])
        check("options AFTER the subcommand work", rc_a == 0, err_a)
        check("options BEFORE the subcommand work", rc_b == 0, err_b)
        check("both spellings give the SAME path", out_a == out_b and bool(out_a),
              f"{out_a!r} != {out_b!r}")


def test_cli_check_exits_1_and_explains_on_a_shared_path() -> None:
    """A wrapper reads the exit code, so it has to discriminate."""
    with tempfile.TemporaryDirectory() as root:
        shared = os.path.join(root, "pr-body.md")
        open(shared, "w").close()
        rc, out, err = _cli(["check", shared, "--agent-id", ME,
                             "--scratchpad", root])
        check("check exits 1 on a shared path", rc == 1, f"rc={rc}")
        check("the refusal goes to stderr", "REFUSED" in err, err)
        mine = asp.agent_path("pr-body.md", agent_id=ME, scratchpad=root)
        open(mine, "w").close()
        rc2, _, err2 = _cli(["check", mine, "--agent-id", ME,
                             "--scratchpad", root])
        check("check exits 0 on my own path", rc2 == 0, err2)


def test_cli_refuses_rather_than_defaulting_without_an_identity() -> None:
    with tempfile.TemporaryDirectory() as root:
        rc, out, err = _cli(["path", "pr-body.md", "--scratchpad", root])
        check("no identity -> exit 1, not a path", rc == 1, f"rc={rc} out={out!r}")
        check("nothing is printed to stdout", out == "", out)


def test_no_test_suite_lives_where_nothing_globs_it() -> None:
    """A suite under `.claude/hooks/` gates nothing on its own.

    `pr-gate.yml`'s tools-tests job globs `tools/test_*.py` only. #3420 covered
    that by delegating from here; #3707 moved the hook suites into `tools/`
    instead, so they are discovered directly and delegating would run them
    twice. What is left to assert is that nobody puts one back.
    """
    root = pathlib.Path(HERE).parent
    strays = sorted(root.joinpath(".claude", "hooks").glob("test_*.py"))
    check("no suite hides under .claude/hooks", not strays,
          f"move to tools/: {[s.name for s in strays]}")
    for name in ("test_prefer_code_navigation.py", "test_shared_scratchpad_guard.py"):
        check(f"{name} is globbed by tools-tests",
              root.joinpath("tools", name).is_file(), name)


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
