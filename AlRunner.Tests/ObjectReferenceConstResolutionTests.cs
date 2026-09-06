// ObjectReferenceConstResolutionTests — issue #3195.
//
// Why this is a RUNNER-side test and not a corpus one
// ---------------------------------------------------
// The BC-behaviour half of #3195 — "a const(Database::X) / const(Report::X) in a CalcFormula or
// TableRelation where() clause contributes the referenced object's ID to the condition" — is a
// claim about what a service tier computes, so it is asked upstream:
// StefanMaron/BusinessCentral.AL.Language.Tests#206 (record/TestCalcFormulaDatabaseConst.Codeunit.al,
// codeunit 60329) puts it in front of all eight BC legs. Nothing here re-states that claim, and
// the pin is deliberately NOT bumped in this PR (#3152 carries the pin move).
//
// What this pins instead is the runner's own resolver, RecordPatches.ResolveObjectReferenceConst,
// on the PRECOMPILED-DEPENDENCY route that no corpus test can reach. The corpus's fixture tables
// and reports are all source-compiled, so they resolve out of _parsedTables / _parsedReports; the
// 22 Base Application CalcFormulas and 5 TableRelations that actually carry this syntax live in
// R2R .app packages and resolve out of SymbolReference.json instead. If that half regressed,
// every "Coupled to Dataverse" FlowField would go back to raising
//   NavNCLEvaluateException: The value "Database::"Customer"" can't be evaluated into type Integer
// while the corpus stayed green.
//
// BARE VERSUS QUOTED (#3207)
// -------------------------
// This file's Theory comment claimed both spellings were covered when every fixture object had a
// space in its name and so had to be quoted. Re-measured over the shipped BC 28.1 packages: of
// the 22 Base Application CalcFormula properties carrying a Database:: const, 7 are written BARE
// (Customer, Item, Currency, Vendor, Contact, Opportunity, Resource) and 15 are quoted; the 5
// TableRelation ones are all quoted. The production code was already right — ConstValueText
// strips only a matched pair of quotes — so this was a coverage gap and an overstated comment,
// not a defect. It is a gap the corpus cannot fill either: every object it can name is
// source-compiled, so the bare spelling on the precompiled SymbolReference route reaches no test
// but this one.
//
// The pass-through arm matters as much as the resolving arm. ConstValueText hands a const literal
// over as TEXT on purpose, because NCLMetaFilterConst evaluates it against the source field's own
// type exactly as it does a user-typed filter — so an option member, a quoted literal and a
// number must come back BYTE-IDENTICAL. A resolver that rewrote those would break the 1215 const
// conditions the Base Application ships that are not object references.
using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// MUST be serial: registering a symbol .app mutates RecordPatches' process-global _bcAppPaths and
// the table index derived from it, and Dispose calls ResetForReload().
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class ObjectReferenceConstResolutionTests : IDisposable
{
    private readonly string _root;

    public ObjectReferenceConstResolutionTests()
    {
        _root = TestScratch.Dir("al-runner-objref-const");
        Directory.CreateDirectory(_root);
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(WriteSymbolApp());
    }

    public void Dispose()
    {
        try { RecordPatches.ResetForReload(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // One .app declaring a table and a report whose NAMES need AL quoting (a space, and a period
    // in "Interaction Tmpl. Language"-style naming), plus a second table so a resolver answering
    // with "the first table it finds" fails, plus a SINGLE-WORD table name that AL does not quote
    // — see the bare-name note in the header. Ids are far from any Base Application id so a hit
    // cannot come from the real packages.
    // no-base-app-in-csharp-tests.md: a bare SymbolReference package, no application floor.
    private string WriteSymbolApp()
    {
        const string SymbolReference = """
        {
          "AppId": "6a4f2f1e-2c53-4b0e-9f1a-0b7d1f9a3c11",
          "Name": "ObjRef Const Fixture",
          "Publisher": "AL Runner",
          "Version": "1.0.0.0",
          "Tables": [
            { "Id": 70931, "Name": "ORC Coupling Row",
              "Fields": [ { "Id": 1, "Name": "Entry No.", "TypeDefinition": { "Name": "Integer" } } ] },
            { "Id": 70932, "Name": "ORC Other Row",
              "Fields": [ { "Id": 1, "Name": "Entry No.", "TypeDefinition": { "Name": "Integer" } } ] },
            { "Id": 70933, "Name": "ORCBareRow",
              "Fields": [ { "Id": 1, "Name": "Entry No.", "TypeDefinition": { "Name": "Integer" } } ] }
          ],
          "Reports": [ { "Id": 70941, "Name": "ORC Merge Report" } ],
          "Codeunits": [ { "Id": 70951, "Name": "ORC Worker" } ],
          "Pages": [],
          "Queries": [],
          "XmlPorts": [],
          "EnumTypes": []
        }
        """;

        var appPath = Path.Combine(_root, "objref-const-fixture.app");
        using var fs = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        using var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    [Theory]
    // Database:: — the reported shape, in both spellings AL emits.
    [InlineData("Database::\"ORC Coupling Row\"", "70931")]
    [InlineData("Database::\"ORC Other Row\"", "70932")]
    // BARE, i.e. no quotes at all, because AL quotes an identifier only when it has to. Seven of
    // the 22 Base Application CalcFormula properties that carry a Database:: const are written
    // this way — Customer, Item, Currency, Vendor, Contact, Opportunity, Resource, re-measured
    // over the shipped BC 28.1 packages for #3207 — and until then no test anywhere, runner-local
    // or corpus, asserted one. ConstValueText strips a
    // matched pair of quotes and passes anything else through, so the quoted and bare spellings
    // of the SAME name must land on the same id; both are asserted, because a resolver that
    // stripped a fixed number of leading characters would satisfy one and not the other.
    [InlineData("Database::ORCBareRow", "70933")]
    [InlineData("Database::\"ORCBareRow\"", "70933")]
    [InlineData("database::ORCBareRow", "70933")]
    // Report:: — the sibling the Base Application ships in 3 TableRelations.
    [InlineData("Report::\"ORC Merge Report\"", "70941")]
    // Codeunit:: — same syntax, resolved through the same object index.
    [InlineData("Codeunit::\"ORC Worker\"", "70951")]
    // The prefix is matched case-insensitively, as AL writes it (`XmlPort::`/`Xmlport::`).
    [InlineData("database::\"ORC Coupling Row\"", "70931")]
    [InlineData("DATABASE::\"ORC Other Row\"", "70932")]
    public void ResolveObjectReferenceConst_NamesADeclaredObject_YieldsItsId(string constText, string expected)
    {
        Assert.Equal(expected, RecordPatches.ResolveObjectReferenceConst(constText));
    }

    [Theory]
    // Everything that is NOT an object reference comes back byte-identical, because
    // NCLMetaFilterConst evaluates it against the source field's own type.
    [InlineData("SPECIAL")]
    [InlineData("On Hold")]
    [InlineData("0")]
    [InlineData("60328")]
    [InlineData("true")]
    [InlineData("")]
    // An enum/option member carries "::" too and must NOT be touched: BC's filter grammar
    // resolves the member by name against the field's own type.
    [InlineData("Some Enum::Member")]
    [InlineData("Sales Document Type::Invoice")]
    public void ResolveObjectReferenceConst_NotAnObjectReference_IsUnchanged(string constText)
    {
        Assert.Equal(constText, RecordPatches.ResolveObjectReferenceConst(constText));
    }

    [Fact]
    public void ResolveObjectReferenceConst_UnknownObjectName_KeepsTheTextAsWritten()
    {
        // loud-failures.md: the honest answer is the text BC's own evaluator will then refuse by
        // name. Answering 0 — or dropping the condition — would pin the filter to the wrong rows
        // and the FlowField would compute a plausible, wrong number in silence.
        Assert.Equal("Database::\"ORC No Such Table\"",
            RecordPatches.ResolveObjectReferenceConst("Database::\"ORC No Such Table\""));

        // And a name declared as a TABLE is not a REPORT: the kind is part of the lookup key.
        Assert.Equal("Report::\"ORC Coupling Row\"",
            RecordPatches.ResolveObjectReferenceConst("Report::\"ORC Coupling Row\""));

        // Both halves again for a BARE name, which is the spelling seven Base Application
        // CalcFormulas actually use: an undeclared one is kept as written rather than answered
        // with 0 or with some other object's id, and the kind still discriminates.
        Assert.Equal("Database::ORCNoSuchBareRow",
            RecordPatches.ResolveObjectReferenceConst("Database::ORCNoSuchBareRow"));
        Assert.Equal("Report::ORCBareRow",
            RecordPatches.ResolveObjectReferenceConst("Report::ORCBareRow"));
    }

    [Fact]
    public void ResolveObjectReferenceConst_ResolvesRepeatedly_WithTheSameAnswer()
    {
        // The successful resolutions are memoised. A cache keyed carelessly (on the name alone,
        // ignoring the kind) would answer the second lookup with the first one's id.
        Assert.Equal("70931", RecordPatches.ResolveObjectReferenceConst("Database::\"ORC Coupling Row\""));
        Assert.Equal("70941", RecordPatches.ResolveObjectReferenceConst("Report::\"ORC Merge Report\""));
        Assert.Equal("70931", RecordPatches.ResolveObjectReferenceConst("Database::\"ORC Coupling Row\""));
        Assert.Equal("70941", RecordPatches.ResolveObjectReferenceConst("Report::\"ORC Merge Report\""));
    }
}
