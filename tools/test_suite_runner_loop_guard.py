#!/usr/bin/env python3
"""The exit-code-collapse guard must key on structure, not a name allowlist (#4362).

`test_suite_runner_loops_preserve_exit_codes.py` fails when a workflow
reintroduces #4359's `for s in "${suites[@]}"; do python3 "$s" || rc=1; done`
inline. It used to key on an allowlist of variable names plus a literal
integer, so a verbatim reconstruction passed whenever either half was spelled
differently -- measured at 9a7ba65d, 1 of 10 spellings caught, and the three
sharpest MISSES (`rc=$((rc+1))`, `rc=true`, `{ rc=1; }`) used the SANCTIONED
name, so a reader checking "does it catch `rc=`?" concluded it was covered.

This suite pins both halves of the fix, because only the pair is a fix:

  * the WIDENING -- every spelling in #4362's table is caught, whatever the
    variable is called and whatever it is assigned;
  * the DISCRIMINATION -- the idioms that legitimately appear in this tree stay
    silent. `|| rc=$?` PRESERVES the status (bc-tests.yml does this five times
    and then `exit "$rc"`s the real code), and `|| true` outside a suite loop is
    a grep over a log. A guard that reddened those would have traded a false
    negative for a false positive, which is the trade #4360's docstring
    explicitly declined.

Both directions are the point. A widening test alone proves only that the
pattern matches MORE; it takes the controls to prove it still matches the right
things (`.claude/rules/tdd.md`, "a mutation that reds everything proves
coverage exists; one that reds exactly the right subset proves the tests
discriminate").

Exit codes: 0 clean, 1 an assertion failed, 3 could not measure.
"""

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))

try:
    from test_suite_runner_loops_preserve_exit_codes import (
        DQ_STRING,
        HEREDOC_OPEN,
        LOOP_OPEN,
        SUITE_GLOB,
        scan_text,
    )
except ImportError as exc:  # the guard is gone or unimportable: unmeasured
    print(f"UNMEASURED: cannot import the guard under test: {exc}", file=sys.stderr)
    print("1 check group(s) could not be measured; nothing here is a pass")
    raise SystemExit(3)


FAILURES: list[str] = []
PASSED = 0


def check(label: str, ok: bool, detail: str = "") -> None:
    if ok:
        global PASSED
        PASSED += 1
        print(f"ok   - {label}")
    else:
        print(f"FAIL - {label}{(': ' + detail) if detail else ''}")
        FAILURES.append(label)


def loop(fallback: str) -> str:
    """A pr-gate-shaped suite loop whose body ends in `|| <fallback>`."""
    return (
        "jobs:\n"
        "  tools-tests:\n"
        "    steps:\n"
        "      - run: |\n"
        "          shopt -s nullglob\n"
        '          suites=(tools/test_*.py)\n'
        "          rc=0\n"
        '          for s in "${suites[@]}"; do\n'
        '            python3 "$s" || ' + fallback + "\n"
        "          done\n"
        "          exit $rc\n"
    )


# ---------------------------------------------------------------- the widening
#
# #4362's own table, re-derived. Every row collapses exit 3 into a failure
# exactly as #4359 described, and every row must now be a finding. The first is
# the spelling that actually recurred three times; the rest are the ones the
# allowlist let through.
MUST_CATCH = [
    "rc=1",            # the spelling that recurred; the only one the old pattern caught
    "bad=1",           # not in the name allowlist
    "result=1",
    "exit_code=1",
    "failures=1",
    "any_failed=1",
    "rc=$((rc+1))",    # sanctioned NAME, non-literal value
    "rc=true",         # sanctioned NAME, non-integer value
    "{ rc=1; }",       # sanctioned NAME and value, brace between `||` and it
    "true",            # swallowed outright, no variable at all
]

for fallback in MUST_CATCH:
    found = scan_text("evil.yml", loop(fallback))
    check(
        f"a suite loop ending `|| {fallback}` is a finding",
        len(found) == 1,
        f"expected 1 finding, got {len(found)}: {found}",
    )

# The same defect spelled across a `case`, which is how pr-gate.yml's
# github-scripts-tests job had it -- two arms, two discards, two findings.
case_loop = (
    "      - run: |\n"
    "          suites=(.github/scripts/test_*.py)\n"
    '          for s in "${suites[@]}"; do\n'
    '            case "$s" in\n'
    '              *.py) python3 "$s" || rc=1 ;;\n'
    '              *)    bash "$s"    || ret=9 ;;\n'
    "            esac\n"
    "          done\n"
)
check(
    "both arms of a case-dispatching suite loop are findings",
    len(scan_text("evil.yml", case_loop)) == 2,
    f"got {scan_text('evil.yml', case_loop)}",
)


