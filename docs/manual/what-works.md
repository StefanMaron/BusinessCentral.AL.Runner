---
title: "What works"
weight: 5
---

The goal is broad AL-language compatibility: any AL code that can run without
the Business Central service tier should compile and execute here. This page is
the short version. [`docs/scope.md`](../scope.md) is the authoritative per-API
list, and [`docs/limitations.md`](../limitations.md) records every known limit
with the measurement behind it.

## Supported

Records — create, read, update, delete, filters, keys, `CalcFields`, `CalcSums`,
and every trigger. Codeunits, including interface dispatch, event subscribers and
the Business Central lifecycle events. The Microsoft test toolkit, `LibraryAssert`
and `Any` among them. Test handlers: confirm, message, modal page, request page,
report and notification. `TestPage`. `RecordRef` and `FieldRef`. BLOBs and
streams, JSON and XML, regular expressions, in-process cryptography and
`IsolatedStorage`.

Because these are Business Central's own assemblies, your test exercises the real
Base Application and System Application code paths. That is the point of the
design: a test that passes here passed against Microsoft's logic, not against an
approximation of it.

## Out of scope, by design

Anything that needs a process or service outside the runner:

- sending mail over SMTP
- HTTP calls to external services, and OAuth flows
- file input and output against external filesystems or blob storage
- publishing OData or SOAP endpoints
- physical printers
- background job scheduling against a real scheduler
- page and report **rendering** — handler callbacks fire, layout is not evaluated

These do not quietly return a default. Touching one raises
`RunnerOutOfScopeException`, naming the API and the reason, so a test can never
pass by silently skipping the part that mattered. AL code that asks permission
first gets an honest answer: `TaskScheduler.CanCreateTask` returns false here,
which is true.

## Not yet implemented

Some things are in scope and simply are not built yet. Those also refuse loudly
rather than returning a default, with the reason `not-yet-implemented`, and each
one has an open issue. [`docs/limitations.md`](../limitations.md) lists them with
what is known about each.

If you hit a failure that is not explained by any of the above, that is a gap
worth reporting — see [Troubleshooting](troubleshooting.md).

## When to use a real environment instead

Use the full Business Central pipeline when what you are testing *is* the part
that is out of scope: an integration against a live web service, a report's
rendered layout, a permissions question that depends on real authentication, or
anything about the service tier's own behaviour under load. The runner is for
the logic underneath.
