# runner-extras-isolation-disabled

Suites here need `--isolation disabled`, so they are a **sibling of** `tests/runner-extras/`
rather than a directory inside it: the main runner-extras CI step runs one invocation under the
default isolation mode (Codeunit) and has no per-bundle override and no exclusion mechanism.

They get their own step in `.github/workflows/bc-tests.yml`, the same shape as the al-language
xmlport order-independence guard — a second `dotnet run` with different flags over a different
root.

Deliberately **not** passed `--count-baseline`. The baseline in
`tests/expectations/count-baseline/test-count-baseline.json` is keyed per suite, and adding a
third key for a handful of tests buys a number nobody reads. `--strict` is what makes this step
a gate. (The other half of that reasoning — that the file was a merge-conflict magnet every
count-changing PR had to edit the same line of — was fixed in #2485; the decision here stands on
the first half alone.)

## Why a whole directory for this

`StartSession` from inside a `[Test]` is refused by real BC unless the TestRunner declares
`TestIsolation = Disabled` (issue #2805, corpus codeunit 60397, green on all eight BC
versions). The runner implements that guard. So the dispatch path *behind* the guard — worker
construction, resolving `OnRun` against the `OnRunAsync` flavour BC's compiler emits for
precompiled codeunits, and awaiting the resulting `ValueTask` — is reachable from AL only with
the guard stood down, and that is a process-global flag.

See issue #2826.
