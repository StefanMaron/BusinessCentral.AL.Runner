---
title: "Command line reference"
weight: 4
---

This page covers the flags you are likely to reach for. `al-runner --help` is
the complete, authoritative list and is generated from the code, so prefer it
whenever the two disagree.

```
al-runner [OPTIONS] <bundle-dir>...
```

## Choosing what runs

| Flag | Effect |
|---|---|
| `--test PATTERN`, `--filter PATTERN` | Run only tests whose qualified name (`CodeunitNNNN.Method`) contains PATTERN, case-insensitively. |
| `--isolation MODE` | `codeunit` (default), `test`, or `disabled`. See [Writing tests](writing-tests.md). |
| `--test-timeout SECONDS` | Per-test timeout. Default 60. |

## Resolving dependencies

| Flag | Effect |
|---|---|
| `--package-cache PATH` | An extra `.app` package cache directory. Repeatable. |
| `--test-data`, `--test-data=PATH` | Hydrate the database from a Business Central backup. Needs the `bcbak` reader. |
| `--test-data-company NAME` | Which company inside the backup to use. Defaults to the first one. |

## Speed

| Flag | Effect |
|---|---|
| `--cache PATH` | Cache compiled AL output between runs. |
| `--watch` | Stay resident and re-run on every `.al` change. |
| `--jobs N`, `-j N` | Run the given bundles across N worker processes. Also bounds memory: peak usage tracks how many bundles a process loads, not how many tests it runs. |
| `--server` | Long-running JSON-RPC daemon over stdin and stdout, for editor integrations. |

## Output

| Flag | Effect |
|---|---|
| `--output-junit PATH` | Write a JUnit XML report, grouped by codeunit. |
| `--output-json` | Per-test JSON on stdout instead of the normal text output. |
| `--coverage` | Statement-level coverage using Business Central's own instrumentation. Writes Cobertura XML. |
| `--failures-only`, `--quiet` | Print only failures. |
| `--verbose` | Show internal diagnostic logs. |
| `--no-strict-exit` | Always exit 0, so a caller can parse the output without the step failing. |

## Debugging

| Flag | Effect |
|---|---|
| `--dap [PORT]` | Debug Adapter Protocol server, default port 4711: set AL breakpoints, pause, inspect locals. Needs exactly one bundle. |
| `--dump-csharp DIR` | Write the intermediate C# that Business Central's compiler emitted, one file per AL object. |

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Every test passed. |
| `1` | At least one test failed or errored. |
| `2` | A bundle could not execute — a process-level error, or a bad invocation such as an unknown flag or a path that does not exist. |
| `3` | A bundle could not compile. |
| `4` | A suite's test or app-group count did not match its declared baseline (`--count-baseline`), or a declared suite produced no bucket (`--count-baseline-require-all`). |
| `5` | An expectations entry matched no test in this run (`--expectations-require-match`). |
| `6` | `--test PATTERN` selected no test in the whole invocation (#4055). Under `--jobs` the workers' counts are summed; not applied in `--watch` or `--server`. |

A run can hold several of these at once, and reports the most fundamental: **`3` > `2` > `4` >
`6` > `1` > `5`**. `3`, `2`, `4` and `6` all say *the run did not measure what it claims to*, so the
report cannot be read at face value; `1` and `5` are statements about the AL it did measure.
That is why a count-baseline mismatch outranks a test failure (#3350) — a consumer who sees
only `1` fixes the failing test, sees green, and never learns that a bundle stopped being
discovered.

## Environment variables

`AL_RUNNER_VERBOSE=1` and `AL_RUNNER_SHOW_PASS=1` mirror the flags of the same
name. `AL_RUNNER_BCBAK` points at the backup reader used by `--test-data`.
`AL_RUNNER_ARTIFACTS_ROOT` moves the Business Central artifact cache off your
home directory, which is useful when it has to live on another volume or on a CI
runner's mounted path.