# ----------------------------------------------------------- the discrimination
#
# Each of these is real code from this repository's workflow tree, or the
# sanctioned replacement. A finding here is a false positive, and a false
# positive is worse than the gap being closed: it reds honest code, and #4360's
# docstring declined the wider shape for exactly this reason.

# `|| rc=$?` PRESERVES the status. bc-tests.yml uses it five times and then
# discriminates on the captured code. This is the single most important control
# in the file: the naive widening flags all five.
preserve = (
    "      - run: |\n"
    '          suites=(tools/test_*.py)\n'
    '          for s in "${suites[@]}"; do\n'
    "            rc=0\n"
    '            python3 "$s" || rc=$?\n'
    '            if [ "$rc" -eq 3 ]; then echo "unmeasured"; fi\n'
    "          done\n"
)
check(
    "`|| rc=$?` inside a suite loop is NOT a finding (it preserves the code)",
    scan_text("ok.yml", preserve) == [],
    f"got {scan_text('ok.yml', preserve)}",
)

# The sanctioned caller, which is what the fix for #4359 put there.
sanctioned = (
    "      - run: |\n"
    '          suites=(tools/test_*.py)\n'
    '          for s in "${suites[@]}"; do :; done\n'
    '          bash .github/scripts/run_guard_suites.sh "tools/test_*.py" "${suites[@]}"\n'
)
check(
    "calling run_guard_suites.sh is NOT a finding",
    scan_text("ok.yml", sanctioned) == [],
    f"got {scan_text('ok.yml', sanctioned)}",
)

# `|| true` OUTSIDE a suite loop. Measured: five such lines in this tree, all
# legitimate -- greps over a log, a sed pipeline, a `git fetch`. The context
# gate is the only thing keeping them silent.
outside = (
    "      - run: |\n"
    "          command grep -E '^(PASS|FAIL) ' eviction.log || true\n"
    '          git fetch --quiet origin "$BRANCH" || true\n'
)
check(
    "`|| true` outside any loop is NOT a finding",
    scan_text("ok.yml", outside) == [],
    f"got {scan_text('ok.yml', outside)}",
)

# `|| true` inside a loop that is NOT over guard suites. coverage-demo.yml:53
# is exactly this, and it is why "any loop" cannot be the context gate -- the
# gate has to be a loop over guard SUITES.
other_loop = (
    "      - run: |\n"
    "          for fixture in RecordTriggerXRec CoverageBranch; do\n"
    '            OUTPUT=$(./al-runner --coverage "Fixtures/${fixture}" 2>&1) || true\n'
    '            echo "$OUTPUT"\n'
    "          done\n"
)
check(
    "`|| true` in a non-suite loop is NOT a finding",
    scan_text("ok.yml", other_loop) == [],
    f"got {scan_text('ok.yml', other_loop)}",
)

# A comment naming the shape. The #4360 fix deliberately left several in
# pr-gate.yml explaining what was removed; a guard that flagged its own
# explanation would be unfixable.
commented = (
    "      - run: |\n"
    '          suites=(tools/test_*.py)\n'
    '          for s in "${suites[@]}"; do\n'
    "            # the `|| rc=1` this replaces had #4359's defect\n"
    '            bash .github/scripts/run_guard_suites.sh "x" "$s"\n'
    "          done\n"
)
check(
    "a comment quoting `|| rc=1` is NOT a finding",
    scan_text("ok.yml", commented) == [],
    f"got {scan_text('ok.yml', commented)}",
)

# A heredoc body is data. This guard's own fixtures embed the bad shape, and a
# workflow writing a script with a heredoc must be able to do the same.
heredoc = (
    "      - run: |\n"
    '          suites=(tools/test_*.py)\n'
    '          for s in "${suites[@]}"; do\n'
    "            cat > example.sh <<'SH'\n"
    '            python3 "$s" || rc=1\n'
    "SH\n"
    "          done\n"
)
check(
    "the bad shape inside a heredoc body is NOT a finding",
    scan_text("ok.yml", heredoc) == [],
    f"got {scan_text('ok.yml', heredoc)}",
)

# After the loop closes, the region closes with it.
after = (
    "      - run: |\n"
    '          suites=(tools/test_*.py)\n'
    '          for s in "${suites[@]}"; do :; done\n'
    "          command grep something log.txt || true\n"
)
check(
    "a discard AFTER the suite loop's `done` is NOT a finding",
    scan_text("ok.yml", after) == [],
    f"got {scan_text('ok.yml', after)}",
)


