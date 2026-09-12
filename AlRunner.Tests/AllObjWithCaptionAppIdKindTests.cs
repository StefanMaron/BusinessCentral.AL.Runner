// AllObjWithCaptionAppIdKindTests — issue #3106.
//
// The BC-behaviour claim (AllObjWithCaption's App ID is the owning app's manifest id, empty for
// an enum) is asserted upstream in corpus codeunit 60802 (corpus PR #329). This file pins the
// runner-local kind set RecordPatches.AllObjWithCaptionKindCarriesAppId implements, so a later
// "every kind with an owner gets its App ID" simplification fails here rather than only on the
// enum corpus test. Source of the set: AllObjWithCaptionDataProvider.GetCaptionAndSubtype.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class AllObjWithCaptionAppIdKindTests
{
    [Theory]
    [InlineData("TableData")]
    [InlineData("Table")]
    [InlineData("Report")]
    [InlineData("XMLport")]
    [InlineData("Page")]
    [InlineData("Query")]
    [InlineData("Codeunit")]
    [InlineData("PageExtension")]
    [InlineData("TableExtension")]
    [InlineData("EnumExtension")]
    [InlineData("PermissionSetExtension")]
    [InlineData("ReportExtension")]
    public void KindWithAnOwnerArmInBc_CarriesAppId(string kind)
    {
        Assert.True(RecordPatches.AllObjWithCaptionKindCarriesAppId(kind));
    }

    [Theory]
    [InlineData("Enum")]
    [InlineData("PermissionSet")]
    [InlineData("Profile")]
    [InlineData("ProfileExtension")]
    [InlineData("QueryExtension")]
    [InlineData("System")]
    [InlineData("Interface")]
    public void KindWithNoOwnerArmInBc_HasEmptyAppId(string kind)
    {
        Assert.False(RecordPatches.AllObjWithCaptionKindCarriesAppId(kind));
    }

    [Fact]
    public void KindMatching_IgnoresCaseAndSpacing()
    {
        // The inventory spells kinds as the parsers and symbol files do ("XMLport", "xmlport").
        Assert.True(RecordPatches.AllObjWithCaptionKindCarriesAppId("xmlport"));
        Assert.True(RecordPatches.AllObjWithCaptionKindCarriesAppId("Table Extension"));
    }
}
