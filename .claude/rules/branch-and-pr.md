# Branch and PR rules

- **Never push directly to `main`.** Always via PR. Branch protection enforces this; agents must respect it even if a task says "push to main".
- **Branch name:** `agent/<agent-id>/issue-<N>` — no exceptions.
- **PR body must contain `Closes #N`** so the linked issue auto-closes on merge.
- **One open PR per impl agent.** Do not claim a second issue while a PR is open.
- **Set `status: review-ready`** on the PR once CI is green — that is how the orchestrator finds your work.
- **Concurrency with human maintainers.** This is a public repo. When claiming an issue, also assign it to `@me` (`gh issue edit <N> --add-assignee @me`, or `mcp__github__issue_write` with `method: update` and your login in `assignees`). Skip any issue or PR whose assignee is a user other than `@me` — a human maintainer is already on it. The assignee field is the boundary between "agent-owned" and "human-owned" work. A repo owner can waive this for a specific PR, but an agent never waives it on its own.
- **GitHub access:** never assume the `gh` CLI exists — it is absent in web/remote sessions. See `.claude/rules/github-access.md`.
