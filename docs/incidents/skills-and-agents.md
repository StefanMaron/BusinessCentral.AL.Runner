# Figures moved out of the skills and agent definitions (#4540)

`.claude/skills/*/SKILL.md` and `.claude/agents/*.md` used to carry these figures inline. Each one
changes without anyone editing the sentence that states it, or is frozen to one run, so the
prose now keeps the claim and the citation and the figure lives here. None of them is a current
value: every row is a reading taken at one moment, named by where and when it was taken.
Re-derive before quoting any of them.

Figures already recorded elsewhere in `docs/incidents/` (for example the #4284 duplicate-review
counts in `check-open-prs-before-claiming.md`, or the navigation-tool call counts in
`CLAUDE.md.md`) are not repeated here.

## Review throughput — `orchestrating-a-session`, `autonomous-cycle`, `reviewer.md`

One attended session, 2026-09-06:

| agent | work | wall | rate |
|---|---|---|---|
| reviewer (runner PRs) | 6 PRs in one pass | 93 min | ~15.6 min/PR |
| reviewer (corpus PRs) | 3 PRs in one pass | 64 min | ~21 min/PR |
| implementation agent | 1 PR each | 35-85 min | ~1 PR/hour |

From that: one reviewer sustained about 4 PRs/hour against 5-6 PRs/hour from six implementation
agents, which is where the old "one reviewer per four implementation agents" came from. In the
same session a batch of six took 93 minutes, during which three PRs from the brief merged and two
heads moved, so two of six verdicts came back "no verdict on current head". Implementation agents
waited 15-25 minutes on CI when they were allowed to.

Other review-side readings: #3978 was armed at exit 2 and two BC legs reported
`Failed: 2, Passed: 5506` twenty minutes later; on #4006 two armed PRs sat red for 12 and 119
minutes on a night with nineteen armed at once; #4338 measured 26 of 150 verdict comments ending
in an attribution footer.

## Comment density — `reviewer.md` § 7

Read once, and stale since: comment prose was 46% of non-blank lines in `AlRunner/` (54,520
comment lines, 63,256 code, 295 files), blocks over ten lines held 60% of the comment mass, and 23
of the last 26 commits touching `AlRunner/` added more comment than code. #4347's worked example,
PR #4336 (merge base `161a4d4a`, read at `origin/main` = `07787531`, 84 commits later): three-dot
`+19 comment / +9 code`, two-dot `+78 / +218`; walking `origin/main` back gave roughly 24x at 84
commits, 12x at 64 and 8x at 24. The review-driven delta on #4336 was `+9 comment / +0 code`.
`tools/comment-density.py`, run over `AlRunner/`, gives the current tree.

## Measurement anecdotes — `reviewer.md`, `orchestrating-a-session`

- A pass count moved 873 → 925 on unchanged code across a rebuild.
- Identical work measured 1.9 s and 3.1 s with agents running; instructions-retired held to ±0.1%.
- A regression claimed at 47% was contamination from concurrent runs.
- A swallowed exception dropped 90 of 96 table extensions.
- Removing a 612-failure wall moved 464 onto a different wall and turned 127 green.
- Filtering by the bc-linux container comparison hid a 102-test cluster worth +93 when fixed.

## The unattended loop — `autonomous-cycle`

- 2026-09-05: nine agents in parallel for about ninety minutes; 4,905 Base App members measured to
  pin an identifier-mangling rule to seven names; a cascade of 47 failures separated into one
  defect; at least four confident conclusions wrong in the same session.
