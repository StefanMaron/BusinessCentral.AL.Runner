#!/usr/bin/env python3
"""A package-install step in a workflow must be bounded, and so must the release pack job (#5441).

`bc-tests.yml`'s `pack` job ("Release pack (dry, no publish)") had no `timeout-minutes`, so
GitHub's 360-minute default applied; one `apt-get install` hung for 79 minutes while the job
held the required `BC test matrix passed` context. The bounds, and the job durations they
were sized from, are in the PR that added this file.

The population is DERIVED, not listed: every step whose `run:` calls `apt-get install` (or
`apt install`) is checked, so a new install step is in scope the day it lands. The command
line is tokenised like a shell and the subcommand is the first argument that is not an option
or the value of a value-taking option, so `apt-get -o Dpkg::Lock::Timeout=60 install` is seen
(#5458); a quoted `sh -c`/`bash -c` argument and a `$(...)`/backtick body are scanned as scripts
of their own. A script that does not name apt is never tokenised, and one that names apt but
will not tokenise falls back to the old raw-text scan with a printed NOTE rather than refusing:
this guard gates every PR, so an unrelated heredoc must not be able to red it. Not covered: an
install behind `$PM`/`eval`, abbreviated long options, `aptitude` (different option table; nothing
here uses it). Two rules:

  1. each such step carries a step-level `timeout-minutes` of at most STEP_MAX;
  2. the one job named in BOUNDED_JOBS carries a job-level `timeout-minutes` of at most
     JOB_MAX.

The ceilings are CEILINGS, not the values the workflows use: an absurd value (360, the
default restated) must fail as surely as an absent key. Parse the YAML, never grep it --
comments in these files mention timeouts.

Third state (guards-need-a-third-state.md): no readable workflow directory, a workflow that
does not parse, zero install steps found, or a named job that no longer exists REFUSES (3)
rather than passing -- each is a moved file or a renamed job, not a clean tree.

Run: python3 tools/test_workflow_install_timeouts.py
Exit: 0 measured and fine, 1 measured and broken, 3 could not measure.
"""
from __future__ import annotations

import os
import re
import shlex
import sys
import tempfile

import yaml

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Ceilings in minutes. Healthy apt step: seconds, slowest healthy observed 12 min;
# healthy pack job: slowest observed 25 min. Both ceilings are a multiple of that, tight
# enough that GitHub's 360-minute default restated by hand still fails.
STEP_MAX = 30
JOB_MAX = 90

# (workflow file under .github/workflows, job id): jobs that must carry their own bound.
BOUNDED_JOBS = [("bc-tests.yml", "pack")]

# Package managers whose `install` is in scope. `aptitude` is not: its option set differs
# (-F/-w/-S take values), nothing here uses it, and a wrong option table would hide an install.
INSTALLERS = ("apt-get", "apt")
# Options that consume a value, so the value is not the subcommand (`apt-get -t install remove`).
# Short options follow getopt: the rest of the cluster is the value, else the next token.
VALUE_SHORT = frozenset("octa")
VALUE_LONG = frozenset({"--option", "--config-file", "--target-release", "--default-release",
                        "--host-architecture"})
SEPARATOR_CHARS = frozenset("();&|<>")
NOT_COMMANDS = ("echo", "printf")
# A shell's `-c` argument and a `$(...)` / backtick body are scripts of their own.
SHELLS = ("sh", "bash", "dash", "zsh")
MAX_DEPTH = 3
# Only a script naming apt is tokenised at all: any other `run:` (heredocs, PowerShell
# here-strings) can hold quotes a shell tokeniser rejects, and must never be able to refuse.
APT_WORD = re.compile(r"\bapt(-get)?\b")
# What an untokenisable script that names apt falls back to: the raw-text scan this guard
# shipped with (#5441). Weaker than the tokeniser, and stated as such in the output.
RAW_INSTALL = re.compile(r"\bapt-get\s+(?:-\S+\s+)*install\b")


class Untokenisable(Exception):
    """A `run:` script the shell tokeniser refuses; the guard cannot measure it."""


