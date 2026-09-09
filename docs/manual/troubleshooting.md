---
title: "Troubleshooting"
weight: 6
---

## A test fails on a table with no rows

Business Central tests usually expect setup records — number series, posting
groups, company information — that an empty database does not have.

The runner detects this: when a failure names a table and that table is
measurably empty, it prints a one-line `[test-data]` note underneath naming the
table and pointing at `--test-data`. Business Central's own message, exception
type and AL call stack are left untouched; the explanation sits beside the
failure rather than replacing it.

The fix is usually [`--test-data`](writing-tests.md). If it is already on, the
note says instead why the table is *still* empty — refused, absent from this
backup, or empty inside it.

## "The TestPage is not open."

That is Business Central's own message, and it names the symptom rather than the
cause. It means the page's row-load trigger raised an AL error, so the page was
torn down; the raised error's text never reaches AL, on a real service tier
either.

The runner prints the underlying error on a `[testpage]` note under the failure.
What AL sees is unchanged — `asserterror` and `GetLastErrorText` still read only
Business Central's message — so the note is the only place the real cause
appears.

## `RunnerOutOfScopeException`

The test reached a surface the runner does not support. The message names the
API and the reason. If the reason cites [`docs/scope.md`](../scope.md), it is
out of scope by design and the test needs a real environment. If the reason is
`not-yet-implemented`, it is a gap that is intended to be filled, and there
should be an open issue for it.

Either way the refusal is deliberate. The runner never returns a default in
place of a surface it cannot honour, because a test that passes that way is
worse than one that fails.

## The run exits 2 with no test failures

Exit 2 means a bundle could not execute at all, as opposed to running and
failing. The most common causes are a bundle path that does not exist and an
unknown flag — both are argument errors, and both print the reason.

See [the exit codes](cli-reference.md) for the rest.

## Compilation fails (exit 3)

The AL did not compile. The compiler diagnostics are Business Central's own and
appear in the output. One thing worth knowing: emit is atomic per module, so a
single object that cannot be emitted takes its whole module with it rather than
being skipped silently.

## A first run on Linux fails asking for a C compiler

The runner needs a small shim for a handful of Win32 calls that Business
Central's assemblies make. Prebuilt shims ship for `linux-x64` and
`linux-arm64`; on any other architecture it compiles one on first use and needs
`cc`, `gcc` or `clang` on your `PATH`. Install one, or build the shim yourself
and point `AL_RUNNER_WIN32_STUBS_SO` at it.

## The first run is slow on Windows

Windows Defender locking a freshly written DLL to scan it is a known source of
first-run delay. The runner retries around it, but excluding its cache and
output directories from real-time scanning avoids the wait.

## Something else

If AL code fails to run and none of the above explains it, treat it as a runner
gap rather than a problem with your AL, and
[open an issue](https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/new).
A minimal reproducer — the smallest AL that shows the behaviour, plus the exact
failing assertion or compiler diagnostic — is what makes a gap fixable.
