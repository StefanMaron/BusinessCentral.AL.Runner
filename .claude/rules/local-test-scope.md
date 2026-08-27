# Run targeted tests locally, not the full suite

Default local scope is targeted: the tests that cover the surface you changed,
plus the new tests you wrote. Do not routinely run the full `dotnet test`
suite or the full 2000+-test AL corpus locally before every push — that is
what CI and PR builds exist for, and re-running it every iteration mostly
re-proves what CI is about to prove anyway.

Two genuine exceptions:

- **Establishing a RED baseline.** Before a fix, run the specific suite your
  change is meant to move, to confirm the failure is real and reproducible.
- **Cache-sensitive changes.** A warm second run has caught real defects here
  (e.g. AL queries broken only on a compile-cache hit) that a single cold run
  cannot surface — run twice when your change touches caching.

See the `al-runner-tests` skill for run commands and flags.
