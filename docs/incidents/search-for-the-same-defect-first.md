# Incidents behind `search-for-the-same-defect-first.md`

## The measurement that produced the rule

Taken while the repository owner was asking whether the metadata-conversion cluster had been
filed more than once. The `area: metadata-conversion` label had been applied by a sweep keyed on
the `blocked-by: metadata-emitter` label — so the sweep inherited that label's blind spot, and
**three issues on the same route carried no area label at all**:

| issue | why the label sweep missed it |
|---|---|
| **#3568** — the SymbolReference derivation disagrees with BC's metadata emitter on **71 more members** | filed *from the first run of the harness built for #3533*, so it is the direct continuation of that work — and it carried **no labels whatsoever** |
| **#3590** — `BuildNCLMetaTable` swallows every construction failure into a cached null | labelled `bug`, which says what it is and not what it belongs to |
| **#3491** — 2,702 lines re-implementing BC's own AL expression parsers | a research issue, so no `status:` and no area |

Three of twenty-three. #3568 is the one that mattered most, because it is the same measurement
programme as the issue it was missing from.

**What generalises:** a label is only as good as the sweep that applied it, and a sweep is built
from whatever the sweeper happened to key on. A label set derived from another label inherits
that label's omissions, and nothing in the result looks incomplete.

## Why the search keys are the four in the rule

Each of the three above would have been found by a different key, which is why the rule asks for
at least three:

- **#3568** by its **measurement** — `71 members` is a distinctive string, and whoever files a
  sibling almost certainly quotes their own count too.
- **#3590** by its **mechanism** — "swallowed into a cached null" shares no vocabulary with the
  issue it duplicates.
- **#3491** by neither label nor title, only by the **symbol** its work touches.

A title search using the words *you* would have chosen finds none of them.

## The confirm-every-empty-result clause

Added because two of this repository's search tools return a clean zero when they have failed
rather than when nothing matched: `grep` here resolves to a shell **function** that rejects `-E`
and exits 0 with no output, and `rg` skips dot-directories unless passed `--hidden` — which is
where everything governing agent behaviour lives. `CLAUDE.md` § 3 and § 3b own both.

`gh issue list --search` also matches differently from a `--jq` filter over titles, so the two
disagree on the same query. A zero from one is not evidence.

## Why the rule was untracked for so long (#3888)

The rule file lived in exactly one working tree, auto-loaded into every agent session on that
box, and was never committed: 4,771 bytes, not tracked, not ignored, no history, no
`History:` line, and no incidents file. Agents there obeyed an instruction no reviewer reading
the repository could see, and a fresh clone silently did not have it.

It is the shape `guards-need-a-third-state.md` describes, one level up: someone searching the
repository for "where is this written down?" gets a correct empty answer about a rule that is
actively in force.

**The class was checked, not just the instance.** `git ls-files --others --exclude-standard
.claude/` returned exactly one path — this file — so no other rule, skill, agent or hook was in
the same state.
