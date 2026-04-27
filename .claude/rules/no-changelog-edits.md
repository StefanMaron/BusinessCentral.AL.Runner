# Never edit CHANGELOG.md

`CHANGELOG.md` is generated from squash-commit messages by `.github/scripts/generate_changelog.py` after merge to `main`. Editing it in a PR creates merge conflicts that block the queue.

**Rule:** never stage, edit, or include `CHANGELOG.md` in any PR. The orchestrator rejects PRs that touch it.
