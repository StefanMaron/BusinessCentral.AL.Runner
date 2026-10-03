# What a watchdog resume reports (#2280, #5268, #5272, #5273)

A watchdog abort ends an attempt and the run continues in a fresh process (`AbortResume`). Every
attempt compiles each bundle again and reports it, so the same drop, the same bundle and the same
app group are seen once per attempt, and the final attempt has to report **the run**. Each output
does that its own way:

| output | how it holds the earlier attempts |
|---|---|
| the printed summary | the final attempt's own table, plus a `carried from earlier attempt(s)` line from the carried totals (`Reporter.CarriedTotals`, including `skipped`) |
| `--output-junit` | the carried cases copied verbatim |
| `--output-json`, `--out`, `--count-out`, the exit code | the earlier attempts' results (`--merge-results`, `ResumeCarry`) merged with the final attempt's, one bucket per bundle (`ResumeCarry.MergeAttempts`) |

## The rule: the distinct things, never fewer than the most any attempt saw

A resumed run reports each thing once however many attempts saw it, and keeps everything only one
attempt saw. A bucket is one bundle at one stage; `MergeAttempts` merges the buckets of one bundle
and one stage across attempts, and nothing else:

| field | merged as | why |
|---|---|---|
| tests | all of them, in attempt order | a test an attempt ran is a result; the rows an attempt would repeat are not added in the first place (below) |
| suite errors (`CompileErrors`) | each line as many times as the attempt that reported it most | the same line in a second attempt is the same suite error; a line only one attempt has (an abort in the first, a failure in the last) stays; one attempt's own repeats stay |
| app groups (`RanGroupCount`, `--count-out`'s `appGroups`) | the most any attempt entered | every attempt enters every app group of the bundle, so the most is the number of groups it has; a sum counts each once per attempt |
| company-initialization aborts | one per abort, with the most app groups an attempt gave it | the same reasoning as the groups |
| provision gaps, process errors | the distinct ones | the same line is one gap |
| emit, compile and run time | summed | each attempt did that work |

**Bundles of different stages are not merged.** A bundle one attempt ran and another staged as a
failed compile stays two buckets: the merged stage would have to be one of them, and a non-`Ran`
bucket's tests are not reported by `--output-json`. Two bundles that share a directory name are two
bundles (`--count-out` keys a suite by that name and adds them), and one bundle named twice in an
attempt merges with its counterpart, not with itself.

### `appGroups` and what consumes it

Scripts compare `--count-out` and `--count-baseline`. `.github/scripts/compare_corpus_count.py` and
`scripts/check-isolation-probe-count.py` read a suite's `tests`. `--count-baseline` (exit 4) compares
`tests` and `appGroups` exactly. So a count that scripts compare is the number of **distinct** things:
the groups a bundle has, not the groups each attempt entered. A count of attempts is a different
thing and nothing reads it; the `resume:` lines name each attempt.

The most any attempt entered is exact when every attempt enters the same groups. The bundle loop
continues past an abort, whose reasons the next app group's run resets (`TestExecutor.AbortReasons`),
and a resumed attempt enters a group whose codeunits it skips (measured on a one-group bundle: one
group per attempt). If an attempt entered fewer, `appGroups` reads lower than the run's groups,
which is a baseline mismatch, the loud direction.

**One-time baseline effect.** A `--count-baseline` recorded from a resumed run holds the doubled
`appGroups` and reports a mismatch once. The test counts are unchanged.

## The rows an attempt would repeat

A dropped test codeunit (EMIT-EXCLUDED, #3476; `--tdd`'s TDD-EXCLUDED, #5272) is found again by the
resumed attempt, which would report its rows again. `ResumeCarry.NotYetReported(carried, bundlePath,
rows)` keeps the rows no carried attempt reports for that bundle (keyed on bundle, codeunit and
method), for the SKIPPED rows (#5268) and for the FAILED ones. A lost carry file reports them again:
the run is exit 2 then, and the rows are not lost.

Tests: `ResumeAttemptMergeTests` (the merge, with the controls that must not collapse),
`ResumeStructuredOutputsTests`, `ResumeThreeAttemptsTests` and `ResumeEmitExcludedTddTests` (end to end).
