// TableMetadataOptionDefaultOrdinalTests — how Table Metadata (2000000136) answers an option
// column for a table that declares NOTHING for it (#3019).
//
// WHY THIS IS A RUNNER-SIDE MECHANISM TEST AND NOT AN AL BUNDLE
// ------------------------------------------------------------
// The BC-behaviour half of #3019 — "a table declaring no DataClassification reports
// CustomerContent" — belongs upstream and is there: corpus fixture ALT Unclassified (60837)
// and Record_TableMetadata_Get_TableDeclaringNoDataClassification_ReportsTheDefault, in
// StefanMaron/BusinessCentral.AL.Language.Tests#191. The assertion RAN, and passed, on the eight
// Cloud legs of run 34026600861 (BC 27.0, 27.3, 27.5, 28.0, 28.1, 28.2, 28.3, 28.4). That run's
// eight OnPrem legs are green as well but build a different app (tests/al-language-onprem,
// codeunits 61200 and 61201) and never execute codeunit 60801, so they say the fixture compiles
// there, not what the column answered. Nothing here re-states that claim.
//
// What it pins instead is the ROUTE, which no AL test can see. Both defaults this file needs
// sit FIRST in their column's option set in every BC artifact measured so far, so the
// undeclared branch returned a hardcoded ordinal 0 and was right by coincidence. That is the
// same shape as the bug #2938 fixed for DECLARED values, and the reason EnsureOptionOrdinals
// reads ordinals out of the artifact instead of writing them down: in BC 28.1's own TableType
// option string, Temporary is at 6 and NOT at the 5 a reading of AL's documented enum
// suggests. An option set that is reordered, extended at the front, or missing the default
// member entirely is indistinguishable from today's through an AL assertion, because AL can
// only see the artifact it is running on.
//
// So the assertions below drive the resolver with option strings the test chooses, including
// reordered ones. Every case is asserted at TWO different ordinals for the same member name,
// which is exactly what a `return 0` cannot satisfy.
//
// WHAT ALREADY COVERS THE REFACTOR ITSELF
// ---------------------------------------
// The rewritten undeclared branch is not first exercised here. Codeunit 60801's
// Record_TableMetadata_Get_DeclaredTable_ReturnsMatchingRow, already in the corpus at the pin
// this repo carries, asserts TableType::Normal for ALT Relation Parent — a fixture that
// declares no TableType — so it drives exactly this branch through a live NCLMetaTable and
// that artifact's real OptionString on every BC leg of this repo's own matrix. The same
// codeunit asserts DataClassification::CustomerContent through the DECLARED path, which is
// what establishes, per leg, that the member this file's default names is present in that
// artifact's option set at all: the precondition the refusal below tests for.
//
// This file adds the cases those cannot reach, because an AL test only ever sees the option
// set of the artifact it is running on.

