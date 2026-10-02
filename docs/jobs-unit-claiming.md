# `--jobs`: sharing one bundle between workers (#5130)

`--jobs N` used to hand whole bundles to workers, so the largest bundle set the wall-clock
floor however many workers ran. A bundle that is heavy enough is now **shared**: several workers
load it and each runs only the test codeunits it claims.

## What decides it

- **Weight** is the bundle's AL file count (`ParallelFanOut.WeighBundle`), known before anything
  compiles. A bundle gets `round(weight / (total / jobs))` workers, at most `N`, and never so many
  that a piece is lighter than `AL_RUNNER_JOBS_SPLIT_MIN_FILES` files (default 100). That default
  is a judgement, not a measurement: every extra worker pays startup, bundle load and test-data
  company load again.
- **Isolation** is what makes it possible. Under `Codeunit` (the default) or `Test` isolation the
  database is rolled back between codeunits, and a test codeunit is the smallest piece that can
  run anywhere (its tests share state through the one instance). SingleInstance state is NOT
  reset at a codeunit boundary (#4781), so sharing a bundle changes which codeunits a codeunit
  follows, and with it what it inherits: a serial/split difference can be real order dependence
  as well as a runner defect. With `--isolation disabled` state carries across every test, so
  the bundle stays in order on one worker.
- **Not shared** with `--count-baseline` (each worker would compare its fraction of the bundle
  against the whole bundle's count) or `--expectations-require-match` (each worker discovers only
  the codeunits it claimed, so every other entry would read as unmatched). The plan line says so.
- **One bundle** is fanned out only when it is shared, and not when the run also passes `--out`,
  `--output-json`, `--count-out` or `--coverage`: a fan-out writes each worker's own `--out` /
  `--output-json` and refuses the other two, and a single bundle never used to be affected.

## How the claim works

The parent makes one directory under its scratch dir and gives every worker
`AL_RUNNER_UNIT_CLAIM_DIR` plus `AL_RUNNER_UNIT_CLAIM_BUNDLES` (the shared bundles). A worker runs
a shared bundle's test codeunits in order of test-method count, largest first, and before each one
creates `<hash>.claim` with `FileMode.CreateNew`; the create that succeeds owns the codeunit.
The hash covers bundle path, app group and codeunit, so two bundles that both declare
`Codeunit50000` do not shadow each other. No round trip to the parent and no worker protocol.

A claim file that cannot be created for any reason other than "it exists" throws rather than
reading as claimed (that would drop the codeunit) or as free (that would run it twice).
The claim comes after `--test`/`--filter` selection, so a deselected codeunit is never claimed and
the workers' selected counts sum to the run's.

## What it does not do yet

- Every worker of a shared bundle loads all of it: peak memory grows with `N`, and `N` is still
  yours to choose. Nothing sizes the worker count from free memory.
- The weight is a file count, so the number of workers per bundle is only as good as that proxy.
- A watchdog abort in a shared bundle ends that worker's run of it; codeunits nobody claimed yet
  run only if another worker or a resumed attempt reaches them, and the abort line says how many
  were still unclaimed.
- Per-method (`Test` isolation) units are not separate: a codeunit is still the unit, so one
  enormous codeunit stays on one worker.
- The caller's `--output-junit` is merged from the shard files; `--out` is still written by every
  worker to the one path (#5129).
