_Filed by Claude Code agent `stma-auto-2` for the account holder, from the #3415 measurement._

## What happens

Per-test recording (statement coverage and the #4999 event keys) attributes everything a test executes to that test only. Many test codeunits do their shared setup once, in the first test that calls `Initialize()`:

```al
local procedure Initialize()
begin
    if isInitialized then
        exit;
    CreateTestSalesInvoices();   // posts 12 sales invoices
    isInitialized := true;
end;
```

The setup's statements and events are recorded against the first test that ran it. Every later test in the codeunit reads the data it created, but its own recording does not show the setup. When a change affects the setup path, selection picks the first test and skips the others.

## Measured

Tests-SMB, BC 28.4.53241.53955, `--test-data`. An extension with one subscriber on `Sales-Post` `OnBeforePostSalesDoc` that calls `Error('PROBE-1')`:

- Recording selects 47 tests that raised the event.
- Running the whole bucket with the subscriber installed, 36 tests fail with `PROBE-1`. 14 of them are not in the selection. All 14 are in codeunit 139126 "O365 Activites Tests": its first test, `CalcOverdueSalesInvoiceAmount`, is the only one recorded as raising the event. With the subscriber installed, that setup never finishes, so every later test runs `Initialize()` again and fails the same way.
- The same 14 tests are missed for a `Sales Line` `Quantity` `OnAfterValidateEvent` subscriber.
- Expanding the selection to every test of each codeunit that has a selected test gives 0 misses for both subscribers, at 275 tests instead of 47 for the posting event.

The same mechanism applies to `affectedOnly` in `--server` for statement coverage: an edit to code that only the first-time setup reaches selects only the test that happened to run it first.

## What would fix it

Options, cheapest first:

1. Select at codeunit level: when any test of a test codeunit is selected, run the whole codeunit. Measured cost above.
2. Record what runs while a test codeunit's globals are being set up and attribute it to the codeunit, not to one test. This needs a reliable signal for "shared setup", which I have not found yet.

Data, scripts and the prototype: branch `agent/stma-auto-2/issue-3415-ms-selection-spike`, folder `spike-3415/`. Details in the #3415 comment.
