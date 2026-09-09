# Runtime packages (`.NEA`)

A **runtime package** is a `.app` Business Central produces when an extension is published
without its source: no `/src/*.al`, but the complete output of the AL compile — BC's own
generated C# and BC's own materialized object metadata. It is another package distribution
format, not a lesser one. A customer is handed one, deploys it, and runs tests against it on a
service tier.

This document records what one contains, how the runner reads it, and what has and has not been
measured. Issue #3537 is the origin; #3531 has the first readability measurements.

## The container

An ordinary `.app` is a 40-byte NAVX header whose payload, at the offset the header names, is a
plain ZIP. A runtime package has the identical NAVX header and puts a **`.NEA` container** at
that offset instead.

```
offset 0    'N','A','V','X'                       (magic)
offset 4    LE uint32 = 40                        (payload offset)
offset 8    LE uint32 = 2                         (format version)
offset 12   16 bytes                              (app GUID)
offset 28   LE uint64                             (payload length)
offset 36   'N','A','V','X'                       (trailing magic)
offset 40   payload
```

At offset 40:

| package | first bytes | meaning |
|---|---|---|
| ordinary `.app` | `50 4B 03 04` (`PK\x03\x04`) | a ZIP |
| runtime package | `2E 4E 45 41 00 00 00 01` (`.NEA` + version 1) | a `.NEA` container |

After the 8-byte `.NEA` header the remaining bytes are **RC4** with a fixed 6-byte key,
`0F 0B 51 89 B8 78`. Decode that and what you have is an ordinary ZIP — the same shape the
runner already reads.

**This is obfuscation, not encryption, and nothing here is a secret.** Both constants ship in
the clear inside `Microsoft.Dynamics.Nav.CodeAnalysis`, as
`Packaging.Nea.NeaStream.Header` and `Packaging.Nea.NeaStream.Key`, and BC's own reader
(`Packaging.Nea.NeaStreamReader.IsSupported`) takes no key argument at all. #3531 first read the
resulting high-entropy bytes as encryption; they are not.

### Why the runner reimplements the decode instead of calling BC

`NavAppPackageReader` is public and would answer all of this. `AppLoader` does not use it, on
purpose.

`AppLoader` runs on the cold-artifact-cache provisioning path, where
`Microsoft.Dynamics.Nav.CodeAnalysis` is **not yet resolvable** — that is the failure recorded in
`AlRunner/AffectedObjectId.cs`'s doc comment, where merely naming a type from that assembly in
`Program.cs` took 20 of 22 provisioning tests down with an unhandled `FileNotFoundException` and
no managed stack. Six lines of RC4 keep `AppLoader`'s zero-BC-assembly property intact, and the
format is a fixed constant rather than something that has to track a BC version.

The runner carries the header and the key as **literals** in `AppLoader` — not resolved from BC
at run time. Reflecting them out of `Microsoft.Dynamics.Nav.CodeAnalysis` is how they were
*verified* (BC's own `NeaStreamWriter` was executed over a known plaintext, and an independent
RC4 reproduced its output byte for byte), but doing that at run time would reintroduce exactly the
assembly dependency the previous paragraph rules out.

The decode lives in `AppLoader`, and both other `.app` readers — `BcAppSymbolCache`
(`SymbolReference.json`) and `NavAppResourcePatches` (`/resources/*`) — call into it, so the
format has one implementation rather than three copies of the same constants.

**If Microsoft ever changed these constants**, a changed *header* is loud: `IsNeaContainer`
answers `false`, the ZIP reader hits bytes that are not a ZIP, and the reader raises
`InvalidDataException`. A changed *key* under an unchanged header is the quieter case — a corrupt
ZIP, loud through `ExtractCSharp` but reaching `IsR2R`/`HasAlSource` as `catch { return false; }`.
No guard is built for that: the constants are byte-identical across all eleven BC builds measured
(27.0 through 28.4), and BC's own reader has no version negotiation — `NeaStreamReader.IsSupported`
takes no key argument and its private overload assigns `NeaStream.Key` unconditionally.

## What is inside one

Measured on three genuine third-party runtime packages of one app
(`365 business API 17.13.1.27844`, from the public feed at
`https://pkgs.dev.azure.com/365businessdev/Public/_packaging/MSDyn365BCRuntimeApps/nuget/v3/index.json`),
each built by a **different** BC compiler:

| built by | `/bin/*.xml` | `/bin/*.cs` | `/src/*.al` | `SymbolReference.json` |
|---|---|---|---|---|
| 27.5.46862 | 26 | 18 | 0 | yes |
| 28.1.49838 | 26 | 18 | 0 | yes |
| 28.4.53241 | 26 | 18 | 0 | yes |

`/bin/TAB<id>.xml` is a fully materialized `MetaTable` document
(`MetadataVersion="130000"`) — the shape `MetaTable.CreateMetaTableFromXml` consumes.
`/bin/<Prefix><id>.cs` is the generated C#, post-AL-compile and pre-C#-compile: the same input
BC's own service tier Roslyn-compiles into its assembly cache.

**A runtime package and a Microsoft shipped app are exact complements.** Measured on
`Microsoft_Base Application_28.4.53241.53989.app`: 8,095 AL source files and **zero** `/bin`
metadata documents. So a shipped app must be compiled to produce metadata, and a runtime package
already contains it.

## What the runner answers about one

Every `AppLoader` reader funnels through two places — `OpenAppZip` (path, streaming) and
`OpenZipFromNavx` (bytes, for the nested R2R case) — so decoding at those two points is what
makes all of them work. Measured before and after, on the three real packages above:

| | before | after |
|---|---|---|
| `ReadManifest` | `null` | the real identity |
| `HasSymbolReference` | `false` | `true` |
| `ExtractCSharp` | throws `InvalidDataException` | 18 sources |
| `ExtractAl` | throws `InvalidDataException` | 0, correctly |
| `IsR2R` | `false` | `false`, correctly |

The two `false` answers in the "before" column are the more dangerous half, and neither announced
itself: `IsR2R` and `HasAlSource` reach their answer through `catch { return false; }`, so a
package the runner could not read at all was reported as one that simply carries no
implementation — which drops it from `DependencyResolver`'s scan set with no diagnosis.
`ReadManifest` returning `null` at least routes to `ReportUnreadablePackage`.

## The metadata property set (settles #3531's open question)

#3531 observed four attributes absent from both a 15.0 and a 22.0 package and asked whether that
was the generator's doing or version drift. Counted over the `/bin/TAB*.xml` documents of the
three current packages above, identically on all three:

| attribute | present |
|---|---|
| `AllowInCustomizations` | **5/5** |
| `ValidateTableRelation` | 5/5 |
| `DataClassification` | 5/5 |
| `Editable` | 5/5 |
| `EnumTypeId` | 2/5 (only where a field is an enum) |
| `TestRelations` | 0/5 |
| `ValidateRelation` | 0/5 |
| `PlatformDefinedFieldsAdded` | 0/5 |

So the caveat was wrong in two different ways. `AllowInCustomizations` **was** version drift — it
is present in every current package. And `TestRelations` and `ValidateRelation` were never
missing: current BC spells that attribute `ValidateTableRelation`, so counting the other two
spellings was counting names that do not exist. `PlatformDefinedFieldsAdded` is genuinely absent.

## What is not shown

- **Executing tests from a runtime package end to end is not wired up.** Reading one is;
  consuming its `/bin/*.cs` as the compile input instead of an AL emit is a separate change
  (#3732 tracks it). The proof that the mechanism works is in #3537's own thread: 45
  dependency-free bundles, 44 of them producing identical results from the package alone.
- **Only the container format is claimed to be version-stable.** Header and key are byte-identical
  across the 27.5, 28.1 and 28.4 builds measured. The *generated C#* inside pins to the BC version
  that produced it, and running a package across a BC minor has not been measured.
- **The three packages measured are one app.** They cover three BC compilers, not three shapes of
  app: no PermissionSet, Profile, Entitlement or DotNet package objects were exercised, and no
  package carrying report layouts was.
- **`/bin/ENUM*.xml` omits per-value `Implementation`**, which `AlEnumMetadataRegistry.Register`
  needs, and `/layout/LayoutManifest.xml` carries no `DefaultRenderingLayout`, so
  `AlReportLayoutInfo.IsDefault` has no source in the package. Both are recorded in #3537.
