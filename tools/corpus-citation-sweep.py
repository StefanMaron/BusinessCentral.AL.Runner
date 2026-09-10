#!/usr/bin/env python3
"""Which MERGED runner PRs cite a corpus PR that never merged? (#3674)

The gate this ships beside (`pr-gate.yml`'s corpus-pr-mergeable job) stops the
next one. This answers the other half of the same question, backwards: over pull
requests that already merged, whose cited corpus PR is not MERGED today — so
whose BC claim no service tier ever adjudicated.

It is a sweep, not a gate. Run it, read the list, and file one catch-up issue per
SURFACE (`.github/ISSUE_TEMPLATE/runner-gap.md`), after checking the open queue
for one that already covers it (`file-issues-for-gaps.md`). Several runner PRs
can cite one corpus PR and one surface; that is one issue, not three.

    tools/corpus-citation-sweep.py                 # the last 400 merged PRs
    tools/corpus-citation-sweep.py --limit 1000

WHAT IT DOES NOT PARSE
----------------------
The `Corpus-PR:` regex lives in .github/scripts/check_corpus_linkage.sh and is
reached through .github/scripts/corpus_pr_state.py, the same module the gate and
tools/ci-wait.py use. The one thing this file decides for itself is a PREFILTER:
a body is handed to that parser only if it contains the marker text at all,
case-insensitively. That can only skip bodies in which every mode of the real
parser would find nothing — the marker is a literal prefix of both its regexes —
and it exists because the parser is a subprocess per body and most bodies carry
no declaration.

Exit codes
----------
    0  every cited corpus PR merged
    1  at least one merged runner PR cites a corpus PR that did not
    3  a read failed, so the sweep is incomplete and its zero means nothing
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = "StefanMaron/BusinessCentral.AL.Runner"
STATE_MODULE = os.path.abspath(
    os.path.join(HERE, "..", ".github", "scripts", "corpus_pr_state.py"))


def load_state_module(path: str | None = None):
    """Import corpus_pr_state.py, or None."""
    target = os.path.abspath(path or STATE_MODULE)
    try:
        spec = importlib.util.spec_from_file_location("corpus_pr_state_for_sweep", target)
        if spec is None or spec.loader is None:
            return None
        mod = importlib.util.module_from_spec(spec)
        # Before exec_module: the module declares a @dataclass, which resolves
        # annotations through sys.modules[cls.__module__].
        sys.modules[spec.name] = mod
        spec.loader.exec_module(mod)
        return mod
    except Exception:
        return None


def has_marker(body: str | None) -> bool:
    """The prefilter. A superset of what the real parser can match."""
    return "corpus-pr:" in (body or "").lower()


def unmerged_citations(prs, numbers_for, state_for):
    """[(runner PR, corpus number, state, detail)] for every citation not MERGED.

    `numbers_for(body)` -> (numbers|None, why); `state_for(number)` -> Entry-like
    with `.state` and `.detail`. Both are injected so this stays a pure decision
    over data, which is what the unit tests drive.
    """
    findings = []
    for pr in prs:
        body = pr.get("body") or ""
        if not has_marker(body):
            continue
        numbers, why = numbers_for(body)
        if numbers is None:
            findings.append((pr, None, "UNREADABLE", why))
            continue
        for number in numbers:
            entry = state_for(number)
            if entry.state != "MERGED":
                findings.append((pr, number, entry.state, entry.detail))
    return findings


def gh_json(args: list[str]):
    p = subprocess.run(["gh", *args], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    out = "\n".join(l for l in (p.stdout or "").split("\n") if not l.startswith("mise "))
    if p.returncode != 0:
        return None, ((p.stderr or out).strip() or "gh failed")
    try:
        return json.loads(out), ""
    except Exception as exc:
        return None, f"could not parse gh output: {exc!r}"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--limit", type=int, default=400,
                    help="how many merged pull requests to sweep (newest first)")
    args = ap.parse_args(argv[1:])

    module = load_state_module()
    if module is None:
        print("could not import .github/scripts/corpus_pr_state.py; the sweep cannot "
              "read a body without the parser it owns. This is not an empty result.",
              file=sys.stderr)
        return 3

    prs, why = gh_json(["pr", "list", "--repo", REPO, "--state", "merged",
                        "--limit", str(args.limit), "--json",
                        "number,title,body,mergedAt,url"])
    if prs is None:
        print(f"could not list merged pull requests: {why}", file=sys.stderr)
        return 3
    print(f"swept {len(prs)} merged pull request(s) on {REPO}")

    cache: dict = {}

    def state_for(number: int):
        if number not in cache:
            payload, read_why = module.fetch_pull(number)
            if payload is None:
                cache[number] = module.Entry(number, "UNREADABLE", "", read_why)
            else:
                state, detail = module.classify(payload)
                cache[number] = module.Entry(number, state,
                                             str(payload.get("head") or ""), detail)
        return cache[number]

    findings = unmerged_citations(prs, module.corpus_pr_numbers, state_for)

    cited = sum(1 for pr in prs if has_marker(pr.get("body")))
    print(f"{cited} of them declare a Corpus-PR line")
    if not findings:
        print("every cited corpus pull request has merged.")
        return 0

    print("\nmerged runner PRs whose cited corpus PR did NOT merge:")
    for pr, number, state, detail in findings:
        where = f"corpus #{number}" if number else "corpus PR unreadable"
        print(f"  runner #{pr['number']} ({pr.get('mergedAt', '')[:10]}) -> {where}: "
              f"{state} -- {detail}")
        print(f"      {pr.get('title', '')}")
    print("\nFile one catch-up issue per SURFACE, not per runner PR "
          "(.github/ISSUE_TEMPLATE/runner-gap.md), after searching the open queue "
          "for one that already covers it.")
    # Same ordering as the gate: a definite finding is a verdict and outranks a
    # refusal, so exit 3 only when EVERY finding is one the sweep could not read.
    definite = [f for f in findings if f[2] != "UNREADABLE"]
    return 1 if definite else 3


if __name__ == "__main__":
    sys.exit(main(sys.argv))
