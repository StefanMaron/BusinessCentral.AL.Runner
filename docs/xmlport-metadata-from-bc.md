# Reconstructing a precompiled xmlport's metadata document

`AlRunner/Patches/DependencyXmlPortMetadata.cs` synthesizes the runtime metadata document for an
xmlport that lives in a **precompiled dependency** `.app` — one the runner never source-compiles,
so `AlXmlPortMetadataRegistry` (which the emit pipeline fills) never holds it. This page is the
measurement record its header points at.

The sibling page for reports is
[`report-metadata-from-bc.md`](report-metadata-from-bc.md); `DependencyReportMetadata.cs` did the
same job for reports first, and this derivation is deliberately built to its shape.

<a id="why-the-source-and-not-the-symbol-file"></a>

## Why the node tree comes from `src/`, not from `SymbolReference.json`

Measured on BC **28.1.49838.53910**, Base Application and System Application:

| question | answer |
|---|---|
| xmlports the symbol files declare | **44** (Base Application 40, System Application 4) |
| how many the top-level `"XmlPorts"` array holds | **0** — all 44 are nested under `Namespaces` |
| keys a nested xmlport entry carries | `Id`, `Name`, `Properties`, `Variables`, `Methods`, `ReferenceSourceFileName` |
| node trees the symbol file states | **none, for any of the 44** |
| xmlports stating a `ReferenceSourceFileName` | **44 of 44** |
| whose `.al` file is present inside the `.app` | **44 of 44** |
| schema nodes in Base Application's 40 source files | **3,904** (`textelement` 1,593, `fieldelement` 1,482, `textattribute` 357, `fieldattribute` 277, `tableelement` 195) |

So the node tree is **not** stated by the compiler's symbol file and **is** fully recoverable from
the AL source the `.app` embeds. That is the whole reason this derivation reads two sources.

Reproduce the first three rows with:

```bash
python3 - <<'EOF'
import zipfile, io, os, json
p = os.path.expanduser('~/.al-runner/platform-apps/Microsoft_Base Application_28.1.49838.53910.app')
d = open(p,'rb').read(); i = d.find(b'PK\x03\x04')
z = zipfile.ZipFile(io.BytesIO(d[i:]))
ib = z.read([n for n in z.namelist() if n.endswith('.app')][0])
j = ib.find(b'PK\x03\x04'); z2 = zipfile.ZipFile(io.BytesIO(ib[j:]))
sr = json.loads(z2.read('SymbolReference.json').decode('utf-8-sig'))
def walk(c, out):
    for x in c.get('XmlPorts') or []: out.append(x)
    for ns in c.get('Namespaces') or []: walk(ns, out)
xps = []; walk(sr, xps)
print('top-level:', len(sr.get('XmlPorts') or []), 'total:', len(xps))
print('keys:', sorted(xps[0].keys()))
EOF
```

**Trap: System Application's four are double-encoded, and that WAS a live defect.** Their
`ReferenceSourceFileName` reads `Permission%20Sets/…` while the zip entry is
`src/Permission%2520Sets/…` (`%25` is a literal `%`, so `%2520` decodes once to `%20`).
`BcAppSymbolCache.TryReadSourceFile` matches on suffix, so the stated path found nothing and
those four read as "this .app ships no source" — the symbol resolved, the source read answered
null, and the derivation correctly refused.

It is fixed: the reader re-probes with `%` → `%25` **after** the plain suffix match has already
failed, so a correctly packaged path still resolves on the first match. Measured: **40 of 44
reconstructing before, 44 of 44 after**, with the four being System Application's 9001, 9862,
9863 and 9864 and none of Base Application's 40 affected. Pinned in both directions by
`AlRunner.Tests/DependencyXmlPortMetadataTests.cs`'s `BcAppSourceFileEncodingTests`, whose two
opposite mutations red disjoint sets.

The fix is in the shared reader, so it applies to **every** caller, reports included.

<a id="the-fixture"></a>

## The fixture the values below come from

Two probe xmlports, compiled by the runner so BC's own emitter produces the ground-truth document,
which `AL_RUNNER_TRACE_XMLPORT_METADATA=2` prints:

- **61602** `"XPDDep Export Port"` — `textelement` → `tableelement` → two `fieldelement`s. This is
  the same shape `tests/runner-extras/xmlport-precompiled-dep-metadata` ships as a precompiled
  dependency.
- **61603** `"XPD Probe Port"` — all **five** AL schema keywords in one port, which is what pins
  the attribute/element split below.

```bash
AL_RUNNER_TRACE_XMLPORT_METADATA=2 dotnet run --project AlRunner -c Release -- \
    --bc-version 28.1 --package-cache "$HOME/.al-runner/platform-apps" <probe-bundle>
```

<a id="object-properties"></a>

## Object-level properties, and the default each takes when unstated

Every row measured against BC's own emitted document. The **default** column is what BC wrote for
a port declaring nothing, which is what the synthesizer writes in that case.

| element | source | default when unstated |
|---|---|---|
| `Direction` | symbol file | `Both` |
| `Format` | symbol file | `Xml` |
| `Encoding` | symbol file | `UTF-16` |
| `UseRequestForm` | symbol file's `UseRequestPage` | **`1`** — AL's default is TRUE |
| `PreserveWhiteSpace` | symbol file | `0` |
| `DefaultFieldsValidation` | symbol file | `1` |
| `InlineSchema` / `UseDefaultNamespace` / `UseLax` | symbol file | `0` |
| `TransactionType` | symbol file | `UpdateNoLocks` |
| `FormatEvaluate` | — | `C/SIDE Format/Evaluate` |
| `XmlVersionNo` | — | `1.0` |
| `DefaultNamespace` | symbol file | `urn:microsoft-dynamics-nav/xmlports/x<id>` |