def _normalise(script):
    """One line of shell: unquoted comments dropped, unquoted newlines turned into `;`, and
    `\\<newline>` continuations joined. Quotes are tracked so a multi-line quoted string stays whole."""
    out, quote, prev, i, ticks = [], None, "\n", 0, False
    while i < len(script):
        ch = script[i]
        if quote == "'":
            quote = None if ch == "'" else quote
        elif quote == '"':
            if ch == "\\" and i + 1 < len(script):
                out.append(ch)
                i, ch = i + 1, script[i + 1]
            elif ch == '"':
                quote = None
        elif ch == "\\" and script[i + 1:i + 2] == "\n":
            i, ch = i + 1, " "
        elif ch == "\\" and i + 1 < len(script):
            out.append(ch)
            i, ch = i + 1, script[i + 1]
        elif ch in "'\"":
            quote = ch
        elif ch == "`":  # an unquoted backtick pair is a subshell: ` ( ` ... ` ) `
            ticks = not ticks
            out.append(" ( " if ticks else " ) ")
            prev, i = " ", i + 1
            continue
        elif ch == "#" and prev.isspace():
            while i < len(script) and script[i] != "\n":
                i += 1
            continue
        elif ch == "\n":
            out.append(" ; ")
            prev, i = "\n", i + 1
            continue
        out.append(ch)
        prev = ch
        i += 1
    return "".join(out)


def _segments(script):
    """Yields the argv-like token lists of each simple command in a `run:` script."""
    lex = shlex.shlex(_normalise(script), posix=True, punctuation_chars=True)
    lex.whitespace_split, lex.commenters = True, ""
    try:
        tokens = list(lex)
    except ValueError as exc:
        raise Untokenisable(f"{exc}: {script.strip()[:80]!r}") from exc
    seg = []
    for tok in tokens:
        if tok and all(c in SEPARATOR_CHARS for c in tok):
            if seg:
                yield seg
            seg = []
        else:
            seg.append(tok)
    if seg:
        yield seg


def _subcommand(args):
    """The first non-option argument, skipping the values of value-taking options."""
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--":
            return args[i + 1] if i + 1 < len(args) else None
        if a.startswith("--"):
            i += 2 if a in VALUE_LONG else 1  # `--opt=value` is not in VALUE_LONG: one token
            continue
        if a.startswith("-") and len(a) > 1:
            step = 1
            for j in range(1, len(a)):
                if a[j] in VALUE_SHORT:
                    step = 2 if j == len(a) - 1 else 1  # attached value, else the next token
                    break
            i += step
            continue
        return a
    return None


def _substitutions(tok):
    """Bodies of `$(...)` and backtick command substitutions inside one token."""
    bodies, i = [], 0
    while True:
        i = tok.find("$(", i)
        if i < 0:
            break
        depth, j = 1, i + 2
        while j < len(tok) and depth:
            depth += (tok[j] == "(") - (tok[j] == ")")
            j += 1
        bodies.append(tok[i + 2:j - 1 if depth == 0 else j])
        i = j
    parts = tok.split("`")
    bodies.extend(parts[1::2])
    return bodies


def _shell_scripts(seg):
    """The `-c` script argument of each `sh`/`bash`/`dash`/`zsh` word in the command."""
    for i, tok in enumerate(seg):
        if tok.rsplit("/", 1)[-1] not in SHELLS:
            continue
        for j in range(i + 1, len(seg) - 1):
            a = seg[j]
            if a.startswith("-") and not a.startswith("--") and "c" in a:
                yield seg[j + 1]
                break


def runs_install(script, depth=0):
    """True when any simple command in `script` is `apt-get`/`apt` with subcommand `install`,
    including inside a shell's `-c` argument or a command substitution."""
    if depth > MAX_DEPTH:
        raise Untokenisable("nested deeper than the guard follows")
    for seg in _segments(script):
        for tok in seg:
            for body in _substitutions(tok):
                if runs_install(body, depth + 1):
                    return True
        if seg[0] in NOT_COMMANDS:
            continue
        for inner in _shell_scripts(seg):
            if runs_install(inner, depth + 1):
                return True
        for i, tok in enumerate(seg):
            if tok.rsplit("/", 1)[-1] in INSTALLERS and _subcommand(seg[i + 1:]) == "install":
                return True
    return False


def step_installs(script):
    """(installs, note): tokenised when the script names apt; when it will not tokenise, the raw
    regex scan stands in and the note says so. A script without an apt word is never tokenised."""
    if not APT_WORD.search(script):
        return False, None
    try:
        return runs_install(script), None
    except Untokenisable as exc:
        return bool(RAW_INSTALL.search(script)), f"tokeniser refused ({exc}); raw-text scan used"


def _minutes(value):
    """A timeout-minutes value as an int, or None when it is not a plain positive int."""
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        return None
    return value


