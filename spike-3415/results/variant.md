_Filed by Claude Code (main session) while checking the published 2.12.0 package._

## Problem

The package ships one `al-runner.dll` at `tools/net8.0/any/` and one per BC minor under `tools/net8.0/any/variants/<build>/`. The runner switches to the matching variant whenever the selected BC version isn't the root build's version. The variants are built differently from the root:

| | root `al-runner.dll` | every `variants/<build>/al-runner.dll` |
|---|---|---|
| configuration | Release | **Debug, optimizations off** (`DebuggableAttribute` blob `01 00 07 01`) |
| version | `2.12.0` | `0.0.0-main` |

Checked in the published 2.12.0 package (GitHub Release asset, sha256 prefix `36af7819f620789c`) and in the installed 2.11.0 package (28.1 variant). Both releases have it.

**Cause:** `publish.yml`'s engine-variant loop (around line 237) runs `dotnet build AlRunner/ -p:_BCVersion="$full" -p:AllowBcArtifactDownload=true -o ...`. There is no `-c Release`, and none of the version, SourceLink or informational-version properties the root build passes (around line 320).

## What users see

- **Speed:** anyone whose BC version isn't the root's (28.5.54151.55364 in 2.12.0) runs an unoptimized runner. The owner's own comparison in #4935 was affected: released v2.11.0 on BC 28.1 took 113 s against 80 s for a local Release build of `main`. Measured Release-against-Release on the same machine, v2.11.0 was actually faster (40.5 s against 45.6 s warm).
- **Version shown:** the run header and anything else that prints the version say `al-runner 0.0.0-main`, while `--version` says `v2.12.0`. Gap reports from those users name no version.

Reproduce: install 2.12.0 and run any bundle with `--bc-version 27.5`. The header prints `al-runner 0.0.0-main · BC 27.5…`. Without `--bc-version` (root build) it prints `al-runner 2.12.0 · BC 28.5…`.

## Expected behavior

Every variant is a Release build carrying the package version, the same as the root.

## Acceptance criteria

- [ ] The variant build in `publish.yml` passes `-c Release` and the same version, SourceLink and informational-version properties as the root build. Check `bc-tests.yml`'s dry-run pack for the same gap.
- [ ] A guard, a `tools/test_*.py` or `.github/scripts/test_*` script so it's picked up automatically, fails when a variant build line lacks `-c Release` or `-p:Version`. Or check the packed variants' `DebuggableAttribute` and informational version in the dry-run pack job.
- [ ] Confirmed on a real package: the dry-run pack shows every variant as Release with the package version.
- [ ] Do NOT edit `CHANGELOG.md`.
