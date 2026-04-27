---
paths:
  - "docs/coverage.yaml"
  - "AlRunner/**"
  - "tests/**"
---

# coverage.yaml must be updated in every feature PR

Every PR that implements a feature, mock, overload, or rewriter rule **must** update `docs/coverage.yaml`. The orchestrator blocks merge when this file is missing from the diff.

**Track at overload level.** If a method has multiple overloads, each gets its own entry — e.g. `File.UploadIntoStream (5-arg)` and `File.UploadIntoStream (6-arg)` are separate. The auto-generated coverage scan only sees method names, not signatures, so telemetry gaps frequently surface missing overloads. When you fix a telemetry-surfaced gap, add the specific overload entry even if the parent method already shows `covered`.
