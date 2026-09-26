using System.Collections;
using System.Reflection;
using System.Xml;
using AlRunner;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4640 — every place the runner builds or reads a one-language (ENU) caption goes through BC's
/// own <c>MultiLanguage</c>, so a text containing <c>;</c>, <c>=</c> or a leading <c>"</c> survives.
/// BC's parser ends an unquoted value at <c>;</c> and reads a leading <c>"</c> as a quoted value;
/// BC's serializer quotes exactly those texts.
///
/// <para>The AL-observable half (TableCaption, FieldCaption, a <c>modify()</c>d caption) is adjudicated
/// on a real service tier by the corpus PR in this change's body. These pin the runner-side sites a
/// corpus test cannot reach: the precompiled-query column caption, the two metadata-equivalence
/// renders, and the two readers of BC-emitted <c>CaptionML</c>.</para>
/// </summary>
[Collection("object-metadata-registry")]
public sealed class EnuMultiLanguageTextTests : IDisposable
{
    public EnuMultiLanguageTextTests() => AlObjectMetadataRegistry.Clear();
    public void Dispose() => AlObjectMetadataRegistry.Clear();

    private const string Semicolon = "Before; after";
    private const string QuotedStart = "\"Quoted\" start";

    [Fact]
    public void ToMultiLanguageString_QuotesASemicolonAndAQuote_AndLeavesPlainTextBare()
    {
        Assert.Equal("ENU=\"Before; after\"", EnuMultiLanguageText.ToMultiLanguageString(Semicolon));
        Assert.Equal("ENU=\"\"\"Quoted\"\" start\"", EnuMultiLanguageText.ToMultiLanguageString(QuotedStart));
        // Negative direction: text carrying no ML syntax is written exactly as BC writes it.
        Assert.Equal("ENU=Plain caption", EnuMultiLanguageText.ToMultiLanguageString("Plain caption"));
    }

    [Theory]
    [InlineData(Semicolon)]
    [InlineData(QuotedStart)]
    [InlineData("Rate = 5%")]
    public void ToMultiLanguageString_ParsesBackThroughBcToTheWholeText(string text)
    {
        var parsed = MultiLanguage.Parse(EnuMultiLanguageText.ToMultiLanguageString(text));
        Assert.Equal(new[] { 1033 }, parsed.LanguageIds);
        Assert.Equal(new[] { text }, parsed.Texts);
    }

    [Fact]
    public void From_HoldsTheWholeTextAsTheOnlyEnuEntry()
    {
        var ml = EnuMultiLanguageText.From(Semicolon);
        Assert.Equal(new[] { 1033 }, ml.LanguageIds);
        Assert.Equal(new[] { Semicolon }, ml.Texts);
    }

    [Theory]
    [InlineData("ENU=\"Changed; by \"\"extension\"\"\"", false, "Changed; by \"extension\"")]
    [InlineData("DEU=\"x;y\";ENU=\"a; b\"", false, "a; b")]
    [InlineData("ENU=Plain", false, "Plain")]
    [InlineData("DEU=\"Nur; deutsch\"", true, "Nur; deutsch")]
    [InlineData("DEU=\"Nur; deutsch\"", false, null)]
    public void ReadEnu_ReadsBcsQuotedFormWhole(string ml, bool firstIfNoEnu, string? expected)
        => Assert.Equal(expected, EnuMultiLanguageText.ReadEnu(ml, firstIfNoEnu));

    /// <summary>The <c>modify()</c> delta reader: BC writes a caption with <c>;</c> quoted.</summary>
    [Fact]
    public void ReadBcFieldChanges_ReadsAQuotedCaptionWhole()
    {
        const int extensionId = 70946;
        AlObjectMetadataRegistry.Register(RecordPatches.BcTableExtensionMetadataKind, extensionId, "Quote Ext",
            """
            <MetadataRuntimeDeltas ID="70946" Name="Quote Ext" xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
              <FieldChange TargetID="4" TargetType="MetaField" CaptionML="ENU=&quot;Changed; by &quot;&quot;extension&quot;&quot;&quot;" />
            </MetadataRuntimeDeltas>
            """);

        var change = Assert.Single(RecordPatches.ReadBcFieldChanges(extensionId));
        Assert.Equal(4, change.TargetFieldId);
        Assert.Equal("Changed; by \"extension\"", change.Caption);
    }

    /// <summary>The permission-set document reader: same quoted form, same whole text.</summary>
    [Fact]
    public void TryReadPermissionSetFromBcDocument_ReadsAQuotedCaptionWhole()
    {
        const int id = 77464001;
        AlObjectMetadataRegistry.Register(RecordPatches.BcPermissionSetMetadataKind, id, "PSD Quoted",
            $"""
             <PermissionSet ID="{id}" Name="PSD Quoted" Assignable="1" CaptionML="ENU=&quot;Sales; read only&quot;"
                            xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
               <Permissions><Permission Type="0" ID="70700" Value="1" /></Permissions>
             </PermissionSet>
             """);

        var set = RecordPatches.TryReadPermissionSetFromBcDocument(id, "PSD Quoted");
        Assert.NotNull(set);
        Assert.Equal("Sales; read only", set!.Caption);
    }

    /// <summary>
    /// The precompiled-query column caption (<c>NclMetaQueryBuilder.AddColumn</c>): a source-compiled
    /// query takes BC's own document instead, so no corpus query reaches this site.
    /// </summary>
    [Fact]
    public void QueryColumnCaption_HoldsTheWholeCaption()
    {
        RecordPatches.EnsureQueryBuilderReflection();
        var dataItemType = typeof(MultiLanguage).Assembly.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaQueryDataItem")!;
        var dataItem = Activator.CreateInstance(dataItemType)!;

        RecordPatches.AddColumn(dataItem, 1, "EntryNo", 1, 0, caption: "Column; with semicolon");

        var columns = (IList)dataItemType.GetProperty("QueryColumns")!.GetValue(dataItem)!;
        var column = Assert.Single(columns.Cast<object>());
        var ml = (MultiLanguage)column!.GetType().GetProperty("CaptionML", BindingFlags.Public | BindingFlags.Instance)!.GetValue(column)!;
        Assert.Equal(new[] { 1033 }, ml.LanguageIds);
        Assert.Equal(new[] { "Column; with semicolon" }, ml.Texts);
    }
}
