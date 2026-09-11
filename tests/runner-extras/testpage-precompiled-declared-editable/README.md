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

**Not here, and not anywhere yet: resolving an expression-bound property.** That arm IS
exercised — `ExpressionBoundProperty_RefusesLoudlyRatherThanGuessing` drives a control declaring
`Visible = SomeUnpublishedGlobal` — but what it pins is the **refusal**, not a resolution.

Measured over the 92 `<Expression>` entries in this machine's 2,272 captured page-metadata
documents (written by the real AL compiler): the binding key lives in the element's `Name`, the
raw AL identifier in its `SourceExpression`, and the two **differ on 59 of the 92**. So the
mapping is a stored pair, not a transformation of the name — and one identifier can carry
several keys (page 60265 has both `Control144826568` and `p60265p60265HideIt` for `HideIt`).

For a precompiled page that pair is absent: `DependencyPageMetadataXml` synthesizes
`<Expressions>` present-but-empty on purpose. So the runner refuses, naming the expression.
**#3825 tracks resolving it for real.**

That literal-only coverage is not a detail. An earlier revision of this fix shipped a
name-based join that matched nothing, and every unit test passed because the fixture asserted
the relationship instead of measuring it — while every AL test declared a literal and
short-circuited before the lookup. This arm is what closes that hole.

## The action arm (#2460)

`PrecompiledDeclaredActionTests.Codeunit.al` does for ACTIONS what the file beside it does for
controls, through a real `TestPage`:

- an action declaring `Enabled = false` reports `false`;
- an action declaring `Visible = false` reports `false` — and still reports `Enabled = true`,
  because a hidden action is not a disabled one;
- an action declaring **neither** reports `true` for both;
- an action whose `Enabled` is bound to a page global **refuses loudly**, naming the
  expression, rather than answering `true`.

**Why this was missing.** #3819 wired the action arm as a two-line mirror of the control arm and
said so in its own PR body: the committed fixture's page declared no actions, so nothing drove
`DeclaredActionProperty` end to end. Everything referencing it asserted against
`TryGetDependencyActionDeclaredProperty`, the string resolver one layer below — a test that names
the thing rather than driving it.

**Why #2460's proposed action tree is not what closed it.** The issue asked for
`<ActionContainers>`/`<Actions>` in the synthesized metadata. Measured against BC's own compiler
output, that reconstruction answers nothing the flat symbol lookup does not already answer:

| `Enabled` on a Base Application action | 28.1 | 27.3 |
|---|---:|---:|
| absent (AL declared none -> default `true`) | 24,179 | 23,990 |
| expression-bound (#3825 owns this) | 1,116 | 1,101 |
| literal `true` | 10 | 10 |
| **literal `false`** | **3** | **6** |

Only the literal changes an answer, and #3819's id-keyed symbol lookup already resolves it with
no tree. Joining the 235 compiled `PageDefinition` documents in
`~/.local/share/al-runner/metadata-ground-truth/28.1.49838.53910` against the matching symbol
files, action by action, shows why the compiled documents look so much richer: **293 of their
literal `Enabled='true'` values come from declarations that are absent upstream** — the compiler
writing out the AL default — against 132 genuinely expression-bound and 1 genuine literal.

### Regenerating the action half of the fixture

The `.app`'s page gains an `Actions` tree nested inside a `group()` node, because that is where a
real page's actions live — a collector reading only the top level of `Actions` would find none of
them and every one of these tests would pass against a resolver that had found nothing:

```bash
python3 - <<'EOF'
import zipfile, io, json, struct, uuid
src = ("tests/runner-extras/testpage-precompiled-declared-editable/.alpackages/"
       "AL_Runner_Fixtures_TPCD_Precompiled_Control_Dep_1.0.0.0.app")
d = open(src, 'rb').read(); i = d.find(b'PK\x03\x04')
z = zipfile.ZipFile(io.BytesIO(d[i:]))
sym = json.loads(z.read('SymbolReference.json'))
sym['Pages'][0]['Actions'] = [{
    "Kind": 3, "Id": 65601900, "Name": "Processing",
    "Actions": [
        {"Kind": 2, "Id": 65601901, "Name": "DisabledAction",
         "Properties": [{"Name": "Enabled", "Value": "false"}]},
        {"Kind": 2, "Id": 65601902, "Name": "HiddenAction",
         "Properties": [{"Name": "Visible", "Value": "false"}]},
        {"Kind": 2, "Id": 65601903, "Name": "PlainAction", "Properties": []},
        {"Kind": 2, "Id": 65601904, "Name": "WizardAction",
         "Properties": [{"Name": "Enabled", "Value": "BackActionEnabled"}]},
    ]}]
buf = io.BytesIO()
with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
    zf.writestr("NavxManifest.xml", z.read('NavxManifest.xml').decode())
    zf.writestr("SymbolReference.json", json.dumps(sym))
    zf.writestr("[Content_Types].xml", z.read('[Content_Types].xml').decode())
zb = buf.getvalue()
header = (b"NAVX" + struct.pack("<I", 40) + struct.pack("<I", 2)
          + uuid.UUID("d1e2f3a4-5b6c-4d1e-9f8a-1b2c3d4e5f61").bytes_le
          + struct.pack("<Q", len(zb)) + b"NAVX")
open(src, "wb").write(header + zb)
EOF
```

The actions need no counterpart in `.deps-bin/*.dll`: `LiveNavTestAction` resolves an action by
member id through the symbol-file declaration, so the compiled page never has to know about them.
That is also why the expression arm can be driven here while the control-side expression arm
cannot — an expression needs a binding the page's own IL registers, and a refusal needs only the
declaration.

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
