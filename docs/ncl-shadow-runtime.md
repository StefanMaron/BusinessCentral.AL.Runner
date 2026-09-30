# The Ncl shadow runtime directory

The runner does not ship `Microsoft.Dynamics.Nav.Ncl.dll`. It mirrors its own install into a
cached "shadow" directory under `<cache>/ncl-shadow/<key>`, puts a Cecil-rewritten Ncl.dll
there, and re-execs into it, because CoreCLR fixes the trusted-platform-assemblies list from
the on-disk contents of the base directory before any managed code runs.
`AlRunner/Infrastructure/NclShadowRuntime.cs` has the class-level account of that design; this
page covers what makes a published directory trustworthy, and what stopped it being so.

## Completeness and in-use locking

Two files are bookkeeping rather than mirror content:

| file | written | meaning |
|---|---|---|
| `.al-runner-shadow-manifest` | last but one, in the `.building.*` temp dir | every entry a completed mirror produced, relative, `/`-separated, directories that are symlinks recorded with a trailing `/` |
| `.al-runner-shadow-inuse.<pid>` | when a process adopts or publishes the dir | an open handle (`FileShare.Read`) the process holds until it exits |

`IsShadowDirComplete` accepts a directory only when the marker matches the install, the launch
set is present (`al-runner.dll`, `Ncl.dll`, `al-runner.deps.json`,
`al-runner.runtimeconfig.json`), **and** nothing the manifest lists is absent.
`PruneStaleShadowDirs` deletes a stale directory only when no in-use lock in it is held and it
is older than `MinPruneAge` (10 minutes).

### What went wrong (#3559)

A run that had executed for ~20 minutes died in `BcAssembler.SharedMetadataReferences` →
`MetadataReference.CreateFromFile` with `FileNotFoundException` on
`…/ncl-shadow/7a4d…/runtimes/win/lib/net8.0/System.Diagnostics.EventLog.Messages.dll`. The
directory existed and held **22 files** where a sibling published from the same install held
**452**. Five runner processes from four builds were starting against one `~/.cache/al-runner`
in the same half hour, and symlinks are refused on that box, so every mirror was a real copy.

The 22 survivors decide the diagnosis. Every one is an assembly a running .NET process holds
mapped:

```
al-runner.dll  Microsoft.Dynamics.Nav.Ncl.dll  AlRunner.QueryJoin.dll  AlRunner.Provisioning.dll
Microsoft.CodeAnalysis[.CSharp].dll  Mono.Cecil.dll  Newtonsoft.Json.dll  Spectre.Console.dll
System.Text.Json.dll  System.Reflection.Metadata.dll  System.Collections.Immutable.dll  …
runtimes/win/lib/net8.0/System.Diagnostics.EventLog.dll
```

Nothing unmapped survived — not the marker, not `al-runner.deps.json`, not the 430 other DLLs,
and not `EventLog.Messages.dll`, which is a resource-only assembly nothing loads. So the
directory was not published incomplete: **it was built complete by the process that then
crashed, and emptied underneath it by a sibling's `PruneStaleShadowDirs`.** The prune keeps the
newest four and five keys existed, so the victim's own published directory was ordinary prune
fodder — its `protectedDir` argument only ever protects the pruning process's own directory.

Measured on Windows 11, .NET 8 (`tmp/probe3559/probe.ps1`): with one file of six held open
`FileShare.Read`, `Directory.Delete(recursive: true)` throws
`The process cannot access the file … because it is being used by another process`, deletes
every other file, and leaves the held one. That is exactly the 22-of-452 shape, and it is why
the victim keeps running — its already-open handles stay valid — until the first time it opens
a file in that directory *by path*, which for this run was Roslyn resolving a metadata
reference twenty minutes in.

The same probe confirms the lock primitive: a holder with `FileShare.Read` refuses a prober's
`FileShare.None` open, and the probe succeeds again the moment the holder disposes. On Linux
the delete would succeed instead (unlink semantics), which is why the prune **probes before
deleting** rather than relying on the delete failing.

### Design notes

- **The manifest lists names, not sizes.** The failure is a *missing* file, and a size read on a
  symlink means different things on different platforms.
- **Presence is not `File.Exists`.** A name list also catches the #2166 shape — a symlink whose
  target the original install lost — but only if the check resolves the link. Measured on Linux
  (.NET 8, WSL) against a dangling symlink:

  | | intact | dangling |
  |---|---|---|
  | `File.Exists` / `FileInfo.Exists` | true | **true** |
  | listed by `Directory.EnumerateFiles` | yes | yes |
  | `ResolveLinkTarget(returnFinalTarget: true)?.Exists` | true | **false** |

  On Windows `File.Exists` answers false for the same shape, so an existence check alone passes
  there and lets the gap through on exactly the legs that matter. `EntryResolves` asks the third
  row. This was caught by CI: PR #3596's first head was red on both unit legs with
  `Assert.False() Failure, Actual: True`, on a Windows box where the test had skipped itself
  because symlink creation is refused.
- **Verification cost** is one `File.Exists` per entry, a few hundred stats, at startup and in
  the publish loop.