# ------------------------------- the REAL file, with the defect put back into it
#
# THE ASSERTION THIS SUITE WAS MISSING, and the gap underneath the bug it caught
# (#4362, found in review of PR #4377). Every other assertion here runs on a
# synthetic fixture, and the one assertion that did touch the real tree is a
# NEGATIVE one -- "nothing is flagged" -- which passes whatever the code does if
# the walk never reaches the lines. It did not: instrumented, 0 of 4,920 real
# lines across all 15 workflow files ever sat inside an open suite region,
# because a heredoc skip armed on `echo "messages<<PR_COMMITS_EOF"` (a STRING,
# not a heredoc operator) swallowed 500 of pr-gate.yml's 592 lines. The guard
# was blind on the one file that carried all three original copies of #4359's
# defect, and every fixture assertion stayed green.
#
# So: reintroduce the defect into a copy of the REAL pr-gate.yml, and require a
# finding. A fixture cannot stand in for this -- the whole failure was that the
# fixtures and the real file differ in a way the fixtures cannot express.
PR_GATE = ROOT / ".github" / "workflows" / "pr-gate.yml"
if not PR_GATE.is_file():
    print(f"UNMEASURED: no pr-gate.yml at {PR_GATE}", file=sys.stderr)
    print("1 check group(s) could not be measured; nothing here is a pass")
    raise SystemExit(3)

REAL = PR_GATE.read_text(encoding="utf-8")
CALL = 'bash .github/scripts/run_guard_suites.sh "tools/test_*.py" "${suites[@]}"'
if CALL not in REAL:
    # The sanctioned call moved or was reworded. That is not a pass: this
    # assertion cannot be made, and saying so is the third state.
    print(f"UNMEASURED: the sanctioned call is not in {PR_GATE.name}; "
          "re-anchor this assertion", file=sys.stderr)
    print("1 check group(s) could not be measured; nothing here is a pass")
    raise SystemExit(3)


def with_defect(fallback: str) -> str:
    """The real pr-gate.yml with its sanctioned call replaced by #4359's loop."""
    return REAL.replace(
        "          " + CALL,
        '          rc=0\n'
        '          for s in "${suites[@]}"; do\n'
        '            echo "=== $s"\n'
        f'            python3 "$s" || {fallback}\n'
        "          done\n"
        "          exit $rc",
    )


for fallback in MUST_CATCH:
    found = scan_text("pr-gate.yml", with_defect(fallback))
    check(
        f"the REAL pr-gate.yml with `|| {fallback}` put back is a finding",
        len(found) >= 1,
        "0 findings -- the walk is not reaching pr-gate.yml's suite job "
        "(a latched heredoc skip does exactly this)",
    )

# The control for the ten above: the file AS IT IS must stay silent, or they
# would pass on a guard that simply flags everything in pr-gate.yml.
check(
    "the REAL pr-gate.yml, unmodified, is NOT a finding",
    scan_text("pr-gate.yml", REAL) == [],
    f"got {scan_text('pr-gate.yml', REAL)}",
)

# And the walk must actually REACH the suite job -- the property whose absence
# made the negative assertion vacuous. Asked of the GUARD's own walk rather than
# a copy of it: a probe that re-implements the scan cannot observe the scan
# being blinded, which is the mistake that produced this whole section. Inject a
# discard into the real file at the suite job and require the guard to see it;
# if the walk never gets there, it cannot.
_reachable = scan_text("pr-gate.yml", with_defect("__reach_probe=1"))
check(
    "the guard's own walk reaches pr-gate.yml's suite job",
    len(_reachable) >= 1,
    "0 findings for an injected discard: the scan is blind on this file "
    "(a latched heredoc skip, a moved anchor, or a region that never opens)",
)


# ------------------------------------------------------- the live tree is clean
#
# The measurement #4362 asked for, kept as an assertion rather than a paragraph:
# the widened pattern must flag nothing in the real workflow tree. If a future
# workflow trips it legitimately, this is where that shows up.
WORKFLOWS = ROOT / ".github" / "workflows"
if not WORKFLOWS.is_dir():
    print(f"UNMEASURED: no workflow directory at {WORKFLOWS}", file=sys.stderr)
    print("1 check group(s) could not be measured; nothing here is a pass")
    raise SystemExit(3)

live = []
for p in sorted(WORKFLOWS.glob("*.yml")) + sorted(WORKFLOWS.glob("*.yaml")):
    live.extend(scan_text(str(p.relative_to(ROOT)), p.read_text(encoding="utf-8")))
check(
    "the widened pattern flags nothing in the real workflow tree",
    live == [],
    f"false positive(s): {live}",
)


print()
if FAILURES:
    print(f"0 passed, {len(FAILURES)} failed")
    for f in FAILURES:
        print(f"  - {f}")
    raise SystemExit(1)
print(f"{PASSED} passed, 0 failed")
raise SystemExit(0)
