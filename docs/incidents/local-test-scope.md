# Incidents behind .claude/rules/local-test-scope.md

## Three cache defects, correct cold and wrong warm (moved out of the rule, #4542)

The condition used to be "when your change touches caching", and that is the half that fails:
three defects in one session were each correct cold and wrong warm, and none of their authors
thought they were touching a cache. All three were caught in review; CI provisions fresh every
leg, so its legs stay green while every repeat local run is wrong.

| PR | the change | what a warm run showed |
|---|---|---|
| #3882 | a loud-failure guard | fired cold, **silent warm** — the cache HIT returned before reaching the guard |
| #3908 | a parse fix | the fixed code **replayed the buggy value** from cache; the key had no term for the parse |
| #3913 | a property default | answered `false` cold, `true` warm — a third call site reached only on a HIT |
