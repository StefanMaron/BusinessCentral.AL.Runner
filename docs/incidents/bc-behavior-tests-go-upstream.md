# Incidents behind .claude/rules/bc-behavior-tests-go-upstream.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## The test

Mixed suite? Split it — BC assertions upstream, only the runner-specific ones stay. The repo
already does this: the LEAVE-BEHIND note at the top of
`tests/al-language/.../TestReportRunExecution.al` migrated the execution tests upstream and
deliberately kept exactly one runner-specific OOS-classification test behind in
`tests/runner-extras/report-run-execution`.

## Declare the linkage in the PR body — the gate accepts exactly one shape

The six shapes that went red on real PRs in one day (#3330), each pinned in `test_check_corpus_linkage.sh`:

| written | why it fails |
|---|---|
| `` The `Corpus-PR:` for this is #226 `` | mid-sentence, backticks, and no URL |
| `Corpus-PR: [#228](https://…/pull/228)` | a markdown link is not a bare URL |
| `Corpus-PR:` on one line, the URL on the next | the marker and the URL must share a line — a brief saying "a bare full URL on its own line" produces this |
| `**Corpus-PR:** https://…/pull/226` | bold markers break the marker |
| `Corpus-PR: <https://…/pull/226>` | angle-bracket autolinks break the URL |
| `Corpus-PR: StefanMaron/BusinessCentral.AL.Language.Tests#293` | GitHub's own cross-repo shorthand renders as a link and reads correctly, but it is not a URL |
