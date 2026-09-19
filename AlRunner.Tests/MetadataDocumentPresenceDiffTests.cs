// MetadataDocumentPresenceDiffTests — issue #4357.
//
// The claim under test is awkward to state and easy to fake, so every test here is built the
// same way: a document pair where BC states an attribute, the runner omits it, and the PARSED
// objects are EQUAL. Each test asserts that equality first — `MetadataObjectDiff.Compare`
// returns zero — because without it the test would pass against a mechanism that merely
// re-reports value differences, which the harness already reports and which would prove nothing
// about the blind spot (.claude/rules/tdd.md, "a test that names the thing is not a test that
// drives it").
//
// The fixture models BC's real shape rather than a convenient one. `PageLike.AnalysisModeEnabled`
// defaults to TRUE when the attribute is absent, which is what BC's own PageDefinition reader
// does on 28.1.49838.53910 — measured: page 8350 states "0" and is the only one of the 94 pages
// stating the attribute that the object diff reports.

using System.Xml;
using AlRunner.Metadata;
using Xunit;

namespace AlRunner.Tests;

public sealed class MetadataDocumentPresenceDiffTests
{
    // ---- the fixture: a parse whose absent-attribute defaults are BC-shaped ----------------

    private sealed class PageLike
    {
        /// <summary>A bool with NO Specified companion, defaulting to true when absent — the
        /// exact shape of BC's PageProperties.AnalysisModeEnabled.</summary>
        public bool AnalysisModeEnabled { get; init; } = true;

        /// <summary>A bool WITH a Specified companion, the shape of CardFormIDSpecified. Its
        /// presence is observable through the companion, so it must not be reported.</summary>
        public int CardFormID { get; init; }
        public bool CardFormIDSpecified { get; init; }

        /// <summary>A string, defaulting to null — never coincides with a written value.</summary>
        public string? QueryCategory { get; init; }
    }

    private static object? ParsePageLike(XmlDocument document)
    {
        var root = document.DocumentElement;
        if (root is null) return null;
        var props = root.ChildNodes.OfType<XmlElement>()
            .FirstOrDefault(e => e.LocalName == "Properties");
        if (props is null) return new PageLike();

        var analysis = props.GetAttribute("AnalysisModeEnabled");
        var card = props.GetAttribute("CardFormID");
        var query = props.HasAttribute("QueryCategory") ? props.GetAttribute("QueryCategory") : null;
        return new PageLike
        {
            AnalysisModeEnabled = analysis.Length == 0 || analysis == "1",
            CardFormID = card.Length == 0 ? 0 : int.Parse(card),
            CardFormIDSpecified = card.Length != 0,
            QueryCategory = query,
        };
    }

    private static XmlDocument Doc(string properties)
    {
        var d = new XmlDocument();
        d.LoadXml($"<PageDefinition ID=\"8350\"><Properties {properties}/></PageDefinition>");
        return d;
    }

    private static IReadOnlyList<MetadataDifference> ValueDiff(XmlDocument bc, XmlDocument runner)
        => MetadataObjectDiff.Compare(ParsePageLike(bc), ParsePageLike(runner), "Page 8350");

    private static IReadOnlyList<MetadataUnobservableOmission> Presence(XmlDocument bc, XmlDocument runner)
        => MetadataDocumentPresenceDiff.Compare(bc, runner, ParsePageLike, "Page 8350");

    // ---- the defect: BC states it, the runner omits it, the parsed values are EQUAL --------

    [Fact]
    public void An_omission_the_value_comparison_cannot_see_is_reported()
    {
        var bc = Doc("AnalysisModeEnabled=\"1\"");
        var runner = Doc("");

        // The premise. If this ever reports anything the test below is measuring the ordinary
        // value path and says nothing about the blind spot.
        Assert.Empty(ValueDiff(bc, runner));

        var reported = Assert.Single(Presence(bc, runner));
        Assert.Equal("Properties.AnalysisModeEnabled", reported.Signature);
        Assert.Equal("1", reported.Value);
        Assert.Equal(MetadataDocumentPresenceDiff.ExpectedSide, reported.StatedBy);
        Assert.Equal("/Properties[0]", reported.ElementPath);
    }

