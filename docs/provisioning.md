# What "provisioned" means, and how each half is measured

`al-runner provision` fills two independent halves of one version directory: the
**service-tier engine closure** and the **platform-app set**. They are provisioned by
separate sub-steps, they can be complete or short independently, and — measured below —
they routinely are. Each half therefore has its own completeness predicate, and a check
over one of them answers wrongly for the other.

This page holds the measurement behind that split, so the claim in the code is a citation
rather than a restatement (`.claude/rules/loud-failures.md` § "The justification is a claim
plus a citation, not the derivation behind it").

| half | predicate | lives in |
|---|---|---|
| service-tier engine closure | `EngineClosure.MissingFiles` / `ArtifactDirState.Classify` | `AlRunner/Infrastructure/EngineClosure.cs` |
| platform apps (w1) | `ProvisioningCheck.PlatformAppsComplete` | `AlRunner/Infrastructure/ProvisioningCheck.cs` |

## <a name="platform-apps-completeness"></a>Platform-apps completeness is a different question from the engine closure

Issue #2661 asked for a content-verified entry guard for `provision --platform-apps` and
`--service-tier`, in place of the glob (`does at least one *.app` / `*.dll` exist) both
modes still used. #3893 asked for the engine-closure classifier built by #3889 to get a
non-test consumer. The open question when the two were batched was whether **one**
predicate could serve both — whether `ArtifactDirState`, which reads the engine closure,
extends to the platform-app half.

It does not, and the evidence is on disk rather than inferred. Every provisioned directory
on the reporting box, 2026-09-11:

| version | engine DLLs | `Ncl.dll` | platform-apps | test-apps |
|---|---|---|---|---|
| 27.0.38460.53934 | 501 | yes | — | — |
| 27.3.44313.53909 | 501 | yes | 5 | 0 |
| **27.5.46862.48827** | **82** | **yes** | — | — |
| 27.5.46862.53931 | 501 | yes | 5 | 103 |
| 28.0.46665.54338 | 501 | yes | — | — |
| **28.0.46665.54452** | **0** | **no** | **6** | — |
| **28.1.49838.53910** | **501** | **yes** | **1** | — |
| 28.1.49838.54308 | 501 | yes | 6 | 107 |
| 28.2.50931.54319 | 501 | yes | — | 107 |
| 28.3.52162.54347 | 501 | yes | — | — |
| 28.4.53241.54346 | 501 | yes | — | — |
| 28.4.53241.54407 | 501 | yes | 6 | — |
| 28.4.53241.54447 | 501 | yes | — | 108 |

Three rows carry the argument:

- **`28.1.49838.53910`** — a **complete** 501-DLL engine closure beside a platform-apps
  directory holding exactly **one** file, `System.app`. An engine-closure predicate calls
  this directory usable, and for the engine it is; its platform-app half is short by all
  five core apps.
- **`28.0.46665.54452`** — the exact inverse: **zero** engine DLLs, a complete six-app
  payload. This is #2226 materialising (`provision`'s two sub-steps resolved different
  patch builds of one major.minor), not corruption — nothing here is broken, the halves
  simply landed in different directories.
- **`27.5.46862.48827`** — 82 DLLs **including** `Ncl.dll`, short exactly the closure
  sentinel. It passes every "is this a service-tier directory" check and fails later,
  inside an assembly load. This is the #3878 shape, and the reason the engine predicate is
  a named-file closure rather than a DLL count.

So the two halves are orthogonal, and one predicate over the engine closure would be wrong
on directories that exist today. `PlatformAppsComplete` is a sibling of
`EngineClosure.MissingFiles`, not a reuse of it.

**`System.app` is the trap worth naming.** It is the platform symbol package, carries no
`NavxManifest.xml`, and is not one of the five core apps. It satisfies the glob and fails a
manifest parse — which is precisely why `28.1.49838.53910` read as a complete provision
forever under the old guard and `provision --platform-apps` never re-attempted the
download.

