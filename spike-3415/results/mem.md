_Filed by Claude Code (main session) from a measurement the owner took; I haven't reproduced it._

## Problem

The owner compared released v2.11.0 with `main` on the same private suite (Pageworks, 1,013 tests), BC 28.1, warm compile cache, same flags, two runs each:

| | v2.11.0 | main |
|---|---|---|
| Wall time | 113.6 s / 107.6 s | 80.3 s / 80.6 s |
| Test execution | 105.2 s / 99.2 s | 66.4 s / 65.8 s |
| CPU (user) | 127.9 s / 121.4 s | 95.2 s / 95.4 s |
| **Peak memory** | **3.8 GB / 3.7 GB** | **5.3 GB / 5.4 GB** |

`main` is faster, but its peak memory is about 1.5 GB higher. That matters for anyone running on a small CI machine, and it should be understood before the next release.

## What I checked

- The GC configuration is the same in both versions: `AlRunner.csproj` at `v2.11.0` and at `origin/main` both declare `ServerGarbageCollection=true`, with the same `--jobs` worker tuning (#2713). A GC-mode change doesn't explain it.
- Earlier measurements put most of the runner's peak RSS in the GC arena rather than in retained objects (#2713). A bigger heap budget, more of something cached per process, or both could explain the rise. I haven't measured which.
- `git log v2.11.0..origin/main` is a long range, so the useful next step is a bisect by peak RSS (`PhaseLog`'s `peak_rss_bytes`), not reading commits.

## Expected behavior

Either peak memory comes back near v2.11.0 without losing the speedup, or the cause is identified and the higher peak is documented as the cost of that speedup.

## Acceptance criteria

- [ ] Reproduce the gap on a suite this repository can run, such as the al-language corpus or a Microsoft bucket with `--test-data`, and record both versions' peak RSS with the corpus SHA and BC build.
- [ ] Bisect it to the change or changes responsible.
- [ ] Fix it, or document the trade-off, with a before/after measurement.

## A lead, not a finding

On a cold cache, the owner saw `main`'s total come in about 92 s above its logged phases, against about 32 s for v2.11.0. The only cold v2.11.0 data point was a run that executed no tests, so this isn't comparable yet. If the bisect runs cold as well as warm, it will show whether there's anything to this.
