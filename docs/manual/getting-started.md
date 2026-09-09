---
title: "Getting started"
weight: 2
---

## Prerequisites

The .NET SDK, version 9 or 10 — [download it here](https://aka.ms/dotnet/download).
That is the whole list on Windows and macOS.

On Linux there is one extra detail, and it usually needs nothing from you. A few
Business Central assemblies call Win32 functions directly — the locale APIs, for
example, which anything evaluating a `TextConstant` reaches. The runner
redirects those to a small shim, and a prebuilt shim ships for `linux-x64` and
`linux-arm64`. If you are on a different architecture the runner compiles the
shim on first use, which needs a C compiler (`cc`, `gcc` or `clang`) on your
`PATH`. If one is missing the run fails and says so by name.

## Install

```bash
dotnet tool install --global MSDyn365BC.AL.Runner
```

On the first run the runner downloads the AL compiler and the Business Central
assemblies it needs — around 11 MB, fetched with HTTP range requests — and
caches them. Later runs use the cache.

## Run your first test

The runner takes one or more **bundle directories**. A bundle is a folder with
an `app.json` at its root: the same shape as any Business Central extension. If
you point at a folder below one, the runner climbs the path to find the
`app.json` itself.

```bash
al-runner ./my-test-app
```

That is the whole invocation. Dependencies declared in `app.json` are resolved
against the runner's artifact cache, the dotnet tool store, and any extra
package caches you name:

```bash
al-runner --package-cache ~/.al-runner/platform-apps ./my-test-app
```

A run prints one line per test and a summary. It exits 0 when everything
passed — see [the exit codes](cli-reference.md) for what the other values mean.

## Make repeat runs fast

Two caches matter, and both are worth turning on for day-to-day work.

```bash
al-runner --cache ~/.cache/al-runner/al-out ./my-test-app
```

`--cache` keeps the compiled output of your AL, keyed on the source, the
dependency set and the runner build, so an unchanged bundle skips compilation
entirely.

The second cache needs no flag. The runner stores the result of your
dependencies' install triggers and `Company-Initialize` and reloads it instead
of re-running those AL bodies in each new process. On a warm single-fixture run
that is the difference between 6.3 seconds and 0.8.

For a tight edit-and-test loop, stay resident instead:

```bash
al-runner --watch ./my-test-app
```

Watch mode keeps dependencies warm and re-runs on every `.al` change, in
process, in about a second. It waits for changes to settle before starting, so a
branch switch or a formatter run does not fire a cycle halfway through.

## Next

[Writing tests](writing-tests.md) covers how a test bundle is put together.