    [Fact]
    public void The_same_omission_in_the_other_direction_is_reported_against_the_runner()
    {
        var bc = Doc("");
        var runner = Doc("AnalysisModeEnabled=\"1\"");

        Assert.Empty(ValueDiff(bc, runner));

        var reported = Assert.Single(Presence(bc, runner));
        Assert.Equal("Properties.AnalysisModeEnabled", reported.Signature);
        Assert.Equal(MetadataDocumentPresenceDiff.ActualSide, reported.StatedBy);
    }

    // ---- the discrimination: what must NOT be reported --------------------------------------

    [Fact]
    public void An_omission_the_value_comparison_already_reports_is_not_reported_again()
    {
        // BC says "0"; the runner's absent-parse default is true. This is page 8350 — the one
        // of the 94 the harness can already see. A mechanism that reported every omission
        // would report this one too, duplicating a failure the harness already produces.
        var bc = Doc("AnalysisModeEnabled=\"0\"");
        var runner = Doc("");

        var values = ValueDiff(bc, runner);
        Assert.Equal("AnalysisModeEnabled", Assert.Single(values).Member);

        Assert.Empty(Presence(bc, runner));
    }

    [Fact]
    public void An_omission_BCs_own_Specified_companion_exposes_is_not_reported()
    {
        // CardFormID="0" parses to 0, which is also the absent default — but the companion flag
        // moves, so the object diff sees it. This is the arm that keeps the mechanism from
        // claiming credit for presence BC already models.
        var bc = Doc("CardFormID=\"0\"");
        var runner = Doc("");

        Assert.Equal("CardFormIDSpecified", Assert.Single(ValueDiff(bc, runner)).Member);
        Assert.Empty(Presence(bc, runner));
    }

    [Fact]
    public void A_string_attribute_whose_absence_parses_to_null_is_not_reported()
    {
        var bc = Doc("QueryCategory=\"Lists\"");
        var runner = Doc("");

        Assert.Equal("QueryCategory", Assert.Single(ValueDiff(bc, runner)).Member);
        Assert.Empty(Presence(bc, runner));
    }

    [Fact]
    public void Two_documents_stating_the_same_attributes_report_nothing()
    {
        var bc = Doc("AnalysisModeEnabled=\"1\" QueryCategory=\"Lists\"");
        var runner = Doc("AnalysisModeEnabled=\"1\" QueryCategory=\"Lists\"");

        Assert.Empty(ValueDiff(bc, runner));
        Assert.Empty(Presence(bc, runner));
    }

    // ---- scoping: an element only one side builds is already a <presence> difference --------

    [Fact]
    public void Attributes_on_an_element_the_other_side_does_not_build_are_not_reported()
    {
        // The runner reconstructs only a page's part controls, so BC's ordinary field controls
        // are absent from its document entirely. Reporting every attribute on every such
        // element is what took the measured page population from 3,066 rows to 80,864.
        var bc = new XmlDocument();
        bc.LoadXml("<PageDefinition ID=\"8350\"><Properties AnalysisModeEnabled=\"1\"/>" +
                   "<Controls><Control Name=\"Only in BC\" Editable=\"1\"/></Controls></PageDefinition>");
        var runner = new XmlDocument();
        runner.LoadXml("<PageDefinition ID=\"8350\"><Properties/></PageDefinition>");

        var reported = Presence(bc, runner);
        Assert.Equal(new[] { "Properties.AnalysisModeEnabled" },
            reported.Select(r => r.Signature).ToArray());
    }

    // ---- the third state: a reader that cannot answer must not read as "nothing to report" --

