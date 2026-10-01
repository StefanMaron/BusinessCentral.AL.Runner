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
