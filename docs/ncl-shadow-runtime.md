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
