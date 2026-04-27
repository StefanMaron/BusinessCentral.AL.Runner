---
name: al-runner-architecture
description: Pipeline architecture, stage-by-stage flow, BC→mock type rewriting, and the key-file map for AlRunner. Use when modifying AlRunner/ source (Program.cs, RoslynRewriter.cs, Pipeline.cs, Runtime/**), debugging compilation/transpilation issues, adding new mock types, understanding why a BC type is or is not rewritten, or interpreting non-zero exit codes (1/2/3).
---

# AL Runner architecture

## Pipeline

```
AL source dirs
    ↓  BC Compilation.Emit()           [Microsoft.Dynamics.Nav.CodeAnalysis]
Generated C# (per-object files, BC runtime types)
    ↓  RoslynRewriter                  [CSharpSyntaxRewriter — AST-level]
Rewritten C# (BC types → mock types)
    ↓  RoslynCompiler                  [Roslyn in-memory compilation]
.NET Assembly (in-memory)
    ↓  Executor                        [test discovery + invocation]
Test Results: N/M PASS
```

- **Stage 1 — AlTranspiler** (`Program.cs`): BC compiler's public API transpiles AL objects to C# classes.
- **Stage 2 — RoslynRewriter** (`RoslynRewriter.cs`): AST-level `CSharpSyntaxRewriter` replaces BC runtime types with mock types, strips BC attributes, injects `using AlRunner.Runtime;`.
- **Stage 3 — RoslynCompiler** (`Program.cs`): Roslyn in-memory compilation against BC Service Tier DLLs + `AlRunner.Runtime`. No disk writes.
- **Stage 4 — Executor** (`Program.cs`): discovers `[NavTest]` methods, resets in-memory tables between tests, reports pass/fail.

The runner uses **real BC types** (`NavText`, `Decimal18`, `NavOption`, etc.) from Microsoft DLLs. Only the I/O boundary is mocked — database, session, UI.

## Key files

| File | Role |
|---|---|
| `AlRunner/Program.cs` | CLI entry point, AlTranspiler, RoslynCompiler, Executor, PrintGuide |
| `AlRunner/RoslynRewriter.cs` | BC→mock type transformations (AST-level CSharpSyntaxRewriter) |
| `AlRunner/Pipeline.cs` | Pipeline orchestration, exit codes |
| `AlRunner/DiagnosticClassifier.cs` | AL diagnostic message parser |
| `AlRunner/PackageScanner.cs` | `.app` file scanning and deduplication |
| `AlRunner/StubGenerator.cs` | `--generate-stubs` command |
| `AlRunner/Runtime/AlScope.cs` | Base scope, `AlDialog` (Error/Message + ErrorInfo), `AlCompat` (Format, RoundDateTime, utility stubs), `MockDialog`, collectible errors state |
| `AlRunner/Runtime/MockRecordHandle.cs` | In-memory record store (filtering, composite PKs, sort, triggers, temp records, CalcFields) |
| `AlRunner/Runtime/MockCodeunitHandle.cs` | Cross-codeunit dispatch via reflection |
| `AlRunner/Runtime/EventSubscriberRegistry.cs` | Event subscriber discovery + dispatch |
| `AlRunner/Runtime/HandlerRegistry.cs` | Test handler dispatch (Confirm, Message, ModalPage, RequestPage, Report, SendNotification) |
| `AlRunner/Runtime/MockTestPageHandle.cs` | TestPage mock — full lifecycle, field access, navigation, Editable, ValidationErrorCount, Last, Previous, Expand, GetRecord |
| `AlRunner/Runtime/MockRecordRef.cs` | RecordRef backed by MockRecordHandle; Mark/MarkedOnly/ClearMarks (functional), Rename, FieldExists, HasFilter, GetPosition, Ascending get/set, ModifyAll, KeyCount/KeyIndex/CurrentKeyIndex, system-field number accessors |
| `AlRunner/Runtime/MockFieldRef.cs` | FieldRef get/set, range/filter, GetFilter, GetRangeMin/Max, Record(), Name/Caption/Type/Length, enum introspection, CalcSum, ALSetTable (no-op stub) |
| `AlRunner/Runtime/MockKeyRef.cs` | KeyRef mock — FieldCount, FieldIndex(n), Record, Active, ALAssign |
| `AlRunner/Runtime/TableFieldRegistry.cs` | Transpile-time AL field metadata (field name/caption/type/length, table name/caption, enum field names, PK extraction) |
| `AlRunner/Runtime/MockNotification.cs` | Notification mock — Message, Send (dispatches to SendNotificationHandler), Recall, SetData/GetData/HasData, AddAction, Id, Scope |
| `AlRunner/Runtime/MockTaskScheduler.cs` | TaskScheduler stubs — CreateTask (sync dispatch), TaskExists, CancelTask, SetTaskReady |
| `AlRunner/Runtime/MockBigText.cs` | BigText — ALAddText, ALGetSubText, ALTextPos, ALLength, ALWrite, ALRead (StringBuilder-backed) |
| `AlRunner/Runtime/MockDataTransfer.cs` | DataTransfer stubs — SetTables, AddFieldValue, AddConstantValue, CopyFields, CopyRows (no-ops) |
| `AlRunner/stubs/LibraryAssert.al` | AL stub for codeunit 130 (auto-loaded test library) |
| `AlRunner/stubs/LibraryVariableStorage.al` | AL stub for codeunit 131004 (auto-loaded test library) |
| `docs/limitations.md` | Architectural limits and behavioral differences |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | All tests pass |
| 1 | Test failures or fatal error |
| 2 | Runner limitation (blocked tests) — `--strict` treats as failure |
| 3 | AL transpilation failure |

CI always passes `--strict`. Any non-zero exit fails the pipeline.

## Hard architectural limits (cannot be fixed)

Parallel session contracts, real transaction isolation, service-tier rendering, HTTP network I/O. Everything else is a closeable gap.