    [Fact]
    public void A_reader_that_returns_null_for_the_whole_document_is_reported_not_swallowed()
    {
        var bc = Doc("AnalysisModeEnabled=\"1\"");
        var runner = Doc("");

        var reported = MetadataDocumentPresenceDiff.Compare(bc, runner, _ => null, "Page 8350");

        // Once per side, each naming the side that could not be read — never an empty list,
        // which is what "every omission is observable" also looks like.
        Assert.Equal(2, reported.Count);
        Assert.All(reported, r => Assert.Equal("<unreadable>", r.Attribute));
        Assert.Contains(reported, r => r.StatedBy.StartsWith(
            MetadataDocumentPresenceDiff.ExpectedSide, StringComparison.Ordinal));
        Assert.Contains(reported, r => r.StatedBy.StartsWith(
            MetadataDocumentPresenceDiff.ActualSide, StringComparison.Ordinal));
    }

    [Fact]
    public void A_reader_that_answers_the_document_but_refuses_a_strip_is_reported_as_unmeasured()
    {
        var bc = Doc("AnalysisModeEnabled=\"1\"");
        var runner = Doc("");

        // Answers any document that states the attribute, and refuses one that does not — so
        // BC's own document parses and the STRIPPED copy of it does not.
        object? Refuse(XmlDocument d)
            => d.DocumentElement!.ChildNodes.OfType<XmlElement>()
                   .First(e => e.LocalName == "Properties")
                   .HasAttribute("AnalysisModeEnabled")
                ? ParsePageLike(d)
                : null;

        var reported = MetadataDocumentPresenceDiff.Compare(bc, runner, Refuse, "Page 8350");

        // The BC side read its own document and then could not read the strip, so the verdict
        // for that ONE attribute is "not measured" — carrying the attribute's own name, not the
        // whole-document refusal.
        var strip = Assert.Single(reported, r => r.Attribute == "AnalysisModeEnabled");
        Assert.Equal("1", strip.Value);
        Assert.Contains("not measured", strip.StatedBy, StringComparison.Ordinal);

        // The runner's document states nothing, so this reader refuses it outright: a whole
        // document that could not be read, reported separately and never as silence.
        var whole = Assert.Single(reported, r => r.Attribute == "<unreadable>");
        Assert.StartsWith(MetadataDocumentPresenceDiff.ActualSide, whole.StatedBy, StringComparison.Ordinal);
    }

    // ---- the walk WARMS what it reads, so a reused baseline answers by walk order ----------

    /// <summary>
    /// A member that reads false on a cold instance and true on a warmed one — the shape of
    /// <c>LazyEx&lt;T&gt;.IsValueCreated</c>, which BC's MetaReport exposes and which
    /// <see cref="MetadataObjectDiff"/> FORCES simply by reading every readable member.
    /// </summary>
    private sealed class Latching
    {
        private bool _read;
        public bool Warmed { get { var before = _read; _read = true; return before; } }
        public string Attributes { get; init; } = "";
    }

    [Fact]
    public void A_second_unobservable_omission_on_one_document_is_still_reported()
    {
        // Two attributes, both genuinely unobservable: this parse ignores them entirely, so
        // stripping either changes nothing. A mechanism that parsed ONE baseline and reused it
        // would report the first and miss the second, because the first comparison warms the
        // baseline and the second then differs on Warmed alone — a difference about the
        // harness, reported as though it were about the derivation.
        var bc = Doc("AnalysisModeEnabled=\"1\" QueryCategory=\"Lists\"");
        var runner = Doc("");

        static object? ParseIgnoringAttributes(XmlDocument _) => new Latching();

        var reported = MetadataDocumentPresenceDiff.Compare(
            bc, runner, ParseIgnoringAttributes, "Page 8350");

        Assert.Equal(
            new[] { "Properties.AnalysisModeEnabled", "Properties.QueryCategory" },
            reported.Select(r => r.Signature).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    // ---- the clone must not write through to the caller's document --------------------------

    [Fact]
    public void The_documents_handed_in_are_not_modified()
    {
        var bc = Doc("AnalysisModeEnabled=\"1\"");
        var runner = Doc("");
        var before = bc.OuterXml;

        Presence(bc, runner);

        Assert.Equal(before, bc.OuterXml);
    }
}
