// ActionRunPageLinkRefusalTests — drives the RunPageLink count-mismatch refusal in
// RunnerPageInstance.LinksFromSymbols (issue #3274).
//
// WHAT WAS MISSING
//   #3248 introduced the refusal and #3267 widened it, but nothing executed it. A grep for
//   DeclaredRunPageLinkEntries across AlRunner.Tests found only ActionRunPageLinkUnreadableEntryTests,
//   which pins the SYMBOL-CACHE side -- that the parser counts declared entries correctly and
//   carries unreadable ones by text. That is the guard's INPUT. Nothing asserted what the
//   consumer does with it, so deleting the comparison turned no test red.
//
// WHY THE GUARD MATTERS, AND WHY IT FAILS CLOSED
//   An action's RunPageLink is a CONJUNCTION: every entry narrows the target's rowset.
//   Applying only the entries the parser could read drops a conjunct, so the target opens
//   showing MORE rows than real BC shows. That is a silently wrong rowset, not a visibly
//   missing one -- which is why the refusal is loud rather than a best-effort partial link.
//
// WHY THIS IS A C# TEST AND NOT AN END-TO-END AL ONE
//   LinksFromSymbols is reached only through ResolveRunTargetFromSymbols, which reads an
//   action's RunObject/RunPageLink out of a dependency .app's SymbolReference.json. A page the
//   runner compiled from AL source takes ResolveRunTargetFromMetadata instead -- BC's own
//   compiled ActionDefinition, where the target is already a kind and a numeric id and there
//   is no property TEXT left to fail to parse. So the AL route needs a precompiled dependency
//   AND a live BC page-action invoke.
//
//   That end-to-end route is blocked on this machine by an unrelated defect, filed as #3977:
//   every runner-extras suite that invokes a page action -- including the merged, CI-gated
//   tests/runner-extras/testpage-promoted-actionref -- dies in BC's own
//   Codeunit2000000002.OnInvokeAsync with "Could not load file or assembly
//   'Microsoft.Bcl.AsyncInterfaces, Version=10.0.0.5'", because the dependency resolver probes
//   the service-tier directory by NAME and the provisioned artifacts carry 10.0.0.2. Both
//   failures surface through GetLastErrorText(), so an AL asserterror cannot tell this guard's
//   refusal from that FileLoadException -- the AL test would have been unfalsifiable here.
//
//   Pinning the method directly is the established answer to exactly this shape: see
//   RecordPatchesGetPageControlFieldMapDependencyTests, whose header records the same decision
//   for the same reason (an arm AL cannot reach, pinned against the method instead).
//
//   A precompiled-dependency runner-extras suite for this WAS built and discarded rather than
//   shipped unverified: the fixtures (a hand-built SymbolReference.json carrying the unreadable
//   entry, plus a Tier-1 .deps-bin DLL) compiled and loaded, and both tests reached real runner
//   behaviour, but #3977 made the refusal arm unfalsifiable on this machine -- it could not be
//   distinguished from the FileLoadException. The PR body records the recipe so it can be
//   rebuilt once #3977 is fixed; shipping a test whose RED nobody has seen is what tdd.md's
//   mutation step exists to prevent.
//
// NOT A CLAIM ABOUT BC
//   Nothing here asserts what Business Central does. The subject is how this runner reads
//   SymbolReference.json -- a Microsoft build artifact a real service tier never reads at all,
//   because a real tier has the compiled page metadata this code path substitutes for. So this
//   owes no corpus PR, for the same structural reason ActionRunPageLinkUnreadableEntryTests
//   does not.
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// BcAppSymbolCache.Get() and RecordPatches' dependency state resolve through the
// process-global CacheRoots override, the same reason the sibling dependency suites serialise.
[Collection(CacheRootsSerialCollection.Name)]
public class ActionRunPageLinkRefusalTests
{
    private static string WriteApp(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    // Ids distinct from every other fixture in this suite: RecordPatches' dependency state is
    // process-global, so a shared id risks reading back another test's payload.
    private const int HostPageId = 88231801;
    private const int TargetPageId = 88231802;

    // The target's source table, so the entries the guard lets through resolve to real field
    // numbers rather than to 0 for an unrelated reason.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Tables": [
            {
              "Id": 88231800,
              "Name": "ARPLR Row",
              "Fields": [
                { "Id": 1, "Name": "No.", "TypeDefinition": { "Name": "Code[20]" }, "Properties": [] },
                { "Id": 2, "Name": "Document No.", "TypeDefinition": { "Name": "Code[20]" }, "Properties": [] },
                { "Id": 3, "Name": "Amount", "TypeDefinition": { "Name": "Decimal" }, "Properties": [] }
              ],
              "Keys": [ { "Name": "PK", "FieldNames": [ "No." ], "Properties": [] } ],
              "Properties": []
            }
          ],
          "Pages": [
            {
              "Id": 88231801,
              "Name": "ARPLR Host",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88231800" }
              ]
            },
            {
              "Id": 88231802,
              "Name": "ARPLR Target",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88231800" }
              ]
            }
          ]
        }
        """;

    private static void WithLoadedDependency(Action body)
    {
        var dir = TestScratch.Dir("al-runner-action-runpagelink-refusal-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));
            body();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // LinksFromSymbols is a private INSTANCE method, but the only instance state it reads is
    // _pageId, and only to name the page in the refusal message. Everything else it touches is
    // static (RecordPatches.ResolveSourceTableIdForAnyPage / TryResolveDependencyFieldId). So
    // an uninitialised instance is enough to execute it, and avoids needing a live NavForm --
    // which would drag in the BC engine this whole file exists to stay out of.
    //
    // _pageId is set by hand so the message's page number is a real assertion rather than 0.
    private static object Invoke(BcAppSymbolCache.ActionRunObjectSymbol spec, int actionId = 4242)
    {
        var type = typeof(RunnerPageInstance);
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_pageId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(instance, HostPageId);

        var method = type.GetMethod("LinksFromSymbols", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "RunnerPageInstance.LinksFromSymbols not found — this test pins that method, so a "
                + "rename must fail here rather than silently pin nothing.");

        try
        {
            return method.Invoke(instance, new object?[] { actionId, spec, TargetPageId })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    /// <summary>
    /// ARM 1 — the count comparison. The spec declares two entries and the parse carries one,
    /// with no unreadable text alongside it, so `parsed.Count != DeclaredRunPageLinkEntries` is
    /// the ONLY condition of the three that can fire. That isolation is the point: it is what
    /// makes this test go green again when the null arm or the unreadable arm is broken, and
    /// red only when the count arm is.
    ///
    /// <para>The shape is not hypothetical. It is what a parser gap on perfectly legal AL
    /// produces — #3267, where a directive-aware splitter and a bare comma splitter disagreed
    /// on the entry count of a link that was completely readable.</para>
    /// </summary>
    [Fact]
    public void ParsedFewerEntriesThanDeclared_IsRefusedRatherThanAppliedPartially()
        => WithLoadedDependency(() =>
        {
            var spec = new BcAppSymbolCache.ActionRunObjectSymbol(
                ObjectName: "ARPLR Target",
                RunPageOnRec: false,
                DeclaredRunPageLinkEntries: 2,
                RunPageLink: new List<BcAppSymbolCache.PageSubFormLinkSymbol>
                {
                    new("Document No.", "field", "\"No.\"", false),
                },
                UnreadableRunPageLinkEntries: null);

            var ex = Assert.Throws<RunnerOutOfScopeException>(() => Invoke(spec));

            // The surface, so the message says WHICH action on WHICH page refused.
            Assert.Contains("TestPage action 4242", ex.Message);
            Assert.Contains(HostPageId.ToString(), ex.Message);
            Assert.Contains("not-yet-implemented", ex.Message);
            Assert.Contains("ARPLR Target", ex.Message);

            // The two counts — the refusal's whole subject. Asserting BOTH numbers is what
            // makes this a proof the comparison ran: a guard that refused unconditionally
            // could not state that it read 1 of 2.
            Assert.Contains("RunPageLink of 2 entr(ies)", ex.Message);
            Assert.Contains("could read 1 of them", ex.Message);

            // The reason it fails closed rather than applying the readable half.
            Assert.Contains("would show MORE rows than real BC", ex.Message);

            // No unreadable text was carried, so the message must not invent an entry list.
            // This is what keeps arm 1 distinguishable from arm 3 in the message itself.
            Assert.DoesNotContain("could not read:", ex.Message);
        });

    /// <summary>
    /// ARM 2 — the null parse. `HasRunPageLink` is true (entries were declared) but the parser
    /// produced no list at all, which is a different failure from producing a short one and
    /// reaches a different operand of the same `if`. The message must still state the shortfall,
    /// with the parsed count read as 0 rather than throwing on the null.
    /// </summary>
    [Fact]
    public void ParsedNull_WithEntriesDeclared_IsRefusedAndReportsZeroRead()
        => WithLoadedDependency(() =>
        {
            var spec = new BcAppSymbolCache.ActionRunObjectSymbol(
                ObjectName: "ARPLR Target",
                RunPageOnRec: false,
                DeclaredRunPageLinkEntries: 3,
                RunPageLink: null,
                UnreadableRunPageLinkEntries: null);

            var ex = Assert.Throws<RunnerOutOfScopeException>(() => Invoke(spec));

            Assert.Contains("RunPageLink of 3 entr(ies)", ex.Message);
            // `parsed?.Count ?? 0` — the null-coalesce is the arm-specific behaviour, and a
            // guard that dereferenced the null instead would throw NullReferenceException here.
            Assert.Contains("could read 0 of them", ex.Message);
            Assert.Contains("would show MORE rows than real BC", ex.Message);
        });

    /// <summary>
    /// ARM 3 — unreadable entries, with the counts AGREEING. #3267 added this arm precisely so
    /// the refusal does not depend on the count alone; here the count arm cannot fire, so only
    /// the unreadable arm can. The entry TEXT must reach the message, because the count alone
    /// leaves a developer to open SymbolReference.json to find out which entry was lost.
    /// </summary>
    [Fact]
    public void UnreadableEntriesCarried_WithCountsAgreeing_IsStillRefused_AndNamesTheEntries()
        => WithLoadedDependency(() =>
        {
            var spec = new BcAppSymbolCache.ActionRunObjectSymbol(
                ObjectName: "ARPLR Target",
                RunPageOnRec: false,
                // 1 declared, 1 parsed — the count arm is satisfied and cannot fire.
                DeclaredRunPageLinkEntries: 1,
                RunPageLink: new List<BcAppSymbolCache.PageSubFormLinkSymbol>
                {
                    new("Document No.", "field", "\"No.\"", false),
                },
                UnreadableRunPageLinkEntries: new List<string> { "\"Amount\" whenever(42)" });

            var ex = Assert.Throws<RunnerOutOfScopeException>(() => Invoke(spec));

            Assert.Contains("RunPageLink of 1 entr(ies)", ex.Message);
            Assert.Contains("could read 1 of them", ex.Message);
            // The text itself, which is this arm's whole contribution over the count arm.
            Assert.Contains("could not read: \"Amount\" whenever(42)", ex.Message);
        });

    /// <summary>
    /// THE NEGATIVE DIRECTION, and the reason it is not optional: a guard that refused every
    /// action would satisfy all three tests above while breaking every RunPageLink in the
    /// repository. A spec whose counts agree and which carries no unreadable text must get
    /// past the guard and produce APPLIED links — asserted on the resolved field numbers and
    /// kinds, not merely on the absence of a throw.
    /// </summary>
    [Fact]
    public void EveryDeclaredEntryRead_IsAppliedRatherThanRefused()
        => WithLoadedDependency(() =>
        {
            var spec = new BcAppSymbolCache.ActionRunObjectSymbol(
                ObjectName: "ARPLR Target",
                RunPageOnRec: false,
                DeclaredRunPageLinkEntries: 2,
                RunPageLink: new List<BcAppSymbolCache.PageSubFormLinkSymbol>
                {
                    new("Document No.", "field", "\"No.\"", false),
                    new("Amount", "const", "42", false),
                },
                UnreadableRunPageLinkEntries: null);

            var links = (System.Collections.IEnumerable)Invoke(spec);
            var applied = links.Cast<object>().ToList();

            Assert.Equal(2, applied.Count);

            // Both entries resolved against the dependency's own table: "Document No." is field
            // 2 and "Amount" is field 3 in the SymbolReference above. Reading the values proves
            // the links were BUILT, not merely counted — a guard that returned an empty list
            // would also "not throw".
            static (int TargetFieldNo, string Kind, int HostFieldNo, string Value) Read(object link)
            {
                var t = link.GetType();
                return ((int)t.GetProperty("TargetFieldNo")!.GetValue(link)!,
                        t.GetProperty("Kind")!.GetValue(link)!.ToString()!,
                        (int)t.GetProperty("HostFieldNo")!.GetValue(link)!,
                        (string)t.GetProperty("Value")!.GetValue(link)!);
            }

            var first = Read(applied[0]);
            Assert.Equal(2, first.TargetFieldNo);
            Assert.Equal("FIELD", first.Kind);
            // The host side of a field(...) entry resolves against the HOST page's table, which
            // is the same table here, so "No." is field 1.
            Assert.Equal(1, first.HostFieldNo);

            var second = Read(applied[1]);
            Assert.Equal(3, second.TargetFieldNo);
            Assert.Equal("CONST", second.Kind);
            Assert.Equal("42", second.Value);
        });

    /// <summary>
    /// The other negative direction: an action declaring NO RunPageLink at all must return the
    /// empty list before the guard is consulted. Without this row, a guard that threw whenever
    /// DeclaredRunPageLinkEntries was 0 would refuse every plain RunObject action in the
    /// repository and no test above would notice.
    /// </summary>
    [Fact]
    public void NoRunPageLinkDeclared_ReturnsNoLinksWithoutConsultingTheGuard()
        => WithLoadedDependency(() =>
        {
            var spec = new BcAppSymbolCache.ActionRunObjectSymbol(
                ObjectName: "ARPLR Target",
                RunPageOnRec: true,
                DeclaredRunPageLinkEntries: 0,
                RunPageLink: null,
                UnreadableRunPageLinkEntries: null);

            var links = (System.Collections.IEnumerable)Invoke(spec);
            Assert.Empty(links.Cast<object>());
        });
}
