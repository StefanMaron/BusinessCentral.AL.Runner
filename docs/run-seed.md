# Reproducible `Random()`: the run seed

Issue #2502. Every run has a **run seed**, and every `[Test]` draws its `Random()` values from
an RNG derived from that seed and the test's own identity. A failing test's values can be
replayed with `--seed`, alone or in the full suite.

## What the runner does

| situation | behavior |
|---|---|
| no `--seed` | the run seed is `AL_RUNNER_SEED` if set, otherwise a freshly generated one |
| `--seed N` | the run seed is `N` |
| before each `[Test]` method | the session RNG is set to `new Random(Derive(runSeed, codeunitId, methodName))` |
| AL calls `Randomize(seed)` | honored exactly as BC does: `new Random(seed)` |
| AL calls `Randomize()` | reseeded from the current test's derived seed, not from entropy; stderr warns once per test |
| `CreateGuid()` | unchanged, and still nondeterministic |

The run seed is printed as `seed: N` (on stdout, or stderr under `--output-json`), written as
the top-level `seed` field of `--output-json`, and as a `<property name="seed">` on every
`<testsuite>` of `--output-junit`.

To replay a failure:

```console
al-runner <bundle> --seed 4271833 --test PostingRoundsAmountCorrectly
```

## What it does not cover

**Text from `Any` and `Library - Random`.** `Any.AlphanumericText`, `Any.GuidValue` and
`LibraryRandom.RandText` are built from `CreateGuid()`, so they differ on every run whatever the
seed. Only numeric randomness reproduces.

**`Randomize(seed)` inside a test library.** `Library - Random` and `Any` call `SetSeed(1)` on
first use, which reaches `Randomize(1)`, and the runner honors it. Values drawn after that call
follow the library's seed, not the runner's. They were already deterministic.

**`Random()` outside a test**, such as in install triggers, draws from the session RNG as BC
leaves it. A no-argument `Randomize()` there is reseeded from `Derive(runSeed, 0, "")`.

## The derivation

`Derive` is FNV-1a (32-bit) over the UTF-8 bytes of `"{runSeed}|{codeunitId}|{methodName}"`,
cast to `int`. `codeunitId` is the number in the test codeunit's compiled type name
(`Codeunit50100` → 50100). `methodName` is the name `--test` matches. `RunSeedTests` pins
golden values, because changing the function silently invalidates every seed anyone recorded.

It must never use `string.GetHashCode()`, which .NET randomizes per process: the same seed would
then give a different sequence on every run. Seeded `System.Random` is stable across .NET
versions (.NET 6 kept the legacy algorithm for `new Random(seed)`).

## How it reaches BC

- The per-test RNG is skeleton state: `RunSeed.BeginTest` sets `NavSession.Random` on the
  skeleton session from `TestExecutor.RunOne`. No BC method is patched for it.
- `ALSystemNumeric.ALRandomize()` is the one Ncl rewrite: its `new Random()` becomes a call to
  `RunSeed.CreateRandomizeRandom()` (`NclCecilRewrite.Runtime.cs`). `ALRandomize(int)` and
  `ALRandom(int)` stay BC's own bodies, which read the session RNG.
- Resolving the run seed writes `AL_RUNNER_SEED` into the process environment, so `--jobs`
  workers, watchdog resumes and re-execs inherit the same seed instead of choosing their own.
