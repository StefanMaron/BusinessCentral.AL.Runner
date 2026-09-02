# C# test fixtures may declare `platform`, never `application`

A fixture `app.json` written by a test in `AlRunner.Tests` must **not** carry an
`"application"` property. `"platform"` is fine and stays.

There are no exceptions. If a test appears to need Base Application objects —
`Customer`, `Item`, `Company Information`, `No. Series` and the like — the answer is to
find another way to assert what it is asserting, not to add the floor back.

In Business Central, `"application"` is the Base Application dependency. It is not
declared through the `dependencies` array, which is why this is easy to add without
noticing what it pulls in: the whole Base Application closure, loaded on every runner
invocation.

## What it costs

Measured on two bundles identical except for that one line, same runner build, same
machine, both discovering and passing one test:

| | cold wall | warm wall | test-execution phase (warm) |
|---|---|---|---|
| with `"application"` | 94.9s | 9.6-13.4s | 2.7-2.9s |
| without | 25.2s | 4.3s | 0.1s |

About 70 seconds cold and 6 seconds warm, per runner invocation. 71 of the 246 files in
`AlRunner.Tests` spawn the runner as a subprocess, and the suite spawns it roughly 130
times, so this is the single largest cost in the C# suite.

## Two classes still violate this. They are debt, not permission.

`InstallBaselineDiskCacheTests` and `InstallSeedDepCompanyCacheTests` still carry the
property, because both fail outright without it: what they assert is the dependency
closure itself, and removing the floor today would leave them green while proving
nothing, which is worse than leaving them slow.

**Tracked in #2364.** They are on the list to be reworked, not a precedent to cite. Do
not add a third. When #2364 lands, this section goes away.

The bar for any claim that a test cannot follow this rule: not "this is easier to write
with Base App", but "this test's claim is about the Base Application closure, and there
is no other way to construct the state it needs." Two small source-dependency apps
produce genuinely different closures; a fixture table seeded by its own install trigger
produces seeded company data. Reach for those first.

## Sister rules

- `.claude/rules/tdd.md` — a test that passes without proving anything is the failure
  mode this rule must not create while chasing speed
- `.claude/rules/local-test-scope.md` — run targeted tests locally; CI runs the sweep
