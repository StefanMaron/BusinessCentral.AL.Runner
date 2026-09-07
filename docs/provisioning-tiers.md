# Provisioning-tier selection, and what an unanswered CDN probe may buy

`BcArtifacts.ResolveProvisionTargetCore` decides which BC artifact auto-provisioning will
fetch when the user gave no `--bc-version`. It walks three tiers, and at each one asks the
cache first and the CDN second:

| tier | meaning |
|---|---|
| `cached-exact` / `cached-minor` | already on disk; nothing to fetch |
| `cdn-exact` / `cdn-minor` | not cached, but the CDN says it has it |
| `cdn-exact-undetermined` / `cdn-minor-undetermined` | the CDN could not be asked; the tier is **held**, not demoted |
| `major-fallback` | both probes answered "no" — the genuinely degraded outcome |
| `major-fallback-offline` | no network step is coming at all (`--no-auto-provision`) |

Only `major-fallback` is KNOWN-DEGRADED. #2020 measured it at dozens of extra test failures
from engine/artifact minor skew (28.1 selected: 1041 pass / 35 fail; 28.2 selected: 996 pass /
77 fail / 3 error, on the same binary and dep set).

## <a name="undetermined"></a>Why an unanswered probe holds the tier instead of throwing

Issue #2981. `ArtifactDownloader.VersionExists` answered a three-state question with a bool —
the CDN said 404, the CDN said 200, and *the probe never got an answer* — and the third
collapsed into `false`. `ResolveProvisionTargetCore` read `false` as "not published" and
dropped a tier, so a five-second network blip walked a user from `cdn-exact` to
`major-fallback`. The reproducer: 4 of 15 TCP connects to the CDN's IPv4 address black-holed
for ~135 s while `curl` succeeded 5/5 at 12 ms moments later. `ResolveVersion`'s `string?`
had the identical defect one tier down — "the index was read and nothing matched" and "the
index was never fetched" were the same `null`.

Three options were on the table.

**Demote anyway (the old behavior).** Rejected: this is the bug. A demotion is the only branch
in the resolver that can hand the caller a KNOWN-DEGRADED artifact, and the single observation
that licenses it is a response from the CDN. An unanswered question is not that observation.
`loud-failures.md` is the general form — a signal must not be reported as a stronger conclusion
than it supports — and `NetworkDiagnosis`'s own header states the rule this violates: *a claim
about the remote service requires bytes from the remote service.*

**Throw.** This reads like the `loud-failures.md` answer, and it is not, because holding is
*already* the loud outcome and strictly better. What follows the tier decision is the download
of the held target, which has exactly two ends: it succeeds, in which case the fault was the
transient blip and the user gets the artifact version selection actually wanted; or it fails,
and fails with `NetworkDiagnosis`'s classified observation naming the real fault. Neither end
is a silent degradation, and only the holding end can still recover. Throwing would convert
every transient blip into a hard provisioning failure — worse on a flaky CI network than the
problem being fixed, which is the trade the issue explicitly asks to be made deliberately.

**Hold the tier (implemented).** The target stays the version selection wanted. The tier name
records that the CDN was never reached, so `ProgramSupport.UndeterminedProbeNotice` can say
that plainly instead of hedging — issue #2981's third suggested shape, and the thing #2926
could not do because the bool gave it nothing to say it with.

## The retry

`ArtifactDownloader.ProbeVersion` and `ProbeVersionPrefix` retry **once** when the answer is
`Undetermined`, before returning it. The reported fault was intermittent, so a single retry
clears the common case outright. A definite answer is never retried: a 404 is a fact, and
re-asking would double the cost of the ordinary withdrawn-build path for nothing.

`ProbeWithRetry(Func<CdnProbeResult>)` is the seam — the whole probe is the delegate, so the
retry is testable without a network (`AlRunner.Tests/UndeterminedCdnProbeTests.cs`).

## The other `ResolveVersion` callers were checked and are correct

Four sites in `ProgramSupport/Provisioning.cs` plus one in `Program.cs` also call
`ResolveVersion` and read `null`. None has this defect: every one of them **stops** — returns
exit 2, or warns and returns — rather than selecting a different artifact. "No version, no
work" is a sound reading of `null` whichever of the two states produced it, and their messages
("could not resolve a full BC version for prefix ...") already claim nothing about what
Microsoft published, with `NetworkDiagnosis`'s observation printed above them. They keep the
`string?` wrapper deliberately; only the tier resolver needs the third state, because only the
tier resolver treats a missing answer as licence to pick something else.

## Sister reading

- `.claude/rules/loud-failures.md` — a signal may not be reported as a stronger conclusion
  than it supports
- `AlRunner.Provisioning/NetworkDiagnosis.cs` — the `NetworkFailureKind` the undetermined
  states carry, and the #2926 argument they come from