def check(root: str):
    """Returns (exit_code, messages)."""
    wf_dir = os.path.join(root, ".github", "workflows")
    try:
        names = sorted(n for n in os.listdir(wf_dir) if n.endswith((".yml", ".yaml")))
    except OSError as exc:
        return 3, [f"UNMEASURABLE: cannot list {wf_dir} ({exc})."]
    docs = {}
    for name in names:
        try:
            with open(os.path.join(wf_dir, name), encoding="utf-8") as fh:
                doc = yaml.safe_load(fh)
        except (OSError, yaml.YAMLError) as exc:
            return 3, [f"UNMEASURABLE: {name} could not be read as YAML ({exc})."]
        docs[name] = doc if isinstance(doc, dict) else {}

    failures, installs, notes = [], 0, []
    for name, doc in docs.items():
        jobs = doc.get("jobs")
        for job_id, job in (jobs.items() if isinstance(jobs, dict) else []):
            for i, step in enumerate((job or {}).get("steps") or []):
                run = step.get("run") if isinstance(step, dict) else None
                if not isinstance(run, str):
                    continue
                label = f"{name} job `{job_id}` step {i + 1} ({step.get('name', 'unnamed')})"
                found, note = step_installs(run)
                if note:
                    notes.append(f"NOTE: {label}: {note}.")
                if not found:
                    continue
                installs += 1
                got = step.get("timeout-minutes")
                m = _minutes(got)
                if got is None:
                    failures.append(f"{label}: no step-level timeout-minutes (cap {STEP_MAX}).")
                elif m is None or m > STEP_MAX:
                    failures.append(f"{label}: timeout-minutes {got!r} is not a positive "
                                    f"integer <= {STEP_MAX}.")

    if installs == 0:
        return 3, ["UNMEASURABLE: no `apt-get install` / `apt install` step found in any workflow. The "
                   "population is derived, so zero is a moved directory or a changed "
                   "spelling, not a clean tree."]

    for wf, job_id in BOUNDED_JOBS:
        job = ((docs.get(wf) or {}).get("jobs") or {}).get(job_id)
        if not isinstance(job, dict):
            return 3, [f"UNMEASURABLE: {wf} has no job `{job_id}`. Renamed? Update "
                       "BOUNDED_JOBS in this file."]
        got = job.get("timeout-minutes")
        m = _minutes(got)
        if got is None:
            failures.append(f"{wf} job `{job_id}`: no job-level timeout-minutes, so GitHub's "
                            f"360-minute default applies (cap {JOB_MAX}).")
        elif m is None or m > JOB_MAX:
            failures.append(f"{wf} job `{job_id}`: timeout-minutes {got!r} is not a positive "
                            f"integer <= {JOB_MAX}.")

    if failures:
        return 1, ["FAIL:"] + ["  " + f for f in failures] + notes
    return 0, [f"PASS: {installs} package-install step(s) bounded; "
               f"{len(BOUNDED_JOBS)} named job(s) bounded."] + notes


# ---- the refusal and failure paths, driven in throwaway trees -------------------------

def _tree(files):
    tree = tempfile.mkdtemp()
    wf = os.path.join(tree, ".github", "workflows")
    os.makedirs(wf)
    for name, text in files.items():
        with open(os.path.join(wf, name), "w", encoding="utf-8") as fh:
            fh.write(text)
    return tree


def _wf(job_extra="", step_extra="", run="sudo apt-get update && sudo apt-get install -y gcc"):
    return ("jobs:\n  pack:\n    runs-on: ubuntu-latest\n" + job_extra +
            "    steps:\n      - name: install\n" + step_extra +
            "        run: " + run + "\n")


def _scalar(run):
    """A `run:` value as YAML: a multi-line (or `#`-bearing) script goes in a block scalar."""
    if "\n" not in run and " #" not in run:
        return run
    return "|\n" + "".join("          " + line + "\n" for line in run.split("\n"))


def _wf_with(run, timeout=None):
    """A bounded plain install step (so the population is never empty), then one step running `run`."""
    extra = "" if timeout is None else f"        timeout-minutes: {timeout}\n"
    return ("jobs:\n  pack:\n    runs-on: ubuntu-latest\n    timeout-minutes: 60\n"
            "    steps:\n      - name: plain\n        timeout-minutes: 20\n"
            "        run: sudo apt-get install -y gcc\n"
            "      - name: variant\n" + extra + "        run: " + _scalar(run) + "\n")


