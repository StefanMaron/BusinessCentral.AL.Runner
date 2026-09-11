# Run targeted tests locally, not the full suite

Default local scope is targeted: the tests that cover the surface you changed,
plus the new tests you wrote. Do not routinely run the full `dotnet test`
suite or the full 2000+-test AL corpus locally before every push — that is
what CI and PR builds exist for, and re-running it every iteration mostly
re-proves what CI is about to prove anyway.

Two genuine exceptions:

- **Establishing a RED baseline.** Before a fix, run the specific suite your
  change is meant to move, to confirm the failure is real and reproducible.
- **Anything behind a cache — which is wider than "changes to caching".** A warm
  second run has caught real defects here that a single cold run cannot surface.
  **Run twice against ONE cache root**, and read both runs.

  The condition used to be "when your change touches caching", and that is the
  half that fails: three defects in one session were each correct cold and wrong
  warm, and none of their authors thought they were touching a cache.

  | PR | the change | what a warm run showed |
  |---|---|---|
  | #3882 | a loud-failure guard | fired cold, **silent warm** — the cache HIT returned 80 lines before the guard |
  | #3908 | a parse fix | the fixed code **replayed the buggy value** from cache; the key had no term for the parse |
  | #3913 | a property default | answered `false` cold, `true` warm — a third call site reached only on a HIT |

  **CI cannot catch these**: it provisions fresh every leg, so the legs stay green
  forever while every repeat local run is wrong. All three were caught in review.

  So ask *"is there a cache between my change and its observable?"*, not *"did I
  edit cache code?"* — and when the answer is yes, the proving test runs twice.

See the `al-runner-tests` skill for run commands and flags.