Two spelling traps, both measured:

- **`Encoding` is spelled differently on each side.** AL and the symbol file write `UTF8` /
  `UTF16`; BC's document writes `UTF-8` / `UTF-16`. Issue #3797 measured the runner answering
  `UTF16` where BC answered `UTF-8` on three System Application xmlports.
- **Booleans are `"1"`, never `"true"`.** Microsoft's packages write the digit, so a reader
  matching only the word answers false for every xmlport that declares the property — the measured
  shape of #3790 on the codeunit side. `BcAppSymbolCache.SymbolBoolValue` accepts both.

<a id="node-ids"></a>

## Node ids, and why the xmlport id is part of them

BC writes each node's `ID` as

```
{0000XXXX-NNNN-0000-0F00-0000836BD2D2}
```

`XXXX` is the **xmlport id in hex**, `NNNN` the **1-based node sequence** in the whole schema.
Xmlport 61602 is `0xF0A2`, so its first node is `{0000F0A2-0001-0000-0F00-0000836BD2D2}` and its
fourth is `{0000F0A2-0004-…}`.

Identical on two distinct BC 28 builds — **28.1.49838.53910** and **28.3.52162.54347** — over both
probe ports.

**The xmlport id being part of the id is load-bearing, not decorative.** BC resolves a node's
parent by matching `ParentID` against another node's `ID`, so a bare sequence would collide across
two xmlports in one run and reparent nodes between ports.
`DependencyXmlPortMetadataTests.NodeIds_EncodeTheXmlPortIdAndTheNodeSequence` pins both halves.

<a id="node-shape"></a>

## What each AL keyword emits

| AL keyword | `NodeType` | `SourceType` | binding element | occurrence elements |
|---|---|---|---|---|
| `textelement` | `Element` | `Text` | — (`VariableName`) | `MaxOccurs` + `MinOccurs` |
| `textattribute` | `Attribute` | `Text` | — (`VariableName`) | **`Occurrence`** |
| `tableelement` | `Element` | `Table` | `SourceTable` (table id) | `MaxOccurs` + `MinOccurs` |
| `fieldelement` | `Element` | `Field` | `SourceField` (`Record::Field`) | `MaxOccurs` + `MinOccurs` |
| `fieldattribute` | `Attribute` | `Field` | `SourceField` | **`Occurrence`** |

An attribute takes `Occurrence` where an element takes the `Max`/`Min` pair — a different element
name, not a different spelling of the same one.

**`ParentID` is the all-zero GUID for a text node and the real parent for a bound one.** That is
BC's own behaviour, not a simplification: on probe 61603 a `textelement` nested *inside* a
`tableelement` carries `{00000000-…}` while its `fieldelement` sibling carries the tableelement's
id. A text node's containment is expressed by `Indentation` alone.

<a id="datatype"></a>

## `DataType` must not carry the AL length suffix

A bound node's `DataType` is the AL type **without** its length: `Code[20]` → `Code`.

BC's `MetaXmlPort` reader takes it through a **case-sensitive `Enum.Parse`** over `NavType`, which
has no `Code[20]` member, so the declared spelling throws

```
ArgumentException: Requested value 'Code[20]' was not found
```

and kills the whole document over one node. Measured on fixture xmlport 61602 while implementing
#3797.

A type the live enum does not know makes the element **omitted**, never written as raw text: an
unparseable value costs the whole document, an absent one costs a single node its declared type.

<a id="request-page"></a>

## The `<RequestPage>` subtree is not optional

BC's compiled xmlport class declares a nested `RequestPage` whose constructor takes a `MasterPage`,
and `InitializeComponent` calls it **unconditionally**. With no `<RequestPage>` element,
`NavForm..ctor(ITreeObject, MasterPage)` dereferences a null MasterPage and the xmlport's own
constructor dies with a bare `NullReferenceException` — measured on fixture 61602.

So a document without it is not a reduced document; it is one BC cannot construct.

Only the **frame** is derived, for the reason `DependencyReportMetadata`'s request page gives:
`MetadataProvider` merges this into BC's own master-page template, so the built-in controls come
from the template. What *is* derived is the per-`tableelement` filter control and its matching
expression, named `XmlPort<id>DataItem<node sequence>TableView` — measured as
`XmlPort61602DataItem2TableView` for the tableelement that is node **2** of 61602, and
`XmlPort61603DataItem3TableView` for node **3** of 61603. The number is the node's sequence in the
whole schema, **not** an index among tableelements.

`PageDefinition` must be the **first** child, because BC reads `val.FirstChild` rather than a child
found by name.

<a id="what-still-refuses"></a>

## What still refuses

An xmlport whose node tree cannot be recovered is **not** answered. `TryBuildDependencyXmlPortMetadata`
returns null and the caller's `RunnerOutOfScopeException` stands.

That is deliberate and it is the whole reason the removed expectation entry
(`tests/expectations/oos-xmlport-precompiled.json`) argued for `expect-oos` over
`expect-fail-known-gap`: a document with no `<Node>` reads to BC as *"this port has an empty
schema"*, so the port would **silently export nothing** — a green test asserting a wrong answer,
which is worse than a loud refusal.

The case that reaches it in practice is a **symbols-only `.app`** — one shipping
`SymbolReference.json` and no `src/`. That is a legitimate package shape rather than an error, so
it refuses rather than throwing on the read.
