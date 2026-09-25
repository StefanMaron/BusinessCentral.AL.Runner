// CodeunitSubscriberMethodTableTests — the codeunit <Methods> subtree for codeunits that declare
// event subscribers, derived from the app's own R2R assembly and checked against BC's own
// emitter output for the same build (#3788). docs/codeunit-metadata-from-bc.md#subscribers-from-the-assembly.

using System.Xml;
using AlRunner.Patches;
using Xunit;
using Xunit.Abstractions;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CodeunitSubscriberMethodTableTests
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    private readonly BcEngineFixture _engine;
    private readonly ITestOutputHelper _output;

    public CodeunitSubscriberMethodTableTests(BcEngineFixture engine, ITestOutputHelper output)
    {
        _engine = engine;
        _output = output;
    }

    private sealed record Codeunit(GroundTruthBundle Bundle, int Id, string BcDocumentPath);

    /// <summary>Every ground-truth codeunit on this box, with its app registered.</summary>
    private IReadOnlyList<Codeunit> RegisteredCodeunits()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        var result = new List<Codeunit>();
        foreach (var bundle in MetadataEquivalenceBundleGate.RequireBundles())
        {
            var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
            Assert.True(app is not null, $"ground truth exists for {bundle.Label} but its .app is not on this box");
            RecordPatches.AddBcAppPath(app!);
            foreach (var o in bundle.Objects.Where(o => o.Kind == "CodeUnit"))
                result.Add(new Codeunit(bundle, o.Id, Path.Combine(bundle.Directory, o.File)));
        }
        return result;
    }

    private static XmlElement? MethodsOf(XmlDocument doc)
        => doc.DocumentElement!.GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>().FirstOrDefault();

    private static XmlDocument Load(string path) { var d = new XmlDocument(); d.Load(path); return d; }

    private static XmlDocument? RunnerDocument(int id)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(id);
        if (xml is null) return null;
        var d = new XmlDocument();
        d.LoadXml(xml);
        return d;
    }

    /// <summary>Element names and attributes, attribute order ignored — the reader's view.</summary>
    private static string Canonical(XmlElement e)
        => e.LocalName + "{" + string.Join(" ", e.Attributes.OfType<XmlAttribute>()
               .Where(a => a.Prefix != "xmlns" && a.Name != "xmlns")
               .OrderBy(a => a.LocalName, StringComparer.Ordinal).Select(a => a.LocalName + "=" + a.Value)) + "}"
           + string.Concat(e.ChildNodes.OfType<XmlElement>().Select(Canonical));

    private static bool HasSubscriber(XmlElement methods)
        => methods.GetElementsByTagName("EventSubscriberAttribute", MetaNs).Count > 0;

    /// <summary>
    /// The population claim: every codeunit that declares a subscriber and for which the runner
    /// now emits a subtree emits BC's subtree EXACTLY — ids, names, order, all seven subscriber
    /// attributes and every parameter. A wrong slot anywhere is a fabricated association, so the
    /// count of inexact subtrees must be zero, and at least one must be emitted or the test
    /// measured nothing.
    /// </summary>
    [SkippableFact]
    public void Every_emitted_subscriber_subtree_is_BCs_exactly()
    {
        int emitted = 0, withSubscriber = 0;
        var inexact = new List<string>();
        foreach (var cu in RegisteredCodeunits())
        {
            var bcMethods = MethodsOf(Load(cu.BcDocumentPath));
            if (bcMethods is null || !HasSubscriber(bcMethods)) continue;
            withSubscriber++;
            var mine = RunnerDocument(cu.Id) is { } doc ? MethodsOf(doc) : null;
            if (mine is null) continue;
            emitted++;
            if (Canonical(mine) != Canonical(bcMethods))
                inexact.Add($"{cu.Bundle.AppName} codeunit {cu.Id}:\n  BC     {Canonical(bcMethods)}\n  runner {Canonical(mine)}");
        }

        _output.WriteLine($"subscriber codeunits={withSubscriber} emitted={emitted} inexact={inexact.Count}");
        Assert.True(withSubscriber > 0, "no ground-truth codeunit declares a subscriber — nothing was measured");
        Assert.True(emitted > 0, $"none of the {withSubscriber} subscriber codeunits emitted a <Methods> subtree");
        Assert.True(inexact.Count == 0,
            $"{inexact.Count} of {emitted} emitted subscriber subtrees differ from BC's:\n" + string.Join("\n", inexact.Take(5)));
    }

    /// <summary>
    /// Order is source order, not the assembly's alphabetical metadata order: codeunit 3902
    /// "Retention Policy Setup" declares nine subscribers, and alphabetical order would put
    /// AddRetentionPolicyOnRegisterManualSetup first and ErrorOnBeforeRename… second.
    /// </summary>
    [SkippableFact]
    public void Subscribers_are_placed_in_source_order_not_metadata_order()
    {
        RegisteredCodeunits();
        var methods = MethodsOf(RunnerDocument(3902) ?? throw new Xunit.Sdk.XunitException("codeunit 3902 not known"));
        Assert.NotNull(methods);
        Assert.Equal(
            new[]
            {
                "AddRetentionPolicyOnRegisterManualSetup",
                "VerifyRetentionPolicySetupOnbeforeDeleteRetentionPeriod",
                "VerifyRetentionPolicySetupOnbeforeModifyRetentionPeriod",
                "VerifyRetentionPolicyAllowedTablesOnBeforeInsertRetenPolSetup",
                "InsertDefaultTableFiltersOnAfterInsertRetenPolSetup",
                "ErrorOnBeforeRenameRetentionPolicySetup",
                "ErrorOnBeforeRenameRetentionPolicySetupLine",
                "CheckRecordLockedOnRetentionPolicySetupLineOnAfterModify",
                "CheckRecordLockedOnRetentionPolicySetupLineOnAfterDelete",
            },
            methods!.ChildNodes.OfType<XmlElement>().Select(m => m.GetAttribute("Name")).ToArray());
    }

    /// <summary>
    /// Publishers (from the symbol file) and subscribers (from the assembly) interleave by source
    /// line: codeunit 3906 declares publisher, subscriber, publisher, subscriber.
    /// </summary>
    [SkippableFact]
    public void Publishers_and_subscribers_interleave_by_source_line()
    {
        RegisteredCodeunits();
        var methods = MethodsOf(RunnerDocument(3906) ?? throw new Xunit.Sdk.XunitException("codeunit 3906 not known"));
        Assert.NotNull(methods);
        Assert.Equal(
            new[]
            {
                ("OnVerifyAddtoAllowedList", "EventPublisherAttribute"),
                ("AllowAddtoAllowedList", "EventSubscriberAttribute"),
                ("OnVerifyModifyAllowedList", "EventPublisherAttribute"),
                ("AllowModifyAllowedList", "EventSubscriberAttribute"),
            },
            methods!.ChildNodes.OfType<XmlElement>().Select(m => (m.GetAttribute("Name"),
                m.GetElementsByTagName("MethodAttributes", MetaNs).OfType<XmlElement>().Single()
                    .ChildNodes.OfType<XmlElement>().Single().LocalName)).ToArray());
    }

    /// <summary>
    /// An upgrade codeunit's trigger compiles with [NavEventSubscriber] but BC does not emit it.
    /// Codeunit 9028 carries the trigger OnUpgradePerCompany and one real subscriber.
    /// </summary>
    [SkippableFact]
    public void An_install_or_upgrade_trigger_is_not_emitted_as_a_subscriber()
    {
        RegisteredCodeunits();
        var methods = MethodsOf(RunnerDocument(9028) ?? throw new Xunit.Sdk.XunitException("codeunit 9028 not known"));
        Assert.NotNull(methods);
        var names = methods!.ChildNodes.OfType<XmlElement>().Select(m => m.GetAttribute("Name")).ToArray();
        Assert.Contains("AddUpgradeTag", names);
        Assert.DoesNotContain("OnUpgradePerCompany", names);
    }

    /// <summary>
    /// Refusals keep the honest absence. Codeunits 55 and 58 have a subscriber with a <c>var</c>
    /// Text parameter: BC writes <c>Length</c> for 55's <c>var Translation: Text[1024]</c> and none
    /// for 58's unbounded ones, and neither the signature nor the method's parameter copy says
    /// which, because a <c>var</c> parameter is stored without a <c>ModifyLength</c> (#4601).
    /// </summary>
    [SkippableFact]
    public void A_subtree_the_runner_cannot_state_exactly_is_withheld()
    {
        var codeunits = RegisteredCodeunits();
        foreach (var id in new[] { 55, 58 })
        {
            Assert.Contains(codeunits, c => c.Id == id);
            var doc = RunnerDocument(id);
            Assert.NotNull(doc);
            Assert.Null(MethodsOf(doc!));
        }
    }

    /// <summary>
    /// The three shapes #4601 recorded as underivable, each now emitted exactly as BC writes it:
    /// 1482's by-value <c>Text[240]</c> writes <c>Length="240"</c> beside an unbounded Text that
    /// writes none (the length is the <c>ModifyLength(N)</c> the method copies it through, 0 when
    /// unbounded); 3920 and 8903's <c>var</c> Interface writes <c>IsVar="True"</c> (stored as-is,
    /// where a by-value one goes through <c>ALByValue</c>); and the <c>local</c>
    /// <c>[InherentPermissions]</c> methods of 306, 307, 309 and 8705, which no symbol file states,
    /// are read off the attribute's four values.
    /// </summary>
    [SkippableTheory]
    [InlineData(1482)]
    [InlineData(1565)]
    [InlineData(3920)]
    [InlineData(8903)]
    [InlineData(306)]
    [InlineData(309)]
    [InlineData(8705)]
    public void A_shape_the_method_body_states_is_emitted_exactly(int id)
    {
        var cu = RegisteredCodeunits().FirstOrDefault(c => c.Id == id);
        Assert.NotNull(cu);
        var bc = MethodsOf(Load(cu!.BcDocumentPath));
        Assert.NotNull(bc);
        var mine = RunnerDocument(id) is { } doc ? MethodsOf(doc) : null;
        Assert.True(mine is not null, $"codeunit {id}: the runner withheld its <Methods> subtree: "
            + RecordPatches.CodeunitMethodTableRefusalForTests(
                MetadataEquivalenceHarness.FindAppPackage(cu.Bundle)!, id, null));
        Assert.Equal(Canonical(bc!), Canonical(mine!));
    }

    /// <summary>
    /// The population claim over EVERY codeunit, subscriber or not: each emitted subtree is BC's
    /// exactly, and each one withheld is withheld for the one reason left — a subscriber with a
    /// <c>var</c> Text/Code parameter, whose declared length nothing in the assembly states.
    /// </summary>
    [SkippableFact]
    public void Every_emitted_subtree_is_BCs_and_every_withheld_one_has_a_var_text_subscriber()
    {
        int withMethods = 0, emitted = 0;
        var inexact = new List<string>();
        var unexplained = new List<string>();
        foreach (var cu in RegisteredCodeunits())
        {
            var bc = MethodsOf(Load(cu.BcDocumentPath));
            if (bc is null) continue;
            withMethods++;
            var mine = RunnerDocument(cu.Id) is { } doc ? MethodsOf(doc) : null;
            if (mine is not null)
            {
                emitted++;
                if (Canonical(mine) != Canonical(bc))
                    inexact.Add($"{cu.Bundle.AppName} codeunit {cu.Id}:\n  BC     {Canonical(bc)}\n  runner {Canonical(mine)}");
                continue;
            }
            var explained = bc.ChildNodes.OfType<XmlElement>().Any(m =>
                m.GetElementsByTagName("EventSubscriberAttribute", MetaNs).Count > 0
                && m.GetElementsByTagName("Parameter", MetaNs).OfType<XmlElement>().Any(p =>
                    p.GetAttribute("RuntimeType") is "ByRef<NavText>" or "ByRef<NavCode>"));
            if (!explained) unexplained.Add($"{cu.Bundle.AppName} codeunit {cu.Id}");
        }

        _output.WriteLine($"codeunits with <Methods>={withMethods} emitted={emitted} inexact={inexact.Count} unexplained={unexplained.Count}");
        Assert.True(withMethods > 0, "no ground-truth codeunit carries <Methods> — nothing was measured");
        Assert.True(inexact.Count == 0, $"{inexact.Count} of {emitted} emitted subtrees differ from BC's:\n" + string.Join("\n", inexact.Take(5)));
        Assert.True(unexplained.Count == 0,
            $"{unexplained.Count} codeunit(s) withheld without a var Text/Code subscriber parameter: " + string.Join(", ", unexplained));
    }

    /// <summary>
    /// The two skip flags are distinct bits. Every shipped subscriber sets both or neither, so the
    /// population test above cannot catch them being swapped; this can.
    /// </summary>
    [Fact]
    public void The_two_skip_flags_are_read_from_their_own_bits()
    {
        var license = (int)Microsoft.Dynamics.Nav.Types.EventSubscriberCallOptions.SkipOnMissingLicense;
        var permission = (int)Microsoft.Dynamics.Nav.Types.EventSubscriberCallOptions.SkipOnMissingPermission;
        Assert.Equal((true, false), RecordPatches.DecodeSubscriberCallOptions(license));
        Assert.Equal((false, true), RecordPatches.DecodeSubscriberCallOptions(permission));
        Assert.Equal((false, false), RecordPatches.DecodeSubscriberCallOptions(0));
    }

    /// <summary>
    /// Every [NavEvent] publisher the assembly declares must be in the symbol file's list, or the
    /// codeunit is withheld. The shipped symbol files never omit one, so this drives the check
    /// with codeunit 3906's list minus OnVerifyAddtoAllowedList — and, as the control, the full
    /// list, which must be accepted.
    /// </summary>
    [SkippableFact]
    public void A_publisher_the_symbol_file_omits_withholds_the_codeunit()
    {
        var cu = RegisteredCodeunits().FirstOrDefault(c => c.Id == 3906 && c.Bundle.AppName == "System Application");
        Assert.NotNull(cu);
        var app = MetadataEquivalenceHarness.FindAppPackage(cu!.Bundle)!;

        BcAppSymbolCache.CodeunitMethodSymbol Publisher(int id, string name)
            => new(id, name, "EventPublisherAttribute", "InternalEvent", Parameters: []);
        var add = Publisher(-1957074930, "OnVerifyAddtoAllowedList");
        var modify = Publisher(-476582944, "OnVerifyModifyAllowedList");

        Assert.Null(RecordPatches.CodeunitMethodTableRefusalForTests(app, 3906, [add, modify]));
        Assert.Equal("publisher 'OnVerifyAddtoAllowedList' is not in the symbol file",
            RecordPatches.CodeunitMethodTableRefusalForTests(app, 3906, [modify]));
    }
}