### Why the set is not a DLL count, and not a marker file

501 is one version's number, not a constant, so a threshold is fragile in both directions.
Both broken shapes above are separated from all eleven healthy ones by the named closure
alone, so no new on-disk marker was needed and none is written. That matters beyond
tidiness: because the predicate reads the closure rather than a marker, it classifies
directories **already on disk**, including the two that motivated the issues. A completion
marker would only have applied to directories provisioned after it shipped.

### Reproducing the survey

```bash
R=~/.local/share/al-runner/artifacts
for d in "$R"/*/; do
  v=$(basename "$d")
  dlls=$(find "$d" -maxdepth 1 -name '*.dll' | wc -l)
  pa=$( [ -d "$d/platform-apps" ] && find "$d/platform-apps" -maxdepth 1 -name '*.app' | wc -l || echo "-" )
  ncl=$( [ -f "$d/Microsoft.Dynamics.Nav.Ncl.dll" ] && echo Y || echo n )
  printf "%-20s dlls=%-4s ncl=%s platform-apps=%s\n" "$v" "$dlls" "$ncl" "$pa"
done
```

`tools/preflight.py` reports the same classification for the engine half on every run, and
reads its closure list out of `EngineClosure.cs` rather than transcribing it.

## <a name="country-channel"></a>The country channel has no completeness answer

`PlatformAppsComplete` returns `bool?`, and a `--country` other than `w1` yields **null**:
"could not measure" (`.claude/rules/guards-need-a-third-state.md`).

The w1 download filters on a **curated** five-app list
(`ArtifactDownloader.W1PlatformAppPrefixes`), so "complete" is enumerable. A country
artifact is not "w1 plus extras" — its Base/System Application are genuinely different
files — and `IsWantedPlatformAppEntry` therefore takes **every** Microsoft-published app the
country artifact ships under `Extensions/`, deliberately, so that no per-country list has to
be hand-maintained for every country Microsoft ships. Nothing enumerates what complete means
there.

Both available guesses are wrong in a different direction, which is why the third state is
not spelled as either:

| answering | consequence |
|---|---|
| `false` | re-downloads a complete country set on every run |
| `true` | restores the glob's false green, now wearing a measured-looking answer |

So the caller keeps the glob for that channel, knowingly, and `PlatformAppsPresent`
documents that it is doing so.

## <a name="reach"></a>Why the closure check is its own file

`EngineClosure.cs` depends on `System.IO` and nothing else. That is a constraint, not a
style preference: `tools/metadata-ground-truth` is deliberately outside `AlRunner.slnx` —
its own csproj header says a project reference "would put a compile of a Microsoft app on
every `dotnet build`" — so it cannot reference the runner, and `ProvisioningCheck` (1,997
lines, reaching `AppLoader`, `SafeDirectoryScan` and `BcArtifacts`) cannot be linked as a
source file either. Before the split, the tool had no way to ask whether its artifact
directory was usable, so it opened the directory regardless and failed deep inside an
assembly load; the #3825 feasibility run contributed zero packages to every population for
exactly this reason, and it read as a code fault.

The tool now links that one file:

```xml
<Compile Include="../../AlRunner/Infrastructure/EngineClosure.cs" Link="Linked/EngineClosure.cs" />
```

**Adding a dependency to `EngineClosure.cs` breaks that link**, and the failure surfaces as
a build error in a tool outside the solution — a long way from the edit that caused it.
`AlRunner.Tests/EngineClosureSharingTests.cs` pins both the link and the
check-before-install ordering.

## Sister reading

- [`provisioning-tiers.md`](provisioning-tiers.md) — which tier a run selects, and what an
  unanswered CDN probe may buy
- [`expectations.md`](expectations.md) — declaring surfaces the runner cannot support
- `.claude/rules/guards-need-a-third-state.md` — why `PlatformAppsComplete` returns `bool?`
- `.claude/rules/loud-failures.md` — why this measurement is here and not in the code
