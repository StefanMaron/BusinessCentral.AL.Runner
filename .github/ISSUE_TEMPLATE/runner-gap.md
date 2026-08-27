---
name: Runner gap / missing mock
about: A BC AL feature, type, or method that al-runner doesn't support yet
title: ''
labels: ''
assignees: ''
---

## Problem

<!-- What AL code triggers the issue? Paste the error message or describe the failure. -->

```
<!-- Compilation error or runtime exception here -->
```

**Triggered by:** <!-- e.g. "parallel-worker-bc", "my project", inline AL -->

## Reproduction

<!-- Minimal AL code that causes the error. Include both source and test codeunit. -->

```al
// Source codeunit
codeunit 50100 MyCodeunit
{
    procedure DoSomething()
    begin
        // AL that triggers the gap
    end;
}
```

```al
// Test codeunit
codeunit 50101 MyCodeunitTest
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    [Test]
    procedure TestDoSomething()
    begin
        // test that fails or errors
    end;
}
```

## Root cause

<!-- Which BC runtime type or method is missing / crashing? -->
<!-- e.g. "NavRecordRef.ALName has no Cecil rewrite" or "NavSession is null when ALIsInWriteTransaction() is called" -->
<!-- Tip: AL_RUNNER_HOOK_AUDIT=1 names patch call sites that are silent no-ops. -->

## Expected behavior

<!-- What does REAL BC do here? That is the target — the runner matches BC or throws loudly. -->
<!-- A silent stub / no-op / default return is never the answer: see .claude/rules/loud-failures.md. -->
<!-- If the surface is permanently out of scope, the expected behavior is
     RunnerOutOfScopeException with a named API + a reason from docs/scope.md. -->

## Likely fix

<!-- Where is the fix? Pick one: -->
- [ ] Cecil rewrite in `AlRunner/Infrastructure/NclCecilRewrite.cs` routing to a new/existing patch body
- [ ] New or extended patch under `AlRunner/Patches/`
- [ ] Compile-pipeline fix in `AlRunner/BcCompiler.cs` / `AlRunner/BcAssembler.cs`
- [ ] Skeleton state the BC method reads (`NavSession`, `NavMethodScope`, …) is missing/unpopulated
- [ ] Out of scope by design → `tests/expectations/oos-<area>.json` entry + a loud throw
- [ ] Other: <!-- describe -->

## Acceptance criteria

- [ ] Proving test with positive + negative (`asserterror` + `Assert.ExpectedError`) cases.
      A test of plain BC behavior goes UPSTREAM in `StefanMaron/BusinessCentral.AL.Language.Tests`;
      only runner-specific claims go in `tests/runner-extras/`.
      See `.claude/rules/bc-behavior-tests-go-upstream.md`.
- [ ] RED confirmed before fix, GREEN after
- [ ] CI matrix green on the PR's own head commit
- [ ] Do NOT edit `CHANGELOG.md` — it is generated post-merge (`.claude/rules/no-changelog-edits.md`)
