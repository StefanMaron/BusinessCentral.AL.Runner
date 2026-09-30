_Filed by Claude Code (main session) at the owner's request._

## Problem

`--test-data` fails whenever the BC backup for the selected version isn't already on disk, even when `--auto-provision` is on. The runner never downloads the `.bak`.

```
al-runner Pageworks.Test/ --auto-provision --test-data --test-data-company cronus
al-runner 0.0.0-main · BC 28.5.54151.55132 · 1 app
  Pageworks Tests: EXEC-FAIL: --test-data: no BC backup for BC 28.5.54151.55132 (w1) at any of
  '~/.bcartifacts.cache/sandbox/28.5.54151.55132/w1/BusinessCentral-W1.bak' or
  '~/.local/share/al-runner/artifacts/28.5.54151.55132/w1/BusinessCentral-W1.bak' — ...
```

Auto-provision had downloaded BC 28.5.54151.55132 (`platform-apps/`, `test-apps/`, and the service-tier DLLs), but not the country backup. Earlier versions worked on the same machine only because BcContainerHelper had already put their backups in `~/.bcartifacts.cache/sandbox/`.

**Triggered by:** a private project (Pageworks) run on a version BcContainerHelper had never downloaded.

## Reproduction

No AL is needed. Any test app works: on a BC version whose sandbox artifact isn't in `~/.bcartifacts.cache`, run `al-runner <dir> --test-data --test-data-company <name>` with auto-provision on (it's on by default).

## Root cause

- `TestDataOptions.ResolveBackupPath` (`AlRunner/Infrastructure/TestDataOptions.cs`) only checks two candidate paths. The comment above `CandidateBackupPaths` says the runner-artifacts path is there "so a future `provision --test-data` writing there needs no change here". That provisioning step was never built.
- The download already exists as `ArtifactDownloader.TestData` (`AlRunner.Provisioning/ArtifactDownloader.cs`). It streams only the `.bak` entry out of the country artifact with ranged requests. Its only caller is the standalone `tools/DownloadArtifacts` CLI, which the MS-bucket workflow uses (#2727). The runner's provisioning path (`RunProvisioning` / the post-`SelectVersion` manifest gate in `Program.cs`) never calls it.

## Expected behavior

With auto-provision on (the default) or the `provision` subcommand, and `--test-data` given without an explicit path, the runner downloads `BusinessCentral-<CC>.bak` for the selected version and `--country` into `<runner artifacts root>/<version>/<country>/` (the second candidate path, which already exists) when no candidate path holds it. After that, the run proceeds.

- `--no-auto-provision` never downloads. The current path-naming error stays, and it should add a hint about how to provision.
- `--test-data=PATH` never downloads.
- A failed download is a loud failure that names the URL and the reason. It must never turn into a run against an empty database.
- The download must be atomic (`ExtractEntryToFile` already writes to `.partial` and moves the file into place), so an interrupted download is never mistaken for a backup.

## Likely fix

- [x] Other: provisioning. Wire `ArtifactDownloader.TestData` into the runner's provisioning flow, gated on `TestDataOptions.Enabled && ExplicitBackupPath == null`, after the BC version is selected. Update `--help` / `--guide` text (`CliText.cs`), because the CLI documentation drift tests gate it.

## Acceptance criteria

- [ ] Proving tests (runner-specific, in `AlRunner.Tests`, with no network): a missing backup under auto-provision calls the downloader with the selected version, country, and target dir; an existing backup is not downloaded again; `--no-auto-provision` and `--test-data=PATH` never download; a downloader failure surfaces loudly.
- [ ] RED confirmed before the fix, GREEN after, with a mutation run reported in the PR
- [ ] CI matrix green on the PR's own head commit
- [ ] Do NOT edit `CHANGELOG.md`
