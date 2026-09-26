# Assembly resolution: the runner's `Resolving` handlers

The runner installs Default-ALC `Resolving` handlers that serve an assembly **by simple name**
when the default binder cannot find it:

| handler | serves from |
|---|---|
| `DependencyLoader.EnsureResolverInstalled` | an already-loaded dependency module (`_byName`), else the selected BC artifact directory (`BcArtifacts.ServiceTierDir`) |
| `ServiceTierDllIndex.EnsureCrossRefResolverInstalled` | the extracted service-tier DLL cache |

All three paths go through `ResolvedAssemblyVersionGuard` (#4569).

## Version check

**Rule: serve a candidate when its assembly version is at least the requested version. Refuse
with a `FileLoadException` naming both versions and the path when it is older. A request with
no version is served.** This is the rule the default binder applies to the TPA list.

**Exception: `Microsoft.Dynamics.*` is never refused on its version.** MS stamps those assemblies per
BC build (`Microsoft.Dynamics.Nav.CodeAnalysis` is 17.0.39.53543 in 28.0.46665.53258 and
28.1.49838.53249, 17.0.40.3339 in 28.1.49838.53910 through 28.4.53241.53989, 16.4.40.3345 in
27.x), and one engine binary serves the selected build's copy across a major's minors
(`AlRunner.csproj`, #1700). The runner's own `al-runner.dll` requests the CodeAnalysis version
it was built against, so the plain rule refused an older build of the same minor and the runner
exited 134 in `BcCompiler.SetTddMode` (reviewer measurement on PR #4585: 28.1.49838.53249 and
28.0.46665.53258, both 4/4 before the guard). With the major rule both pass 4/4 on
`tests/runner-extras/environment-type-default`.

An equal-major rule was not enough either: it refused a different major, and that bind works.
The precompiled dependency apps under `tests/runner-extras/` (`precompiled-tableext-keys`,
`query-dataitem-filter-precompiled-dep`, `testpage-precompiled-*`, ...) reference
`Microsoft.Dynamics.Nav.Ncl, Version=28.0.0.0` and are served 27.0.0.0 on the 27.x legs. On
PR #4585's run 36124578301 the equal-major rule refused that load on BC 27.0.38460.55036 and
27.5.46862.55139 and failed 25 tests in nine codeunits (61101, 65201, 65281, 65701, 65841,
65871, 65921, 65922, 65941) that pass on `main`. The runner serves the selected build's platform
to every caller whatever build it was compiled against, so the whole `Microsoft.Dynamics.*`
prefix is exempt from the check.

### Reading the version must not re-enter the handler (#4725)

The candidate's version is read with `AssemblyName.GetAssemblyName`, which loads
`System.Reflection.Metadata` the first time it runs. If that load cannot bind from the TPA list it
reaches this same handler, which reads another version, and so on until `Stack overflow.` (exit 134).
Measured on a `--no-cache` run whose platform-apps attempt child deleted the shared throwaway root,
and with it the parent's `ncl-shadow` directory, so the TPA entry for `System.Reflection.Metadata`
pointed at a deleted file. Two rules keep it bounded:

- an **unversioned** request is loaded without reading the file, since there is nothing to compare
  (`GetAssemblyName`'s own request for `System.Reflection.Metadata` carries no version);
- a **versioned** request arriving while a version read is in progress on the same thread is
  refused with a `FileLoadException` naming both requests, never recursed into.

The deletion itself is fixed in `CacheRoots`: a process that adopted a `--no-cache` root deletes it
only when the root's `.owner` sidecar names no live process other than itself; otherwise the
generation that minted it does.

### Why the handler has to check: the runtime does not

Measured on .NET 8 (runner host), with a strong-named `VLib` built at 1.0.0.0 and 2.0.0.0 and an
app compiled against 2.0.0.0 whose `Resolving` handler returns the 1.0.0.0 file:

```
Resolving VLib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=4d57a70e8aa7ac0f
bound VLib 1.0.0.0
```

The runtime binds it. A shortfall is therefore silent at load time and surfaces later, if at
all, as a `MissingMethodException` or `TypeLoadException` away from the load.

### Why only an OLDER file is refused

A request for a lower version than the file is normal and must keep working. Static count over
every `AssemblyReference` in each full artifact directory provisioned on the measuring box (the 26.0.30643.48845 and 28.0.46665.48948 directories carry only the engine DLLs and have no edge either way) whose target file is in the same
directory and not in the runner's TPA/bin (the requests that can reach the handler):

| builds | equal | requested lower than file | requested higher than file |
|---|---|---|---|
| 26.0.30643.50520 | 1982 | 199 | 224 |
| 27.0.38460.53260, 27.0.38460.53338, 27.0.38460.53934, 27.3.44313.53267, 27.5.46862.53242, 27.5.46862.53716, 27.5.46862.53775, 27.5.46862.53931 | 2009 | 206 | 210 |
| 27.5.46862.48827 | 2569 | 200 | 210 |
| 28.0.46665.53240, 28.0.46665.53258, 28.1.49838.53220, 28.1.49838.53249, 28.2.50931.52786, 28.2.50931.53496, 28.2.50931.53737 | 1956 | 201 | 211 |
| 28.1.49838.50794 | 1961 | 199 | 211 |
| 28.1.49838.53910, 28.1.49838.53953, 28.1.49838.53997, 28.3.52162.53954, 28.4.53241.53955, 28.4.53241.53989 | 1694 | 271 | 205 |
| 28.1.49838.54044, 28.1.49838.54169 | 1695 | 272 | 205 |
| 28.1.49838.54424, 28.4.53241.54407, 28.5.54151.55132 | 1697 | 273 | 205 |

Every "higher" edge is in the service tier's web-hosting stack: `Microsoft.AspNetCore.*` (files
2.1.3.0 / 2.3.0.0 / 2.3.6.0, requested 3.1-8.0), `Microsoft.Net.Http.Headers` (2.3.0.0),
`System.Management.Automation` (file 3.0.0.0, requested 7.4.0.0),
`System.Net.WebSockets.WebSocketProtocol` (one build) and three `Microsoft.Extensions.*` files at
8.0.0.0 requested at 8.0.0.1 / 8.0.0.2. Refusing the lower-request direction would break a few
hundred edges per build; refusing the higher direction touches only these.

### Does a runner process ever hit a shortfall?

Logging every request the handlers served (build `28.1.49838.53910`, the engine's own build,
corpus `4946afb4`):

| run | handler serves | equal | unversioned | requested lower than file | requested higher than file |
|---|---|---|---|---|---|
| al-language corpus, 3618 tests | 53 | 51 | 1 | 1 | **0** |
| `tests/runner-extras`, 568 tests | 54 | 52 | 1 | 1 | **0** |

The unversioned request is `Microsoft.BusinessCentral.SystemApp`; the lower one is
`Microsoft.Extensions.Logging` requested at 8.0.0.0 and served 10.0.0.0. All from the
service-tier file probe; neither run reached the `_byName` or cross-reference paths.

Those runs used the engine's own build, so request and file were equal; the runner's own
`Microsoft.Dynamics.*` references against an older build are exempt (above). What the check
still refuses is a non-`Microsoft.Dynamics.*` assembly from the artifact directory's closure
(Azure SDK, `Microsoft.Identity.*`, `Microsoft.Extensions.*`, ...) older than a caller requested.

### The message is on the inner exception

The runtime wraps an exception thrown by a `Resolving` handler in its own generic
`FileLoadException` (`Could not find or load a specific file. (0x80131621)`), so the diagnosis
is the **inner** exception. Because a caller printing only `.Message` would lose it, the guard
also writes it to stderr once per requested name, prefixed `[assembly-resolver]`.