- **The heal copies every manifest entry that is missing**, never overwriting one that exists.
  The four-file heal that predates this could not fill a 430-file hole. Overwriting is what is
  unsafe (a sibling may have the file mapped) and renaming the directory aside is what #2489
  forbids (a live process's `AppContext.BaseDirectory` is that exact path); creating a missing
  file is safe under both.
- **A legacy directory without a manifest is refused and rebuilt, once.** It costs nothing in
  practice: the key already includes `RunnerFingerprint.ContentHash`, so a new runner build
  never lands on an old build's directory.
- **The 10-minute age floor is the second guard, not the first.** The lock is what decides. The
  floor covers the window between a publish and its lock, and processes from builds that
  predate the lock — those hold no lock, and nothing in a new build can make them.
- **The lock is held by the parent, not the shadow child.** `TryShadowReexec` starts the child
  and calls `WaitForExit`, so the parent outlives it; holding the lock in `EnsureShadowDir`
  covers the child's whole run without touching the child's startup path.

### Residual gaps

- A process started from a build that predates this change holds no lock, so only the age floor
  protects it. That resolves as installs update; it cannot be fixed from the new build.
- A directory whose lock is held **and** whose file set is incomplete cannot be renamed aside,
  so it is healed in place; if the heal cannot complete, `PublishShadowDir` falls back to
  running from the `.building.*` temp dir, as it did before.

## Publishing Ncl.dll (#5018, #5019)

`NclCecilRewrite.RewriteInPlace` runs at every start and publishes the Cecil-rewritten Ncl.dll
into the directory the process is about to load it from — the shadow dir for a shadow child.
It goes through `NclFilePublisher` (`AlRunner/Infrastructure/NclFilePublisher.cs`):

1. **An identical destination is not written.** `PublishIfChanged` compares the destination's
   bytes with the cached rewrite and returns without touching the file when they match. Every
   process sharing one shadow dir computes the same bytes (the key pins the runner build and
   the source Ncl.dll), so after the first start this is the only path taken.
2. **Equality has to be established, not assumed.** A symlink, a missing file, or a read that
   keeps failing answers "not current", and the file is written as before. A symlink is never
   current because its content can change without our path being written.
3. **When a write is needed, the one-step rename goes first.** `File.Move(overwrite: true)` —
   rename(2), or MoveFileEx with `MOVEFILE_REPLACE_EXISTING` — never makes the name disappear.
   `File.Replace` (Win32 ReplaceFile) comes second, only when the rename is refused, because it
   gets past a real-time scanner holding the fresh temp file (#1650). ReplaceFile renames the old
   file aside and then moves the new one in, so between the two steps the name does not exist.
4. **Leftover `Microsoft.Dynamics.Nav.Ncl.dll~RF<hex>.TMP` files are reaped** from that directory
   at every start. A backup another live process still has loaded is the file ReplaceFile itself
   could not delete, so our delete is refused too and it is left for a later start.

### What each Windows report was

- **#5019** — ReplaceFile on every cache hit, while another al-runner process had the old
  Ncl.dll loaded, left one ~11 MB `~RF*.TMP` per start. Step 1 removes the replace; step 4
  removes the files earlier versions left.
- **#5018** — a sibling loading Ncl.dll inside ReplaceFile's window got `FileNotFoundException`.
  Step 1 removes the window from the steady state; step 3 keeps it out of the write path unless
  a rename is refused.

### Why the skip is back after #2489 withdrew it

PR #2512 first shipped this skip, then withdrew it: with it, subprocess tests
(`BatchAppIdentityTests`, `TestFilterFlagTests`, `TestIsolationMethodAliasTests`) failed
roughly 1 run in 5 to 1 in 8 under the parallel C# suite, and 5 runs with it disabled were
clean. The cause was never identified. Five clean runs do not separate the two arms at that
rate (about a one-in-three chance under no difference), and the same PR was fixing two
self-heal defects that corrupt or rename a live shadow dir at the same time.

Re-measured on this change (Linux, `-p:_BCVersion=28.1.49838.53910`, default xUnit
parallelism, those three classes together, default cache root):

| arm | runs | failed runs | shadow `Ncl.dll` inode across the arm |
|---|---|---|---|
| warm (shadow dir and `ncl-cecil` entry present) | 14 | 0 | unchanged — the skip ran every time |
| cold (both removed before each run) | 7 | 0 | new per run — the directory was rebuilt each time |

At a 1-in-8 rate, 0 failures in 21 runs has about a 6% chance; at 1 in 5, under 1%.

What differs from the #2512 version: it followed symlinks (`File.Exists`), and read the
destination with `FileShare.Read` behind a 60-attempt retry. This one refuses a symlink and
opens the destination with `FileShare.ReadWrite | FileShare.Delete`, so it never blocks a
sibling's rename. None of that is shown to be what went wrong in #2512.

### Not verified on Windows

This suite runs on Linux. The Windows ordering and the reap are driven through `NclFileOps`
in `NclFilePublisherTests`, which stands in for Windows rather than running on it. Not measured
on Windows:

- that a DLL another process has loaded refuses deletion — the reap's safety rests on it; the
  reporter's `~RF*.TMP` files are ReplaceFile's own delete being refused in exactly that case;
- that trying the rename before ReplaceFile costs the #1650 antivirus case nothing but one
  refused MoveFileEx — ReplaceFile still runs on the same attempt, before any backoff.
