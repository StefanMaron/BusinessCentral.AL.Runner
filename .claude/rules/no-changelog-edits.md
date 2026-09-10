# Never edit CHANGELOG.md

`CHANGELOG.md` is generated from squash-commit messages by `.github/scripts/generate_changelog.py` after merge to `main`, so an edit in a PR is either overwritten or becomes a merge conflict that blocks the queue behind it. **Never stage, edit, or include `CHANGELOG.md` in any PR.**

Enforced, since #3677, by `pr-gate.yml`'s `CHANGELOG.md must not be changed in a pull request` job (`.github/scripts/check_changelog_untouched.sh`) — no escape hatch and no bypass label, because the two workflows that legitimately write the file (`sync-changelog-unreleased.yml`, `publish.yml`) push straight to `main` and never open a PR. Until a maintainer adds that context to the branch ruleset it reports without gating (`ci-verdicts.md` §2 on the pending mechanism), so a red tick there still has to be honoured by hand.