# #5458: an option BEFORE the subcommand, with and without a value, in every spelling.
_VALUE_OPTION_RUNS = [
    ("-o spaced value", "sudo apt-get -o Dpkg::Lock::Timeout=60 install -y gcc"),
    ("-o then a flag", "sudo apt-get -o Acquire::Retries=3 -y install gcc"),
    ("-o attached value", "sudo apt-get -oDpkg::Lock::Timeout=60 install gcc"),
    ("--option spaced value", "sudo apt-get --option Dpkg::Lock::Timeout=60 install gcc"),
    ("--option=value", "sudo apt-get --option=Dpkg::Lock::Timeout=60 install gcc"),
    ("clustered -yo", "sudo apt-get -yo Dpkg::Lock::Timeout=60 install gcc"),
    ("-t value", "sudo apt-get -t jammy install gcc"),
    ("apt, not apt-get", "sudo apt -o Dpkg::Lock::Timeout=60 install -y gcc"),
    ("after && on one line", "sudo apt-get update && sudo apt-get -o X=1 install gcc"),
]

CASES = [
    ("control: both bounded", {"bc-tests.yml": _wf("    timeout-minutes: 60\n", "        timeout-minutes: 20\n")}, 0),
    ("step timeout absent", {"bc-tests.yml": _wf("    timeout-minutes: 60\n")}, 1),
    ("step timeout at the default", {"bc-tests.yml": _wf("    timeout-minutes: 60\n", "        timeout-minutes: 360\n")}, 1),
    ("job timeout absent", {"bc-tests.yml": _wf("", "        timeout-minutes: 20\n")}, 1),
    ("job timeout at the default", {"bc-tests.yml": _wf("    timeout-minutes: 360\n", "        timeout-minutes: 20\n")}, 1),
    ("job timeout is an expression", {"bc-tests.yml": _wf("    timeout-minutes: ${{ inputs.t }}\n", "        timeout-minutes: 20\n")}, 1),
    ("no install step anywhere", {"bc-tests.yml": _wf("    timeout-minutes: 60\n", run="echo hi")}, 3),
    ("named job missing", {"bc-tests.yml": "jobs:\n  other:\n    steps:\n      - run: sudo apt-get install -y x\n        timeout-minutes: 5\n"}, 3),
    ("workflow unparseable", {"bc-tests.yml": "jobs: [unclosed\n"}, 3),
    # the install is neither first on the line nor preceded only by bare flags
    ("value option, install not first non-option, timeout 10 -> ok", {"bc-tests.yml": _wf_with("sudo apt-get -o Dpkg::Lock::Timeout=60 install -y gcc", 10)}, 0),
    ("value option, timeout at the default", {"bc-tests.yml": _wf_with("sudo apt-get -o Dpkg::Lock::Timeout=60 install -y gcc", 360)}, 1),
    # the value of a value-taking option is not the subcommand, even when it spells `install`
    ("a value spelled install, subcommand remove", {"bc-tests.yml": _wf_with("sudo apt-get -t install remove gcc")}, 0),
    ("non-install subcommand after -o, unbounded", {"bc-tests.yml": _wf_with("sudo apt-get -o x=y update")}, 0),
    ("install only inside a quoted string", {"bc-tests.yml": _wf_with("echo 'run apt-get install later'")}, 0),
    ("install only in a trailing comment", {"bc-tests.yml": _wf_with("echo hi # apt-get install gcc")}, 0),
    ("install on a continued line", {"bc-tests.yml": _wf_with("sudo apt-get -o X=1 \\\n  install gcc")}, 1),
    ("install on the third line, after an apostrophe in a comment", {"bc-tests.yml": _wf_with("# don't skip this\nset -e\nsudo apt-get -y install gcc")}, 1),
    # (A) a script without an apt word is never tokenised, so it can never refuse the run
    ("heredoc body with an apostrophe, no apt", {"bc-tests.yml": _wf_with("cat <<'EOF'\nit's here\nEOF")}, 0),
    ("heredoc body with a lone double quote, no apt", {"bc-tests.yml": _wf_with("cat <<EOF\nsay \"hi\nEOF")}, 0),
    ("ANSI-C quoted escape, no apt", {"bc-tests.yml": _wf_with("echo $'it\\'s'")}, 0),
    ("pwsh here-string with an apostrophe, no apt", {"bc-tests.yml": _wf_with("$t = @'\nit's\n'@\nWrite-Output $t")}, 0),
    ("unterminated quote, no apt", {"bc-tests.yml": _wf_with("echo 'unterminated")}, 0),
    # (A) an untokenisable script that names apt degrades to the raw-text scan, not to exit 3
    ("heredoc with an apostrophe and an unbounded install", {"bc-tests.yml": _wf_with("cat <<'EOF' > x.md\nit's here\nEOF\nsudo apt-get -y install gcc")}, 1),
    ("heredoc with an apostrophe and a bounded install", {"bc-tests.yml": _wf_with("cat <<'EOF' > x.md\nit's here\nEOF\nsudo apt-get -y install gcc", 10)}, 0),
    ("unterminated quote and an unbounded install", {"bc-tests.yml": _wf_with("echo 'unterminated\napt-get install gcc")}, 1),
    # (B) a quoted script handed to a shell, and a command substitution, are scripts of their own
    ("bash -c double-quoted install", {"bc-tests.yml": _wf_with('bash -c "apt-get install -y x"')}, 1),
    ("sudo sh -c single-quoted install with -o", {"bc-tests.yml": _wf_with("sudo sh -c 'apt-get -o X=1 install x'")}, 1),
    ("bash -eo pipefail -c install", {"bc-tests.yml": _wf_with("bash -o pipefail -c 'apt-get install x'")}, 1),
    ("docker run ... sh -c install", {"bc-tests.yml": _wf_with("docker run --rm img sh -c 'apt-get install -y x'")}, 1),
    ("quoted $(...) install", {"bc-tests.yml": _wf_with('out="$(apt-get install -y x)"')}, 1),
    ("backtick install", {"bc-tests.yml": _wf_with("out=`apt-get install -y x`")}, 1),
    ("nested bash -c install", {"bc-tests.yml": _wf_with("bash -c \"sh -c 'apt-get install x'\"")}, 1),
    ("bash -lc (combined option) install", {"bc-tests.yml": _wf_with("bash -lc 'apt-get install x'")}, 1),
    ("install in a substitution inside echo", {"bc-tests.yml": _wf_with('echo "$(apt-get install -y x)"')}, 1),
    ("bash -c install, bounded", {"bc-tests.yml": _wf_with('bash -c "apt-get install -y x"', 10)}, 0),
    ("sh -c install, bounded", {"bc-tests.yml": _wf_with("sudo sh -c 'apt-get -o X=1 install x'", 10)}, 0),
    ("quoted $(...) install, bounded", {"bc-tests.yml": _wf_with('out="$(apt-get install -y x)"', 10)}, 0),
    ("echo of a string naming install", {"bc-tests.yml": _wf_with('echo "run apt-get install later"')}, 0),
    ("bash -c that only echoes install", {"bc-tests.yml": _wf_with("bash -c 'echo apt-get install'")}, 0),
    # (C) arms a mutation of the finder must not be able to remove unseen
    ("an option-looking token after -- is the subcommand", {"bc-tests.yml": _wf_with("sudo apt-get -- -x install")}, 0),
    ("-- then install", {"bc-tests.yml": _wf_with("sudo apt-get -- install gcc")}, 1),
    ("echo of an install is not an install", {"bc-tests.yml": _wf_with("echo apt-get install -y x")}, 0),
    ("absolute path to apt-get", {"bc-tests.yml": _wf_with("sudo /usr/bin/apt-get install -y x")}, 1),
] + [(f"unbounded: {n}", {"bc-tests.yml": _wf_with(r)}, 1) for n, r in _VALUE_OPTION_RUNS] + [
    (f"bounded: {n}", {"bc-tests.yml": _wf_with(r, 10)}, 0) for n, r in _VALUE_OPTION_RUNS]


