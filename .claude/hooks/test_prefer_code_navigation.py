#!/usr/bin/env python3
"""Unit tests for prefer-code-navigation.py.

The hook is advisory, so "did it fire" is the only observable behaviour: it
prints the reminder to stderr and always exits 0. These tests assert on that
stderr, against synthetic Bash payloads.

Usage: python3 test_prefer_code_navigation.py
Exits 0 when every case passes, 1 on the first failure.
"""

import json
import pathlib
import subprocess
import sys

HOOK = pathlib.Path(__file__).resolve().parent / "prefer-code-navigation.py"

CASES = []


def case(name, command, should_fire, tool="Bash"):
    CASES.append((name, command, should_fire, tool))


# --- reads of C# sources: the dominant cost, and what this hook missed ---
# Measured 2026-09-02 over one session: 533 sed/cat/head reads against 5 grep/rg
# calls. The hook matched only grep/rg/ag, so it guarded the 5 and ignored the 533.
case("sed -n range over a C# file", "sed -n '600,680p' AlRunner/Patches/NavReportSync.cs", True)
case("cat a C# file", "cat AlRunner/Patches/RequestPageTestPage.cs", True)
case("head a C# file", "head -60 AlRunner/BcRuntime.cs", True)
case("tail a C# file", "tail -40 AlRunner/Program.cs", True)
case("awk over a C# file", "awk 'NR>100 && NR<200' AlRunner/Patches/RecordPatches.cs", True)

# --- searches: the behaviour that already worked, and must keep working ---
case("rg over the C# tree", "rg -n 'IsProcessingOnly' --glob '*.cs' AlRunner", True)
case("grep with an explicit .cs include", "command grep -rn Foo --include=*.cs AlRunner", True)

# --- must NOT fire: these are legitimately the shell's job ---
case("build output piped to tail names AlRunner but reads no .cs",
     "dotnet build AlRunner -c Release | tail -5", False)
case("running the runner and trimming output",
     "dotnet run --project AlRunner -c Release -- bundle --out x.json | head -20", False)
case("reading a log", "sed -n '1,50p' /tmp/run.log", False)
case("reading AL sources", "command grep -n 'SaveAsXml' tests/al-language/foo.al", False)
case("reading a results JSON", "cat scratchpad/results.json", False)
case("git log", "git log --oneline -5 -- AlRunner", False)
case("writing a C# file with a heredoc must not nudge",
     "cat > AlRunner/Patches/New.cs <<'EOF'\nclass X {}\nEOF", False)
case("redirecting into a C# file must not nudge",
     "sed 's/a/b/' in.txt > AlRunner/Patches/Out.cs", False)
case("a non-Bash tool is ignored", "sed -n '1,10p' AlRunner/Program.cs", False, tool="Read")


def run():
    failures = 0
    for name, command, should_fire, tool in CASES:
        payload = json.dumps({"tool_name": tool, "tool_input": {"command": command}})
        r = subprocess.run(
            [sys.executable, str(HOOK)], input=payload, capture_output=True, text=True
        )
        fired = "Code-navigation reminder" in r.stderr
        ok = (fired == should_fire) and r.returncode == 0
        if ok:
            print(f"PASS  {name}")
        else:
            failures += 1
            print(f"FAIL  {name}")
            print(f"      expected fire={should_fire}, got fire={fired}, exit={r.returncode}")
            print(f"      cmd: {command!r}")

    print(f"\n{len(CASES) - failures}/{len(CASES)} passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(run())
