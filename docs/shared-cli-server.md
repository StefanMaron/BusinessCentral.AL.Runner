# One `--server` per test class (`SharedCliServer`)

A C# test that starts its own `--server` process pays the process start before it sends a
request. `AlRunner.Tests/SharedCliServer.cs` is an xUnit class fixture that starts one server
for a whole test class, so later facts in the class send their requests to a warm process
(#1804, #5110). The rules for when a fact may share are in that file's doc comment: same
startup flags, no fact that stops or kills the server, and a distinct app id for every bundle
(rule (c), #5079).

Two more rules come from converting classes in #5110:

- **Read stderr per request.** A shared server's `CliServer.StdErr` holds every earlier
  request's output too. Read `StdErrOfLastRequestAsync()` after the request, or
  `StdErrOfRequestAsync(n)` with `RequestsSent` read right after sending it (#5168). Both wait
  for the server's `[server] request <n> done` marker, which comes after every line the request
  writes to stderr, so a negative assertion on the slice is sound
  (docs/server-mode.md#stderr-request-marker). `StdErrMark` with `StdErrSinceAsync(mark,
  anchor)` (#5096, #5100) still works where a positive anchor is what the fact needs;
  `StdErrSince(mark)` with no wait is no stronger than the unsynchronised read the fact made
  before it shared a server.
- **A fact that only passes on a fresh server is a finding.** File it, keep that class on one
  server per fact, and name the issue in the class. `TransitiveDependencyVisibilityTests` is
  the example (#5107).

To list the classes that share a server:

```bash
rg -l 'IClassFixture<SharedCliServer>' AlRunner.Tests
```

## The canary

Sharing a server makes facts depend on each other: a fact can pass only because an earlier one
left state behind, or leave state that changes a later one's answer. Every `SharedCliServer`
runs a small canary bundle (`AlRunner.Tests/SharedServerCanary.cs`) right after it starts the
server, and again when the class has finished. Its three tests read state a request must not
inherit from an earlier one: rows in its own table, a `NumberSequence`, and a `SingleInstance`
codeunit. The first run must pass all three. The class-end run must give the same fingerprint
(summary counts plus each test's name, status and message).

When the fingerprints differ, the fixture's `DisposeAsync` throws, naming both runs. xUnit
reports that as `[Test Class Cleanup Failure (<class>)]`, not as a failed test.

**Trap: the summary line still says `Failed: 0`.** `dotnet test` prints `Passed!  - Failed: 0`
and exits **1**. Read the exit code and the `Test Class Cleanup Failure` line, not the summary.
The canary's message is in the TRX file's standard output.

The canary uses object ids 69990-69992 and app id `5a110c4a-7a2e-4c11-9e5d-c0de5110ca7a`.
Keep both out of every bundle a shared server runs.

## Reversed fact order

`AlRunner.Tests/ReversibleTestCaseOrderer.cs` is the assembly's test-case orderer. It keeps
xUnit's default order unless `AL_RUNNER_TEST_CASE_ORDER=reverse`, which runs every class's facts
in the opposite order. A class that passes both ways has no fact depending on another fact's
leftovers in either direction the two orders exercise.

```bash
dotnet build -c Release AlRunner.Tests/AlRunner.Tests.csproj
filter=$(rg -l 'IClassFixture<SharedCliServer>' AlRunner.Tests \
  | xargs -n1 basename | sed 's/\.cs$//; s/^/FullyQualifiedName~AlRunner.Tests./; s/$/./' | paste -sd'|')
AL_RUNNER_TEST_CASE_ORDER=reverse dotnet test AlRunner.Tests/AlRunner.Tests.csproj -c Release --no-build \
  --filter "$filter"
```

Any value other than unset, `default` or `reverse` makes
`ReversibleTestCaseOrdererTests.ConfiguredMode_ForThisRun_IsKnown` fail. The orderer throws on
it as well, but xUnit catches an orderer's exception, logs it as a diagnostic message and runs
the default order, so the test is what makes a typo visible.

## Suite server

`SuiteServer.RunViaServer` (`AlRunner.Tests/SuiteServer.cs`, #5111) runs one `runTests`
request on a `--server` process shared by every class in the test run, instead of starting
a runner process per fact. A one-fact class then costs a request on a warm server rather than
a process start, which is where most of a CLI fact's time went.

Use it for a fact whose assertions are only exit codes, test counts, per-test results,
failure messages and compile errors. The helper returns those from the protocol
(`ServerRunResult`): `AssertPassed` stands in for a `PASS  Codeunit<id>.<method>` line,
`AssertNoFailures` for "no FAIL anywhere", `AssertCounts` for the `<P>P/<F>F/<E>E` line, and
`CompilationErrors` carries what the CLI printed under `COMPILE FAIL`, `EMIT-EXCLUDED`
included.

Keep a fact on the CLI when it needs any of these:

- a CLI-only flag (`--strict`, `--test`, `--coverage`, `--watch`, an output file, ...) or an
  environment-variable hook;
- an assertion on what the CLI prints, rather than on a protocol field: a rendered failure line
  or diagnosis, the `Tests:` summary, a `[cache]` HIT or MISS line;
- a cold or warm run against a cache directory it owns, or several runner processes;
- the CLI's own bundle discovery or exit-code path, which is what the fact is about.

Every code path the conversions touch keeps at least one process-per-fact CLI test.

### Absence assertions and stderr

The CLI test asserted `DoesNotContain` on stdout and stderr together. The helper's equivalent
is `result.AssertOutputDoesNotContain(text)`, which looks in the protocol lines and in the
request's own stderr slice. The slice is read up to the request's `[server] request <n> done`
marker (docs/server-mode.md#stderr-request-marker) or the request throws, so "not in the slice"
means the request never wrote it. Three things follow:

- A slice that was not read never passes: `ServerRunResult.StdErr` throws, and so does the
  assertion. An empty slice that was read up to the marker is an answer, not a missing one: a
  clean request writes nothing to stderr, so the assertion also reads the protocol lines, which
  always carry at least the summary.
- `"FAIL"` and `"ERROR"` are CLI line prefixes. The protocol spells a failure `"status":"fail"`,
  so a fact that meant "no failure" calls `AssertNoFailures()`, and the absence call is an
  addition for the other text.
- A request in which a test timed out (`errorKind: "timeout"`, `ServerRunResult.TimedOut`) gets
  no stderr slice: the runner abandons that test's thread and it may write after the marker
  (#5171). `StdErr` and the two output assertions throw, and the pool discards that server
  instead of handing it to the next fact, so no later slice can carry the abandoned thread's
  lines either. The counts and test results of that request stay readable.

The pool starts at most `SuiteServer.Capacity` servers, each with `--package-cache` set to the
platform apps when they are present, and replaces a server after
`SuiteServer.RequestsPerServer` requests. Requests to one server never overlap.

After every request the pool re-runs the canary from the section above on the same server and
throws, naming the request, when the fingerprint moved. The fact that made the request fails,
and the server is discarded rather than handed to the next fact. A failure of this kind is a
runner defect: file it, or match it to an open issue, and keep that class on the CLI with a
comment naming the issue. Never change an assertion to fit the server.

To list the classes that use it:

```bash
rg -l 'SuiteServer.RunViaServer' AlRunner.Tests
```