- Process-work selection, measured 2026-09-22 over seven days (#4477): process/tooling was 21-36%
  of merged PRs against a backlog that was 19% process (27 of 145 open issues), roughly 1.1x to
  1.9x over-selection. Ten classifiers over the same week spanned 16-43%; the author's own 44% did
  not reproduce; 16% against 19% is 0.84x, under-selection.
- Box incidents: a skipped cleanup step across ~20 agents filled a 7.7 GB tmpfs; `--reap` left 5
  worktrees and 0.8 GiB before `--reap-carried` (#4419); the `impl-69` counter left 82 worktrees
  and 10 GB; a coordinator diagnosed from a tree 40+ commits behind and misdirected four agents;
  an 18-day-old stray graph answered `No matching nodes found.` where the correct one returned 11
  nodes.
- Budget: one panel sample read 35% → 37% of a session across 8 minutes at 9–12 concurrent
  agents (about 15 points per hour); a ccusage block `%` was read as "at the cap" while the
  authoritative figure was 37%; of 738,650,170 tokens in one day, 723,088,774 (97.9%) were cache
  reads and 8,761 were input.
- The owner held 22 open issues self-assigned when the resume rule was written; 14 issues and a
  comment were filed under the owner's name without an agent marker in the session the skill was
  written from.
- Per-worker memory after the GC tuning: about 1.1 GB without test data, ~2.3 GB with it.

## Microsoft buckets — `running-ms-test-buckets`

Counted on the 28.1.49838.53507 platform artifact (#3409): 34 `Tests-*` buckets, 32 non-empty,
40,530 `[Test]` methods. Older notes say "about 40,550": `ParallelFanOut.cs` and the two
`ParallelFanOut*TimeoutTests` describe one past run that totalled that, and stay as recorded.

- Tests-SMB (1,027 tests): 259 passing without test data, 595 with it; a later 28.1 run with
  1,028 discovered read 727 / 286 / 15 with and without `--test-data-normalize-company`, and the
  flag reported `1 of 1 row(s) changed (was 'EUR')` in both.
- No-test-data run of 29,514 classified failures, top clusters: 2690 `Order Nos.` (Purchases &
  Payables), 2214 General Posting Setup, 2020 `Order Nos.` (Sales & Receivables), 1507
  `Invoice Nos.`, 1001 Unit of Measure — roughly 40% of all failures were missing setup data.
- Full Tests-ERM with and without the normalize flag: 9,497 tests both arms; 6,691 → 6,709 pass,
  2,790 → 2,772 fail, 16 error; +18 (+0.19 points), 11 of them tests Microsoft never runs.
  Codeunit 134157 3/6 → 6/6, codeunit 134880 22/28 → 26/28, the 16-test exchange-rate cluster
  unchanged. Codeunit 134157 in isolation: 3 pass / 3 fail → 6 / 0.
- Disabled tests: 12,018 of 40,828 `[Test]` methods listed in `src/DisabledTests/`, so Microsoft
  runs 28,810; correcting for it moved the headline from 59.2% to 60.3% (59.1% on their disabled
  tests, 66.9% on the live ones).
- `microsoft/BCApps` `src/DemoTool/`: 315 files (95 png, 94 jpg, 32 gif), zero `.al`. Shipped Base
  Application 28.1: 8,026 AL files, 1,691 codeunits, 2,610 pages, zero in 101000–101999. 25
  `DemoDataConfig.xml` files in BCApps (W1 plus 24 country layers).
- The ACY balance miss: debits `54,426.58` against payables `-54,426.57`.
- Cascade example: 46 of 47 failures from one test that renamed a row. A killed run's inconsistent
  cache cost 76% of passing tests, and three commits were bisected first.
- The demo backup is about 900 MB; Tests-SMB ran in about 2 minutes warm with test data; Tests-ERM
  was 9,496 tests, about a quarter of the surface.

## Tooling timings and sizes — `impl-agent.md`, `triager.md`, `find-code`, `al-runner-tests`

- bc-decompiler on Ncl.dll (8,619 types, 43,135 methods): `search_members` 1.6s,
  `get_decompiled_source` 0.42s, `find_callers` 0.11s, `compare_symbols` about half a second.
  The BC service update that bypassed a Cecil rewrite cost 53 tests on the newer build.
- `tools/lsp-query.py`: ~8.5s for a hit, ~10s for a miss, one process per query, cold.
- `AlRunner/` on 2026-09-10: ~139,000 lines across 341 `.cs` files, `Program.cs` 6,952; the
  grep-then-read loop was 63% of one implementation agent's tool calls.
- The ncl-shadow race produced 18 bundles failing on one path; #2819's crashed run was followed by
  four runs finishing 2523/2523; a type-2 minidump of a hello-world process was ~127 MB; five of
  five `bc-engine-serial` rows skipped silently on 2026-09-06 (about 28 rows in that collection).
- Re-enabling orphaned JmpHooks: −7 Pageworks passes, −42 corpus passes (2026-08-21).
- `impl-agent.md`'s "build first" warning: three guards failed spuriously against a build 15
  hours behind `main`. (Its guard-count figures are #4248's, not this file's.)
