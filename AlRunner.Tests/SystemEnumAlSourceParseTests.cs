// SystemEnumAlSourceParseTests — issue #3594.
//
// BC's 16 platform ("system") enums are declared by the platform, not by any app, so they
// appear in no SymbolReference.json and nothing put them in AlEnumMetadataRegistry. A table
// field typed by one — Base Application table 2000000132 has such a field, enum 2000000002
// "Entity Text Scenario" — therefore could not resolve its option metadata.
//
// BcRuntime.EnsureSystemEnumsRegistered reads them from BC's OWN inventory
// (PlatformMetadataProvider.GetSystemEnums / GetEnumALCodeById, which returns Microsoft's AL
// source for the enum) and parses the value declarations out of it. This test pins that PARSE
// — the one piece of the fix that is the runner's own logic rather than a lookup redirected at
// BC's data — against the exact shapes BC's own source uses. Measured on BC 28.1: 16 enums,
// 57 value(...) declarations, 27 of them with a "quoted" name, 73 captions, and three enums
// declaring no values at all.
//
// The BC-behaviour claim (what Field.OptionString / EnumTypeId answer for an enum-typed field)
// is proven upstream against a live service tier; see the Corpus-PR on #3594.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class SystemEnumAlSourceParseTests
{
    // POSITIVE — the ordinary shape: bare names, explicit ordinals, per-value captions.
    // Asserted by value, so a parser returning empties or defaults fails.
    [Fact]
    public void BareNames_ParseWithOrdinalsAndCaptions()
    {
        var (options, ordinals, captions) = BcRuntime.ParseSystemEnumValues(@"
enum 2000000004 ""Agent Task Status""
{
    Extensible = false;
    Caption = 'Agent Task Status';

    value(0; Paused) { Caption = 'Paused'; }
    value(1; Ready) { Caption = 'Ready'; }
    value(2; Scheduled) { Caption = 'Scheduled'; }
}");

        Assert.Equal(new[] { "Paused", "Ready", "Scheduled" }, options);
        Assert.Equal(new[] { 0, 1, 2 }, ordinals);
        Assert.Equal(new string?[] { "Paused", "Ready", "Scheduled" }, captions);
    }

    // POSITIVE — quoted names, which are 27 of BC's own 57 declarations. The quotes are part
    // of the syntax, not of the name, so they must not survive into the option string.
    [Fact]
    public void QuotedNames_LoseTheQuotesAndKeepTheSpaces()
    {
        var (options, ordinals, _) = BcRuntime.ParseSystemEnumValues(@"
enum 2000000011 ""Agent User Int Request Type""
{
    value(0; ""Input Message"") { Caption = 'Input Message'; }
    value(1; ""Output Message Draft"") { Caption = 'Output Message Draft'; }
    value(2; ""Page Operation"") { Caption = 'Page Operation'; }
}");

        Assert.Equal(new[] { "Input Message", "Output Message Draft", "Page Operation" }, options);
        Assert.Equal(new[] { 0, 1, 2 }, ordinals);
    }

    // POSITIVE — sparse and out-of-order ordinals are carried as declared, never as a
    // 0..Count-1 array index. This is the same property AlEnumOptionMetadata exists to keep.
    [Fact]
    public void SparseOrdinals_AreCarriedAsDeclared()
    {
        var (options, ordinals, _) = BcRuntime.ParseSystemEnumValues(@"
enum 2000000099 ""Sparse""
{
    value(0; Zero) { }
    value(5; Five) { }
    value(10; Ten) { }
}");

        Assert.Equal(new[] { "Zero", "Five", "Ten" }, options);
        Assert.Equal(new[] { 0, 5, 10 }, ordinals);
    }

    // POSITIVE — a value with no Caption block records null, meaning "declares none", which is
    // the convention AlEnumMetadataRegistry.Register and AlEnumOptionMetadata already use: the
    // consumer then applies AL's own default (the member name). An empty string here would be
    // a caption BC never declared.
    [Fact]
    public void ValueWithoutCaption_RecordsNullNotEmptyString()
    {
        var (options, _, captions) = BcRuntime.ParseSystemEnumValues(@"
enum 2000000098 ""Mixed""
{
    value(0; WithCaption) { Caption = 'Has One'; }
    value(1; NoBody)
    value(2; EmptyBody) { }
}");

        Assert.Equal(new[] { "WithCaption", "NoBody", "EmptyBody" }, options);
        Assert.Equal(new string?[] { "Has One", null, null }, captions);
    }

    // NEGATIVE — three of BC's sixteen system enums declare no values at all (they are
    // extensible enums an app is expected to extend). Empty is the CORRECT answer there, and
    // must not be confused with a parse failure: enum 2000000002 is one of them, and it is the
    // very enum whose absence aborted the corpus.
    [Fact]
    public void EnumDeclaringNoValues_ParsesToEmpty_NotAFailure()
    {
        var (options, ordinals, captions) = BcRuntime.ParseSystemEnumValues(@"
namespace System.Text;

enum 2000000002 ""Entity Text Scenario""
{
    Extensible = true;
    Caption = 'Entity Text Scenario';
}");

        Assert.Empty(options);
        Assert.Empty(ordinals);
        Assert.Empty(captions);
    }

    // NEGATIVE — the word "value" inside a doc comment must not be read as a declaration. BC's
    // own sources are heavily doc-commented (73 captions across 16 enums, most preceded by a
    // /// block), so a parser that skipped comment stripping would invent members here.
    [Fact]
    public void DocCommentsMentioningValue_DoNotBecomeMembers()
    {
        var (options, ordinals, _) = BcRuntime.ParseSystemEnumValues(@"
enum 2000000097 ""Commented""
{
    /// <summary>
    /// This mentions value(99; Fake) inside prose and must be ignored.
    /// </summary>
    value(0; Real) { Caption = 'Real'; }

    // value(98; AlsoFake)
    /* value(97; BlockFake) */
}");

        Assert.Equal(new[] { "Real" }, options);
        Assert.Equal(new[] { 0 }, ordinals);
    }

    // NEGATIVE — a caption containing a comment marker survives, because comment stripping must
    // not run inside a string literal. Opposite pair to the test above: one proves comments are
    // removed, this one proves the removal stops at a quote.
    [Fact]
    public void CommentMarkerInsideACaption_Survives()
    {
        var (_, _, captions) = BcRuntime.ParseSystemEnumValues(@"
enum 2000000096 ""Slashes""
{
    value(0; Ratio) { Caption = 'in/out // both'; }
}");

        Assert.Equal(new string?[] { "in/out // both" }, captions);
    }
}
