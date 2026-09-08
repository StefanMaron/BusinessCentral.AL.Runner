# metadata-equivalence

The declared state of the total comparison between BC's own metadata emitter and the runner's
`SymbolReference.json`-derived metadata. Issue #3533.

| file | what it decides |
|---|---|
| `apps.json` | which Microsoft apps the harness MUST cover. Declaring an app makes covering it mandatory: the harness fails if no ground-truth bundle for it is present. |
| `allowlist.json` | which member differences are tolerated, and why. Anything not listed fails; an entry that matches nothing also fails. |

The ground truth itself is not here — it is regenerated per BC build by
`tools/gen-metadata-ground-truth.sh`. [`docs/metadata-equivalence.md`](../../../docs/metadata-equivalence.md)
has the full picture, including why it is not checked in.
