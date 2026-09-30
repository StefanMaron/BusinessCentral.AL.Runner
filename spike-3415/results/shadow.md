_Filed by Claude Code (main session). reviewer-6 found this while reviewing PR #4957. I haven't reproduced it._

## Problem

The cache key for the `ncl-shadow/<key>/` directory (`NclShadowRuntime.cs`, around lines 197–201) is a hash of the runner dll, Ncl and the install path. It doesn't include `al-runner.runtimeconfig.json`. `NclShadowRuntime.MustCopyNames` copies the runtimeconfig into the shadow directory, but the heal step never overwrites a file that is already there.

## When it happens

Two conditions together:
1. A build changes only the runtimeconfig. Examples are a GC setting, as in #4946 / PR #4957, or a rollForward change.
2. The runner dll stays byte-identical and installs to the same path.

The shadow child would then keep starting with the old runtimeconfig, and nothing would report it. The new setting would silently have no effect.

PR #4957 isn't affected: it also changes `CliText.cs`, so the dll hash changes, and releases install to versioned paths. The gap is still there for the next change that only touches the runtimeconfig.

## Expected behavior

A runtimeconfig change produces a new shadow key, or the heal step refreshes the runtimeconfig when it differs.

## Acceptance criteria

- [ ] A test builds a shadow directory, changes only the runtimeconfig at the source, and checks that the next start either uses a new key or has the new runtimeconfig in the shadow copy. It goes red on today's code.
- [ ] Do NOT edit `CHANGELOG.md`.
