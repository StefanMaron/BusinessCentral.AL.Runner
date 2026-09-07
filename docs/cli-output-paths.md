# Output paths on the CLI (`--out`, `--output-junit`, `--coverage-out`)

Three flags name a file the runner writes when a run finishes. This describes what happens
when that file cannot be written, and why the handling is split across two places.

## The behaviour

**The parent directory is created for you.** `--out /tmp/fresh/run2/results.json` works with
neither `fresh` nor `run2` existing — the runner creates them. This is what most tools with an
`--out` flag do, and it is the first of the two options issue #2403 proposed.

**A path that cannot be prepared is refused before the run starts**, at argument-parse time,
with exit 2 and a message naming the flag and the path:

```
--out '/tmp/afile/results.json' is not a usable output path: The file '/tmp/afile' already
exists. Give --out a path whose parent directory can be created and written to.
```

**A write that fails anyway does not discard the run.** The summary has already printed; the
failure is reported, the remaining outputs are still attempted, and the process exits 2
rather than dying on an unhandled exception.

`--coverage-out` is only checked when `--coverage` asked for the file. It has a default
value, and preflighting that default would create a directory for a run that never writes
one.

## Why both a preflight and a write-time guard

They answer different questions, and neither subsumes the other.

Preflight is about **cost**. All three writers open their file only after the run finishes,
so a bad path used to be discovered at the most expensive possible moment. Measured on the
Microsoft `Tests-SINGLESERVER` bucket on 2026-09-02: 103 seconds of run, 834 classified
results, and an unhandled `System.IO.DirectoryNotFoundException` out of `WriteClassification`
that discarded all of it and exited 134. After the fix the same mistake is caught in **0.134
seconds**. A cold full-corpus or MS-bucket run is several minutes, and `--test-data`
hydration adds more, so this is the difference between a typo costing nothing and costing the
whole run.

The write-time guard is about **the results surviving**. Preflight runs minutes before the
write and cannot promise the directory is still there, still writable, or that the disk has
room. It also does not run at all for callers that reach the writers directly — the
watchdog-resume carry file, `--server`. What matters there is that a report failing to be
written is not a test failure and must not be able to take the run's own verdict down with
it.

Preflight deliberately stops at "the parent exists". Probing for writability by creating a
file would race, and would leave litter on a path the caller may never write to. The
read-only-directory case therefore reaches the write-time guard, which is where it belongs.

## Exit codes

A lost output reports **exit 2**, matching the existing `carryIncomplete` case in
`Program.cs`, which was already ranked with this reasoning: it says nothing about the AL, it
says the report is not there. So a consumer must not read it as "some tests failed", and
equally must not read a zero as "the file I asked for is on disk".

It never *raises* the exit code above what the tests earned — a run that already failed
reports its own, more specific code (3 compile, 2 execute, 1 test failure). The escalation
only applies when the run would otherwise have exited 0.

**`--output-json` reports the same code.** The document's `exitCode` field exists so a
JSON-only consumer learns the real outcome even when the process itself exits 0 under
`--no-strict-exit` — which makes it a claim about the run, and it has to agree with the
process. The document is therefore serialized *after* the output writes have run, not before
them: serializing earlier emitted `exitCode: 0` on a run whose `--out` write then failed and
whose process exited 2, telling the one consumer the field is for that the file it asked for
was on disk. Nothing else moves — stdout is redirected to stderr for the whole run in
`--output-json` mode, so the document stays the only thing ever written to stdout.

## Where the code is

- `AlRunner/Infrastructure/OutputPaths.cs` — `TryPrepare` (preflight) and `TryWrite`
  (write-time), plus the one shared message builder so the two cannot describe the same
  condition differently.
- `AlRunner/Program.cs` — the parse-time loop over the three flags, and the three call sites
  at the end of the run.
- `AlRunner.Tests/OutputPathPreparationTests.cs` — the proving tests, including the
  end-to-end assertion that an unusable path is refused *before* the run rather than after.
