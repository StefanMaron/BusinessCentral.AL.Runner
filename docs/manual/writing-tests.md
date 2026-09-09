---
title: "Writing tests"
weight: 3
---

Tests for AL Runner are ordinary Business Central test codeunits. There is no
runner-specific test framework, no attribute you have to add, and nothing to
change if you later run the same tests against a real environment. That is
deliberate: a test that only works here would not be evidence about Business
Central.

## A bundle

A bundle is a folder with an `app.json` at its root — the same shape as any
extension. The runner reads that manifest for the app's identity and its
dependencies, resolves those dependencies from its caches, and compiles the AL
underneath.

```
my-test-app/
  app.json
  src/
    MyFeatureTests.Codeunit.al
```

You can pass several bundles in one invocation. They run in sequence and report
one combined summary. Two arguments naming the same directory are collapsed onto
one run — `x`, `./x`, `x/` and a symlink to `x` are the same bundle — and the run
says which argument it dropped.

## A test codeunit

```al
codeunit 50100 "My Feature Tests"
{
    Subtype = Test;

    [Test]
    procedure DiscountAppliesToTheLineAmount()
    var
        Line: Record "Sales Line";
    begin
        // your arrangement here
        Assert.AreEqual(90, Line."Line Amount", 'A 10% discount should leave 90.');
    end;

    var
        Assert: Codeunit "Library Assert";
}
```

`Subtype = Test` marks the codeunit; `[Test]` marks each test procedure. The
Microsoft test libraries are available — `Library Assert` (codeunit 130), `Any`
(130500) and the rest of the test toolkit — because they are real Business
Central code running here, not reimplementations.

Handler attributes work as they do on a service tier:
`[ConfirmHandler]`, `[MessageHandler]`, `[ModalPageHandler]`,
`[RequestPageHandler]`, `[ReportHandler]` and `[SendNotificationHandler]` all
fire. Report and page *rendering* does not happen — the handler callback is in
scope, the layout is not.

## Test isolation

Between tests, the runner resets state the way Business Central's own
`TestIsolation` does:

```bash
al-runner --isolation codeunit ./my-test-app   # default
al-runner --isolation test     ./my-test-app
al-runner --isolation disabled ./my-test-app
```

`codeunit` shares state inside a codeunit and resets between codeunits, matching
AL's `TestIsolation = Codeunit`. `test` gives every `[Test]` a fresh state
(`TestIsolation = Function`). `disabled` never resets. The default is `codeunit`,
which is what Business Central does by default too.

## Running a subset

```bash
al-runner --test DiscountApplies ./my-test-app
```

`--test` (or `--filter`) matches against the qualified name,
`CodeunitNNNN.Method`, case-insensitively.

## Setup data

Many Business Central tests expect setup records — number series, posting
groups, company information — that a real environment has and an empty database
does not. By default the runner starts empty, so those tests fail on missing
rows.

`--test-data` hydrates the in-memory database from a Business Central backup,
reading each table the first time the run touches it rather than loading
everything up front:

```bash
al-runner --test-data ./my-test-app
```

It needs the `bcbak` backup reader on your `PATH`. When a test fails on a table
with no rows, the runner prints a one-line note naming that table and pointing
here — so you find out that the cause was missing data rather than guessing at
the assertion.

## Where the canonical examples are

The [AL language test corpus](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests)
is thousands of tests written in exactly this shape, each one validated against a
real Business Central service tier on every push. It is the best available
reference for how a test should look.
