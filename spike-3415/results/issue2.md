_Filed by Claude Code (main session) at the owner's request._

## Problem

A user asked why `Environment Information.IsProduction()` returns `true` under the runner. It's intentional: the tenant carries BC's default `EnvironmentType = Production`, so `IsSandbox()` and `IsSaaS()` are false (#3514, `docs/limitations.md#environment-type`). But nothing in `--guide` or `--help` says so. A user or coding agent sees AL branch on `IsProduction()`/`IsSaaS()` differently from a sandbox and has no pointer to the explanation.

A second, smaller problem: `--guide` refers to `docs/limitations.md` and other `docs/` files by relative path. Someone running the installed tool doesn't have the repository, so those paths lead nowhere.

## Expected behavior

`--guide` has a short section on the environment AL code sees by default, one or two lines per point, each with a pointer:

- Environment type: Production, not a sandbox, not SaaS (`IsProduction()` true, `IsSandbox()`/`IsSaaS()` false), plus how a test switches it, and that the al-language corpus tier runs as a sandbox.
- Service topology: always the on-premises branch, even after a test switches to SaaS.
- No license: named-user license checks never refuse a write (#4700).

`FURTHER READING` also says where the `docs/` paths resolve online.

## Acceptance criteria

- [ ] Text assertions on `PrintGuide` output (the pattern of `TestDataCompanyHelpTextTests`), RED before the change, GREEN after
- [ ] CLI documentation drift tests still pass
- [ ] Do NOT edit `CHANGELOG.md`
