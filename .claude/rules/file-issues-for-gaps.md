# File issues for runner gaps

If AL code fails to run and the reason is **not** in `docs/limitations.md`, that is a runner gap — not a problem with the AL code. Never silently work around it.

**Rule:** open a GitHub issue using `.github/ISSUE_TEMPLATE/runner-gap.md` immediately. The goal is to wipe gaps out systematically; gaps that are worked around silently get re-discovered repeatedly.

## File once

1. **Search the open queue for the exact title first**, with the title in inner quotes:
   ```
   gh issue list --state open --search "\"<exact title>\" in:title" --json number,title --repo StefanMaron/BusinessCentral.AL.Runner
   ```
   A returned `title` equal to yours, character for character, is the issue you were about to file: comment your findings there and stop; a returned title that merely contains yours is not. Quote the title — an unquoted `in:title` search answered zero for an existing title containing an apostrophe, and the quoted form found it (#3724).
2. **After a timeout or an error from `gh issue create`, list the newest issues before retrying:**
   ```
   gh issue list --state open --limit 10 --json number,title,createdAt,author --repo StefanMaron/BusinessCentral.AL.Runner
   ```
   A title equal to yours, by your login, created in the last ten minutes is the issue you just filed — `gh issue create` reports a timeout on a call that already created it. Comment there and stop.

Done when you have commented on one issue whose title equals yours (the oldest, when several exist; name the others in that comment), or created one.
