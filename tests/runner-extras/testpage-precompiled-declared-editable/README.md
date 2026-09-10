# testpage-precompiled-declared-editable (#3504)

A field control's declared `Editable` / `Enabled` must be honoured on a page that ships
**precompiled** in a dependency `.app`.

## The defect

`DependencyPageMetadataXml` reconstructs no control tree for such a page. That is correct for a
control's **value binding**, which lives in the `.app`'s IL and no XML here could rebuild — the
file header says so and it is unchanged. The consequence was not: `ControlDefinition(id)`
answered null for every control on every precompiled page, and `EvaluateProperty`'s first arm
reads null as *"this element publishes no such property at all"*, whose answer is the AL default
of `true`.

Two different conditions — *the AL declared nothing* and *there is no definition to ask* — had
one answer.

## Scale, measured

Base Application 28.1.49838.53910, read out of the shipped `SymbolReference.json`:

| | pages | field controls |
|---|---|---|
| | 2,610 | 37,185 |

| property | declared on | literal | bare identifier | compound expression |
|---|---|---|---|---|
| `Editable` | 5,920 | 4,915 | 658 | 347 |
| `Visible` | 11,505 | 8,113 | 3,182 | 210 |
| `Enabled` | 797 | 121 | 452 | 224 |

**18,222 declarations, every one answered `true`.** 83% of the `Editable` ones are the
compile-time literal, so most need no page state at all.

Page 46 "Sales Order Subform" — the issue's own surface — declares `Editable` on 19 of its 102
field controls, nine of them the literal `false`. The reported control is

```
id=308617479  name='Invoice Discount Amount'  src='InvoiceDiscountAmount'  Editable='InvDiscAmountEditable'
```

`InvDiscAmountEditable` is a **page variable**, not a table field and not a constant.

## What this suite proves, and what it does not

Committed here (end-to-end, through a real `TestPage`):

- a control declaring `Editable = false` reports `false`;
- a control declaring `Enabled = false` reports `false`;
- a control declaring **neither** still reports `true` — AL's default, and the negative that
  stops the fix becoming the opposite wrong answer;
- a non-editable control is still **readable**, because this fix answers a property and
  deliberately does not change reachability.

**Not here: the expression arm.** Driving `Editable = SomePageVariable` end-to-end needs a
precompiled dependency whose page both declares the variable *and* registers it through its own
compiled `RegisterSourceExpression` IL. The committed fixture is reused from
`testpage-precompiled-dep-control`, whose page 65601 has no page variables, so that arm is
pinned directly against the resolver in
`AlRunner.Tests/DependencyControlDeclaredPropertyTests.cs`, where the symbol shape is modelled
attribute-for-attribute on page 46's real declaration.

That the registered expression is *available* on a precompiled page was verified by execution
while diagnosing this: a page declaring `Editable = FreeTextEditable` registered the key
`p65701p65701FreeTextEditable` in `NavForm.SourceExpressions` at run time, which is exactly what
`EvaluateProperty` resolves. The expression was never the missing piece — the definition was.

## Why this is not a corpus test

A corpus test compiles its page **from source**, so it takes the source-parsed branch and never
reaches the dependency-metadata synthesizer. The condition under test cannot be constructed
upstream. Real BC's answer for "a control declaring `Editable = false` reports
`Editable() = false`" is not in doubt and is not what this pins; what it pins is that the runner
stops discarding the declaration.

## Regenerating the fixtures

`.deps-bin/*.dll` is copied verbatim from `testpage-precompiled-dep-control` — same dependency,
same compiled page 65601, no changes. `.alpackages/*.app` is that suite's symbol package with
two properties added to the `Description` control (`Editable = false`, `Enabled = false`) and
`Additional Information` left declaring neither:

```bash
python3 - <<'EOF'
import zipfile, io, json, struct, uuid
src = "tests/runner-extras/testpage-precompiled-dep-control/.alpackages/AL_Runner_Fixtures_TPCD_Precompiled_Control_Dep_1.0.0.0.app"
d = open(src, 'rb').read(); i = d.find(b'PK\x03\x04')
z = zipfile.ZipFile(io.BytesIO(d[i:]))
sym = json.loads(z.read('SymbolReference.json'))
for c in sym['Pages'][0]['Controls'][0]['Controls']:
    if c['Id'] == 65601001:
        c['Properties'] += [{"Name": "Editable", "Value": "false"},
                            {"Name": "Enabled",  "Value": "false"}]
buf = io.BytesIO()
with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
    zf.writestr("NavxManifest.xml", z.read('NavxManifest.xml').decode())
    zf.writestr("SymbolReference.json", json.dumps(sym))
    zf.writestr("[Content_Types].xml", z.read('[Content_Types].xml').decode())
zb = buf.getvalue()
APP_ID = "d1e2f3a4-5b6c-4d1e-9f8a-1b2c3d4e5f61"
header = (b"NAVX" + struct.pack("<I", 40) + struct.pack("<I", 2)
          + uuid.UUID(APP_ID).bytes_le + struct.pack("<Q", len(zb)) + b"NAVX")
open("tests/runner-extras/testpage-precompiled-declared-editable/.alpackages/"
     "AL_Runner_Fixtures_TPCD_Precompiled_Control_Dep_1.0.0.0.app", "wb").write(header + zb)
EOF
```

Both fixtures are `git add -f`: `.gitignore` excludes `*.app`, as it does for the sibling suite.

`testpage-precompiled-dep-control/REGENERATE-FIXTURES.txt` has the full account of how the
underlying `.app` and `.dll` were produced, including why the `.app` needs a real
`SymbolReference.json` and why the dependency's AL source must never live inside a suite folder.

## Verify

```bash
dotnet run --project AlRunner -c Release -- --bc-version 28.1 --strict \
    tests/runner-extras/testpage-precompiled-declared-editable
```

RED without the fix (`DeclaredEditableFalse_IsHonoured` and `DeclaredEnabledFalse_IsHonoured`
both report `true`), GREEN with it. Fully hermetic — no `--package-cache` and no Base
Application needed.
