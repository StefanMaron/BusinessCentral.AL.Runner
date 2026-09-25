# `--test-data`: how the runner talks to the backup reader

`--test-data` reads rows out of a BC `.bak` through `bcbak` (the `bcdb` binary, installed as
`bcbak`; see the `running-ms-test-buckets` skill for where it comes from). The whole transport
lives in two files: `AlRunner/Infrastructure/BackupReaderTool.cs` (locating the binary, the
one-process-per-command path) and `AlRunner/Infrastructure/BackupReaderServe.cs` (the serve
session). Everything else calls `BackupReaderTool.Run(args)` and gets back the text the CLI
would have printed.

## One serve process per run

`bcdb serve <backup> [--symbols <apps>]` opens the backup and parses the symbol closure once,
then answers one JSON request per stdin line with one JSON line. The runner starts it lazily on
the first `read`, `tables` or `companies`, keyed on (backup, symbol set), and stops it with
`quit` (then a kill after 3 s) from `ProcessExit`. A runner killed outright closes the child's
stdin, and the reader exits on EOF.

`companies` does not depend on the schema, so any live session on the same backup answers it.
`tables` and `read` do, so a session started with a different symbol set is replaced.
`TestDataProvisioner.Arm` asks for `tables` before `companies` for that reason: the other order
starts a symbol-less session for `companies` and then a second one for `tables`
(pinned by `TestDataArmServeSessionTests`).

`describe` is not routed: it has no call site.

### Server mode (`--server`, `--watch`)

The session lives as long as the runner process, across cycles, not one per cycle. The armed
plan (`TestDataProvisioner._armed`) is process-lifetime too, and was built from the catalog this
same session answered; restarting the reader per cycle would pay the symbol parse every cycle
and could pair a plan read from one open file with rows read from another.

## Failure handling

| what happened | outcome |
|---|---|
| the reader answered `"ok": false` | `BackupReaderException` with the reader's own text; the session stays up |
| the session exited or broke its pipe before answering anything (a reader without `serve`) | one `[warn]` naming the reason and the reader's stderr, then one process per command for the rest of the run |
| the session exited after answering | `BackupReaderException` naming the exit code and the reader's stderr; no fallback |
| no answer within the command timeout (600 s) | the child is killed; `BackupReaderException`; no fallback |
| an answer that is not JSON, or carries another request's `id` | the session is stopped; `BackupReaderException`; no fallback |
| an answer missing what the command must carry (`headers`, `tables`, `companies`, a table's `name`/`rows`) | `BackupReaderException` — never an empty result |

`AL_RUNNER_BCBAK_SERVE=0` turns serve mode off.

## Measurements

BC 28.1.49838.53910 W1 backup, `bcdb 0.1.2+68df0f96`, this machine, warm page cache.

Per invocation, with 5 platform apps as `--symbols` (Base Application, System Application,
Business Foundation, Application, System):

| | seconds |
|---|---|
| `bcdb tables` (one process) | 1.20 |
| `bcdb companies` (one process) | 0.06 |
| `bcdb serve`, one `read` | 0.77–0.81 |
| `bcdb serve`, `tables` + `companies` + the same `read` | 1.21 |

So an arm that used to cost `tables` + `companies` + a serve session (≈ 2.0 s) costs one
session (≈ 1.2 s). The symbol parse dominates `tables`, so the saving grows with the closure:
an earlier measurement with the 108-app closure a normal run resolves put it at 1.85 s per
parse (quoted in #2263's comments; not re-measured here).

Serve against one process per command on six commands (`tables`, `companies`, and merged reads
of Payment Terms, Currency, Customer and G/L Account; 3 symbol apps): 1.2 s against 4.4 s, rows
identical (`BackupReaderServeRealBackupTests`).

The `tables` text rebuilt from the serve answer is byte-identical to the CLI's on all 3,955
lines of that backup (3 symbol apps).

The first differential found one real difference: re-serialising a `read` cell escaped `€` to
`\u20AC` (Currency). The value decodes the same, but the cell is now copied as the reader's own
bytes.
