using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #3614 — the runner reads a tableextension's <c>modify(&lt;field&gt;)</c> property changes out
/// of BC's own emitted delta document rather than re-deriving them from AL syntax.
///
/// <para>These pin the RUNNER-SIDE mechanism: that the delta document is parsed into the right
/// field ids and captions, that a change naming a field the table does not have is reported
/// rather than dropped, and that a malformed document is loud. The BC-behaviour claim itself —
/// that <c>modify(...)</c> changes the caption AL reads back at all, with control arms for an
/// unmodified field and for the field the same extension adds — is adjudicated by a real
/// service tier in corpus PR #304, not here
/// (<c>.claude/rules/bc-behavior-tests-go-upstream.md</c>).</para>
///
/// <para>The XML in these tests is not invented: it is what BC 28.1's emitter produced for a
/// probe tableextension, captured through <c>AL_RUNNER_TRACE_OBJECT_METADATA=2</c>. An
/// <c>ENU=</c>-prefixed <c>CaptionML</c> and a <c>TargetType="MetaField"</c> attribute are BC's
/// shape, not this test's convention.</para>
/// </summary>
public class TableExtensionFieldDeltaTests
{
    private const int ExtensionId = 70901;

    /// <summary>Exactly what BC 28.1 emitted for `modify(Description) { Caption = '...'; }`.</summary>
    private const string RealDeltaXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <MetadataRuntimeDeltas xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" MetadataVersion="130000" ID="70901" Name="Probe Ext" ALNamespace="" xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
          <FieldChange TargetID="2" TargetType="MetaField" CaptionML="ENU=Modified Description" />
        </MetadataRuntimeDeltas>
        """;

    private static void Register(string xml) =>
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcTableExtensionMetadataKind, ExtensionId, "Probe Ext", xml);

    [Fact]
    public void ReadBcFieldChanges_ReadsTheTargetFieldIdAndCaption_FromBcsOwnDocument()
    {
        Register(RealDeltaXml);

        var changes = RecordPatches.ReadBcFieldChanges(ExtensionId);

        var change = Assert.Single(changes);
        Assert.Equal(2, change.TargetFieldId);
        // Unwrapped from CaptionML's `ENU=` form: the derivation's ParsedField.Caption is the
        // plain text, so a reader that passed the raw attribute through would put
        // "ENU=Modified Description" on the field.
        Assert.Equal("Modified Description", change.Caption);
    }

    [Fact]
    public void ReadBcFieldChanges_AnswersEmpty_ForAnExtensionWithNoDocument()
    {
        // The ordinary case, and the one that must not throw: an extension that only ADDS
        // fields emits no FieldChange, and BC folds its added fields into the base table's
        // own document instead.
        Assert.Empty(RecordPatches.ReadBcFieldChanges(int.MaxValue - 3614));
    }

    [Fact]
    public void ReadBcFieldChanges_SkipsAnEntryThatIsNotAboutAField()
    {
        // MetadataRuntimeDeltas is the envelope BC uses for several extension kinds, so a
        // non-MetaField entry is skipped deliberately rather than misread as a field change.
        Register("""
            <MetadataRuntimeDeltas xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
              <FieldChange TargetID="2" TargetType="MetaKey" CaptionML="ENU=Not A Field" />
              <FieldChange TargetID="7" TargetType="MetaField" CaptionML="ENU=A Real One" />
            </MetadataRuntimeDeltas>
            """);

        var change = Assert.Single(RecordPatches.ReadBcFieldChanges(ExtensionId));
        Assert.Equal(7, change.TargetFieldId);
        Assert.Equal("A Real One", change.Caption);
    }

    [Fact]
    public void ReadBcFieldChanges_AnswersNothing_ForAChangeThatCarriesNoCaption()
    {
        // Measured: a design-time-only property (`Description`) makes BC emit a FieldChange
        // with no property attributes at all. That is normal and is NOT a discard to report —
        // there is no runtime property change in it.
        Register("""
            <MetadataRuntimeDeltas xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
              <FieldChange TargetID="2" TargetType="MetaField" />
            </MetadataRuntimeDeltas>
            """);

        Assert.Empty(RecordPatches.ReadBcFieldChanges(ExtensionId));
    }

    [Fact]
    public void ReadBcFieldChanges_ReportsAnUnreadableTargetId_RatherThanDroppingItSilently()
    {
        // The #3614 shape itself: a value that cannot be applied must SAY so. The diagnostic
        // text is asserted, not merely its existence — a drop that logs nothing and a drop
        // that logs at a verbosity nobody runs are the same defect.
        Register("""
            <MetadataRuntimeDeltas xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
              <FieldChange TargetID="not-a-number" TargetType="MetaField" CaptionML="ENU=Lost" />
            </MetadataRuntimeDeltas>
            """);

        var stderr = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(stderr);
            Assert.Empty(RecordPatches.ReadBcFieldChanges(ExtensionId));
        }
        finally { Console.SetError(previous); }

        var written = stderr.ToString();
        Assert.Contains("[TableExtDelta]", written);
        Assert.Contains("no readable TargetID", written);
        Assert.Contains("NOT applied", written);
    }

    [Fact]
    public void ReadBcFieldChanges_ReportsAMalformedDocument_AndAnswersNothing()
    {
        // The document is the compiler's own output, so a malformed one is a runner-side shape
        // gap worth seeing. It must not shrink quietly to "no changes" — that is exactly the
        // silent discard this issue is about, one level up.
        Register("<MetadataRuntimeDeltas><FieldChange TargetID=\"2\"");

        var stderr = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(stderr);
            Assert.Empty(RecordPatches.ReadBcFieldChanges(ExtensionId));
        }
        finally { Console.SetError(previous); }

        var written = stderr.ToString();
        Assert.Contains("[TableExtDelta]", written);
        Assert.Contains("could not be parsed", written);
        Assert.Contains("NOT applied", written);
    }

    [Fact]
    public void ReadBcFieldChanges_ReadsEveryChangeAnExtensionDeclares()
    {
        // One modify(...) block per field produces one FieldChange each. A reader that stopped
        // at the first would apply one field's change and drop the rest — a partial answer,
        // which is worse than none because it looks like it worked.
        Register("""
            <MetadataRuntimeDeltas xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
              <FieldChange TargetID="2" TargetType="MetaField" CaptionML="ENU=First" />
              <FieldChange TargetID="3" TargetType="MetaField" CaptionML="ENU=Second" />
              <FieldChange TargetID="4" TargetType="MetaField" CaptionML="ENU=Third" />
            </MetadataRuntimeDeltas>
            """);

        var changes = RecordPatches.ReadBcFieldChanges(ExtensionId);

        Assert.Equal(3, changes.Count);
        Assert.Equal(new[] { 2, 3, 4 }, changes.Select(c => c.TargetFieldId));
        Assert.Equal(new[] { "First", "Second", "Third" }, changes.Select(c => c.Caption));
    }

    [Fact]
    public void ReadBcFieldChanges_ReadsTheEnuCaption_FromAMultiLanguageAttribute()
    {
        // CaptionML is a `;`-separated list of `<lang>=<text>`. The runner is single-language
        // and every other caption it handles is the ENU one; a reader that took the first
        // entry would answer the wrong language here.
        Register("""
            <MetadataRuntimeDeltas xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
              <FieldChange TargetID="2" TargetType="MetaField" CaptionML="DEU=Deutsch;ENU=English;FRA=Francais" />
            </MetadataRuntimeDeltas>
            """);

        Assert.Equal("English", Assert.Single(RecordPatches.ReadBcFieldChanges(ExtensionId)).Caption);
    }
}