using System;
using System.Collections.Generic;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class TableMetadataOptionDefaultOrdinalTests
{
    // BC 28.1.49838.53910's own option strings for the two Table Metadata option columns the
    // runner answers, copied verbatim from the artifact's System.app rather than from AL's
    // documented enums — which is the distinction this whole file is about.
    private const string RealDataClassification =
        "CustomerContent,ToBeClassified,EndUserIdentifiableInformation,AccountData,"
        + "EndUserPseudonymousIdentifiers,OrganizationIdentifiableInformation,SystemMetadata";
    private const string RealTableType =
        "Normal,CRM,ExternalSQL,Exchange,MicrosoftGraph,Query,Temporary";

    private static int Resolve(string optionString, string? declaredName, string alDefault, string fieldName = "DataClassification")
        => RecordPatches.ResolveOptionMemberOrdinal(
            RecordPatches.ParseOptionOrdinals(fieldName, optionString),
            fieldName, optionString, declaredName, alDefault, tableId: 60837);

    [Fact]
    public void UndeclaredDataClassification_ResolvesCustomerContentByName_NotOrdinalZero()
    {
        // On the real artifact CustomerContent IS first, so this half is what a hardcoded 0
        // also produced — it pins the answer the service tier measured on corpus PR #191.
        Assert.Equal(0, Resolve(RealDataClassification, declaredName: null, "CustomerContent"));

        // The half a hardcoded 0 cannot produce. Same column, same "declares nothing" input,
        // an option set whose members are in a different order: the answer has to MOVE with
        // CustomerContent, because the member is what the row means and the position is only
        // how this artifact happens to spell it.
        const string Reordered =
            "ToBeClassified,SystemMetadata,AccountData,CustomerContent,"
            + "EndUserIdentifiableInformation,EndUserPseudonymousIdentifiers,"
            + "OrganizationIdentifiableInformation";
        Assert.Equal(3, Resolve(Reordered, declaredName: null, "CustomerContent"));

        // A third position, so the two above cannot be satisfied by any pair of constants.
        Assert.Equal(1, Resolve("ToBeClassified,CustomerContent", declaredName: null, "CustomerContent"));
    }

    [Fact]
    public void UndeclaredTableType_ResolvesNormalByName_NotOrdinalZero()
    {
        // The sibling column, which shares the resolver and had the same hardcoded branch.
        Assert.Equal(0, Resolve(RealTableType, null, "Normal", "TableType"));
        Assert.Equal(2, Resolve("CRM,Temporary,Normal,Query", null, "Normal", "TableType"));
    }

    [Fact]
    public void DeclaredValue_StillResolvesItsOwnOrdinalFromTheArtifact()
    {
        // The declared path, unchanged by #3019 and asserted here so a refactor of the shared
        // resolver cannot break it silently. SystemMetadata is at 6 and ToBeClassified at 1 in
        // the real option string — neither is a value a reading of the docs would predict from
        // position alone.
        Assert.Equal(6, Resolve(RealDataClassification, "SystemMetadata", "CustomerContent"));
        Assert.Equal(1, Resolve(RealDataClassification, "ToBeClassified", "CustomerContent"));

        // The reason ordinals are read rather than written down, restated as an assertion:
        // Temporary is 6 in BC 28.1's TableType set, not the 5 AL's documented enum suggests.
        Assert.Equal(6, Resolve(RealTableType, "Temporary", "Normal", "TableType"));
    }

    [Fact]
    public void UndeclaredValue_DefaultMemberAbsentFromTheOptionSet_IsRefusedNotDefaulted()
    {
        // An artifact whose DataClassification column does not carry CustomerContent at all.
        // Answering 0 there would report ToBeClassified about a table that said nothing — a
        // wrong answer dressed as a default, which is what .claude/rules/loud-failures.md
        // forbids. The refusal has to name the column, the member it looked for and the set it
        // looked in, so the next reader can tell an artifact change from a runner bug.
        const string WithoutDefault = "ToBeClassified,AccountData,SystemMetadata";
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => Resolve(WithoutDefault, declaredName: null, "CustomerContent"));

        Assert.Contains("60837", ex.Message);
        Assert.Contains("declares no DataClassification", ex.Message);
        Assert.Contains("CustomerContent", ex.Message);
        Assert.Contains(WithoutDefault, ex.Message);
        // Category (2) of RecordPatches.VirtualTableShapeGap.cs: in scope, the runner cannot
        // answer yet. The anchor is load-bearing — IsPermanentOutOfScope reads it to decide
        // whether an AL [TryFunction] traps this into `false` or it tears through.
        Assert.StartsWith("not-yet-implemented", ex.Reason);
    }

    [Fact]
    public void DeclaredValue_NotAMemberOfTheOptionSet_IsRefusedNotDefaulted()
    {
        // The #2938 refusal, still in place: a declared member the column does not list is a
        // shape mismatch, never ordinal 0. Asserted alongside the new one so the two refusals
        // stay distinguishable in the message a reader gets.
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => Resolve(RealDataClassification, "NotAMember", "CustomerContent"));

        Assert.Contains("declares DataClassification = 'NotAMember'", ex.Message);
        Assert.Contains("not a member of that column's own option set", ex.Message);
    }

    [Fact]
    public void ParseOptionOrdinals_EmptyOptionString_IsRefused()
    {
        // The precondition behind every lookup above. An empty option string would otherwise
        // make the map empty, and an empty map turns every resolution into the refusal path —
        // which would read as "this artifact renamed a member" rather than "this column has no
        // option metadata at all".
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.ParseOptionOrdinals("DataClassification", ""));
        Assert.Contains("option string is empty", ex.Message);
    }
}