def _self_test() -> int:
    import shutil

    failed = 0
    for name, files, want in CASES:
        tree = _tree(files)
        try:
            rc, msgs = check(tree)
        finally:
            shutil.rmtree(tree, ignore_errors=True)
        ok = rc == want
        failed += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {name}: exit {rc} (want {want}) | {msgs[0]}")
    # which path a script took: a script without an apt word is never tokenised (no note, even
    # when the shell would refuse it); one that names apt and will not tokenise says so.
    paths = [
        ("no apt word is not tokenised", "echo 'unterminated", (False, False)),
        ("apt word, untokenisable: noted, raw scan finds the install",
         "echo 'x\napt-get -y install gcc", (True, True)),
        ("apt word, tokenisable: no note", "apt-get -y install gcc", (True, False)),
    ]
    for name, script, (want_found, want_note) in paths:
        found, note = step_installs(script)
        ok = (found, note is not None) == (want_found, want_note)
        failed += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {name}: found={found} note={note is not None}")
    total = len(CASES) + len(paths)
    print(f"Total: {total}, Failed: {failed}, Passed: {total - failed}")
    return 1 if failed else 0


def main() -> int:
    rc, msgs = check(ROOT)
    print("\n".join(msgs))
    if rc == 0:
        print("\n--- refusal and failure paths ---")
        rc = _self_test()
    return rc


if __name__ == "__main__":
    sys.exit(main())
