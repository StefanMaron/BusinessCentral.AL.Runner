// SilentReflectionLookupRatchetTests — the ratchet for #3663.
//
// THE SHAPE
//   A reflection member lookup under AlRunner/Patches/ fails, `?.` propagates the null, and
//   the null check exits with a value meaning "nothing here" — indistinguishable from a
//   legitimate empty answer. A BC rename then makes the runner answer WRONG rather than
//   fail: #3647's failed GetProperty yielded no filters, so a query returned more rows than
//   it should and nothing said why. #3656 and #3660 are the same mechanism one call path
//   over, each found while fixing the one before it.
//
//   `AlRunner/Infrastructure/BcShapeGapException.cs` was built for exactly this and draws
//   the line: raise when THE READ COULD NOT BE PERFORMED, stay silent when THE READ SUCCEEDED
//   AND THE ANSWER WAS MERELY UNWELCOME. Nothing drove adoption, so every new site defaulted
//   to silent and the defect was rediscovered one method at a time.
//
// WHAT THIS ASSERTS, AND WHAT IT DELIBERATELY DOES NOT
//   It asserts ONLY that the population does not GROW. It does not assert that every site in
//   it is a bug — per #3663 and BcShapeGapException's own header, some fraction of these are
//   correct as they stand, and deciding which needs the per-site adjudication #3657 spent its
//   review on. So this is a population to triage, not a defect count, and the ratchet exists
//   to pace that triage rather than to pass judgement on it.
//
//   Concretely: Baseline may only go DOWN. Converting a site to BcShape.* (or to any explicit
//   refusal) lowers it; writing a new `?.`-absorbed lookup raises it and fails here.
//
// WHY A RATCHET RATHER THAN A CONVERSION
//   Converting the population in one change would produce something nobody can review, and
//   each conversion needs its own judgement. A ratchet makes the number visible so it cannot
//   drift upward unnoticed, and lets the conversions land a few at a time.
//
// WHAT COUNTS AS SILENT — PURELY LOCAL, NO DATAFLOW
//   The classifier reads only the lookup expression itself, never the surrounding method:
//
//     x.GetProperty(...)?.GetValue(y)     SILENT — the null is absorbed at the expression
//     x?.GetType().GetProperty(...)       SILENT — a null-conditional receiver does the same
//     x.GetProperty(...) ?? <default>     SILENT — the null becomes an ordinary value
//
//     x.GetProperty(...) ?? throw ...     LOUD  — 105 sites; the commonest shape in the tree
//     x.GetProperty(...) ?? x.GetProperty(...)   CHAIN — an alternate member NAME, not a
//                                         default: BC renamed FieldNo to No and the site
//                                         asks for both. Neither loud nor silent, and
//                                         counting it either way would be wrong.
//     x.GetProperty(...)!                 not this file's population — a null-forgiving `!`
//                                         NREs at first use. BcInternalsNullForgivingGuardTests
//                                         owns that shape (#3051); counting it here would
//                                         double-count it and couple two ratchets.
//     var p = x.GetProperty(...);         not counted — the null goes into a variable and the
//                                         failure path becomes a property of the METHOD. 453
//                                         sites, and a text scanner cannot decide them. This
//                                         is the honest abstention, not an oversight.
//
//   This locality is the design, not a shortcut. A window-based classifier — "does anything
//   within seven lines raise?" — is what #3663 measured with, and it reports `?? throw` as
//   silent, which is how its headline read 339 where the dataflow-free reading is 120. A
//   ratchet has to be reproducible by the next person from the code alone; a heuristic that
//   abstains on half its population cannot be.
//
// THE EXCLUDED FALSE-POSITIVE CLASSES
//   `JsonElement.GetProperty(string)` and `StackFrame.GetMethod()` are not reflection lookups
//   at all — they share a method name with one. Both are live in this tree (21 sites), so
//   these are exclusions against measured code, not defensive ones. See ExcludedClass.
//
// See also:
//   .claude/rules/loud-failures.md              — no silent out-of-scope failures
//   AlRunner/Infrastructure/BcShapeGapException.cs — the line between a gap and an answer
//   docs/silent-reflection-lookup-ratchet.md    — the measurement, and how to move the number
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class SilentReflectionLookupRatchetTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// The number of silent reflection lookups under <c>AlRunner/Patches/</c> as of #3663.
    /// <para><b>This may only ever go DOWN.</b> Lower it in the PR that converts a site, and
    /// say in the PR body which sites moved. If it went UP, a new silent lookup was written —
    /// give it an explicit refusal (<c>BcShape.Property</c> / <c>.Method</c> / <c>.Field</c>,
    /// or <c>?? throw</c>) rather than raising this number.</para>
    /// <para><b>A conversion does not necessarily lower this.</b> #3664 merged a real
    /// conversion of <c>RecordPatches.QueryJoin.cs</c> and the count did not move: its
    /// <c>StaticMember</c> helper probes two spellings —
    /// <c>GetField(...)?.GetValue(null) ?? GetProperty(...)?.GetValue(null)</c> — and refuses
    /// the pair with <c>?? throw new BcShapeGapException</c> on the NEXT statement. That code
    /// is loud, and both lookups still classify as silent, because each one individually ends
    /// in a <c>?.</c> and this classifier reads only the expression (see the header). #3665
    /// carries the same shape and will behave the same way. So a conversion landing with no
    /// ratchet movement is the guard working as specified, not a defect in it — the 2 sites in
    /// QueryJoin and 3 in QueryProjection are counted here on purpose.</para>
    /// </summary>
    private const int Baseline = 125;

    // ── The assertions ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ratchet. Equality rather than <c>&lt;=</c> on purpose: a DROP has to be recorded
    /// here too, otherwise the number silently stops describing the tree and a later addition
    /// hides under the slack left by an earlier conversion.
    /// </summary>
    [Fact]
    public void TheSilentReflectionLookupPopulation_DoesNotGrow()
    {
        var sites = SilentSites();

        Assert.True(sites.Count <= Baseline,
            $"{sites.Count - Baseline} NEW silent reflection lookup(s) under AlRunner/Patches/. "
            + "A failed lookup here returns null, `?.` propagates it, and the null check exits with "
            + "a value meaning \"nothing here\" — so a BC rename makes the runner answer WRONG "
            + "instead of failing (#3663, and #3647/#3656/#3660 for what that costs). Give the "
            + "lookup an explicit refusal — BcShape.Property/Method/Field, or `?? throw` — or, if "
            + "absence really is a legitimate answer here, say so at the call site and lower "
            + $"Baseline by hand.{Environment.NewLine}"
            + string.Join(Environment.NewLine, Unaccounted(sites)));

        Assert.True(sites.Count == Baseline,
            $"{Baseline - sites.Count} silent reflection lookup(s) were converted — thank you. "
            + $"Lower Baseline in this file to {sites.Count} and name the converted sites in the "
            + "PR body. The count is asserted exactly so that slack from a conversion cannot "
            + "later hide a new silent site (#3663).");
    }

    /// <summary>
    /// The ratchet counts; this NAMES. Without it a conversion in one file and a regression in
    /// another would net to zero and the count assertion above would stay green while the tree
    /// had gained a silent site.
    /// <para>Keyed on file + receiver + kind + first argument, never on a line number: a line
    /// number goes stale on the next edit above it and would turn this guard into noise. Four
    /// keys name two identical sites each, so the entry carries an occurrence COUNT — a set
    /// would silently absorb the second one, and 120 sites would read as 116.</para>
    /// </summary>
    [Fact]
    public void TheSilentSites_AreTheOnesRecorded()
    {
        var actual = SilentSites()
            .GroupBy(Key)
            .ToDictionary(g => g.Key, g => g.Count());

        var appeared = new List<string>();
        var vanished = new List<string>();

        foreach (var key in actual.Keys.Union(KnownSites.Keys))
        {
            actual.TryGetValue(key, out var now);
            KnownSites.TryGetValue(key, out var was);
            if (now > was) appeared.Add($"{Describe(key)}  (recorded {was}, found {now})");
            if (now < was) vanished.Add($"{Describe(key)}  (recorded {was}, found {now})");
        }

        Assert.True(appeared.Count == 0,
            "silent reflection lookup(s) not accounted for in KnownSites. A failed lookup here is "
            + "absorbed at the expression, so a BC rename makes the runner answer WRONG instead of "
            + "failing (#3663). Give it an explicit refusal — BcShape.Property/Method/Field, or "
            + $"`?? throw` — rather than adding an entry:{Environment.NewLine}"
            + string.Join(Environment.NewLine, appeared.OrderBy(s => s, StringComparer.Ordinal)));

        Assert.True(vanished.Count == 0,
            "these silent reflection lookups are recorded in KnownSites but are no longer found. If "
            + "you converted them, remove their entries and lower Baseline; if you merely MOVED or "
            + $"RENAMED one, update its entry (#3663):{Environment.NewLine}"
            + string.Join(Environment.NewLine, vanished.OrderBy(s => s, StringComparer.Ordinal)));
    }

    // ── The allowlist ───────────────────────────────────────────────────────────────────
    //
    // 125 sites across 42 files, 121 distinct keys (four name two identical sites each, hence
    // the count). Generated from the tree at the commit that introduced this file, NOT curated
    // — every entry here is a site somebody has yet to adjudicate, and being on the list says
    // only "this existed when the ratchet was installed", never "this is correct".
    //
    // TO MOVE THE NUMBER: convert a site, delete its entry, lower Baseline by the same amount.

    private readonly record struct SiteKey(string File, string Recv, string Kind, string Member);

    private static readonly IReadOnlyDictionary<SiteKey, int> KnownSites = new (string File, string Recv, string Kind, string Member, int Count)[]
    {
        // ALDatabasePatches.cs — 2
        ("AlRunner/Patches/ALDatabasePatches.cs", "lang?", "GetProperty", "name", 1),
        ("AlRunner/Patches/ALDatabasePatches.cs", "tCSide!", "GetProperty", "\"DetailedErrorMessage\"", 1),
        // AlCompilerStreamPatches.cs — 4
        ("AlRunner/Patches/AlCompilerStreamPatches.cs", "company?.GetType()", "GetProperty", "\"SharedObjects\"", 1),
        ("AlRunner/Patches/AlCompilerStreamPatches.cs", "parentOfResult.GetType()", "GetProperty", "\"Tree\"", 1),
        ("AlRunner/Patches/AlCompilerStreamPatches.cs", "session?.GetType()", "GetProperty", "\"Company\"", 1),
        ("AlRunner/Patches/AlCompilerStreamPatches.cs", "tree?.GetType()", "GetProperty", "\"Session\"", 1),
        // ApplicationObjectBasePatches.cs — 1
        ("AlRunner/Patches/ApplicationObjectBasePatches.cs", "self.GetType()", "GetField", "\"executePermissionsValidated\"", 1),
        // BlobStoreIsolationPatches.cs — 4
        ("AlRunner/Patches/BlobStoreIsolationPatches.cs", "dataAccess.GetType()", "GetProperty", "\"DataProvider\"", 1),
        ("AlRunner/Patches/BlobStoreIsolationPatches.cs", "field?.GetType()", "GetProperty", "\"FieldNclType\"", 1),
        ("AlRunner/Patches/BlobStoreIsolationPatches.cs", "mrbType", "GetProperty", "\"FieldCount\"", 1),
        ("AlRunner/Patches/BlobStoreIsolationPatches.cs", "mrbType", "GetProperty", "\"MetaTable\"", 1),
        // CodeunitPatches.MetaCodeunit.cs — 2
        ("AlRunner/Patches/CodeunitPatches.MetaCodeunit.cs", "tAppGroup?", "GetProperty", "\"BaseGroup\"", 1),
        ("AlRunner/Patches/CodeunitPatches.MetaCodeunit.cs", "tAppGroup?", "GetField", "\"BaseGroup\"", 1),
        // CodeunitPatches.cs — 7
        ("AlRunner/Patches/CodeunitPatches.cs", "navGlobal?", "GetProperty", "\"NCLMetadata\"", 1),
        ("AlRunner/Patches/CodeunitPatches.cs", "navSourceObj?.GetType()", "GetProperty", "\"SourceObjectId\"", 1),
        ("AlRunner/Patches/CodeunitPatches.cs", "objId?.GetType()", "GetProperty", "\"ObjectNumber\"", 2),
        ("AlRunner/Patches/CodeunitPatches.cs", "self.GetType()", "GetProperty", "propName", 1),
        ("AlRunner/Patches/CodeunitPatches.cs", "tAppGroup?", "GetProperty", "\"BaseGroup\"", 1),
        ("AlRunner/Patches/CodeunitPatches.cs", "tAppGroup?", "GetField", "\"BaseGroup\"", 1),
        // CompanyAccessPatches.cs — 1
        ("AlRunner/Patches/CompanyAccessPatches.cs", "session?.GetType()", "GetProperty", "\"Company\"", 1),
        // EventSubscriberPatches.cs — 5
        ("AlRunner/Patches/EventSubscriberPatches.cs", "(string?)t", "GetProperty", "\"TargetFieldName\"", 1),
        ("AlRunner/Patches/EventSubscriberPatches.cs", "_tNavAppGroup", "GetProperty", "\"BaseGroup\"", 1),
        ("AlRunner/Patches/EventSubscriberPatches.cs", "_tNavAppGroup", "GetField", "\"BaseGroup\"", 1),
        ("AlRunner/Patches/EventSubscriberPatches.cs", "oType", "GetProperty", "\"TargetFieldId\"", 1),
        ("AlRunner/Patches/EventSubscriberPatches.cs", "t", "GetProperty", "\"TargetFieldId\"", 1),
        // FlowFieldPatches.cs — 1
        ("AlRunner/Patches/FlowFieldPatches.cs", "fieldObj.GetType()", "GetProperty", "\"EmptyValue\"", 1),
        // HelperShims.cs — 5
        ("AlRunner/Patches/HelperShims.cs", "m?.GetType()", "GetProperty", "\"Method\"", 1),
        ("AlRunner/Patches/HelperShims.cs", "requestMessage?.GetType()", "GetProperty", "\"Method\"", 1),
        ("AlRunner/Patches/HelperShims.cs", "session?.GetType()", "GetProperty", "\"TestExecution\"", 1),
        ("AlRunner/Patches/HelperShims.cs", "table?.GetType()", "GetProperty", "\"TableId\"", 1),
        ("AlRunner/Patches/HelperShims.cs", "testExecution?.GetType()", "GetMethod", "\"RedirectNotificationOperationToTestHandler\"", 1),
        // MediaSetPatches.cs — 1
        ("AlRunner/Patches/MediaSetPatches.cs", "key?.PropertyType", "GetProperty", "\"Value\"", 1),
        // MetadataPatches.cs — 5
        ("AlRunner/Patches/MetadataPatches.cs", "envType", "GetField", "\"instance\"", 1),
        ("AlRunner/Patches/MetadataPatches.cs", "tDiag?", "GetProperty", "\"GetMostSpecificInstance\"", 1),
        ("AlRunner/Patches/MetadataPatches.cs", "tDiag?", "GetField", "\"GetMostSpecificInstance\"", 1),
        ("AlRunner/Patches/MetadataPatches.cs", "tDiagForTenant?", "GetProperty", "\"GetMostSpecificInstance\"", 1),
        ("AlRunner/Patches/MetadataPatches.cs", "tDiagForTenant?", "GetField", "\"GetMostSpecificInstance\"", 1),
        // MockTestPage.cs — 1
        ("AlRunner/Patches/MockTestPage.cs", "_expression.GetType()", "GetProperty", "\"Name\"", 1),
        // NavAppResourcePatches.cs — 2
        ("AlRunner/Patches/NavAppResourcePatches.cs", "session?.GetType()", "GetProperty", "\"Tenant\"", 1),
        ("AlRunner/Patches/NavAppResourcePatches.cs", "tenant?.GetType()", "GetProperty", "\"DefaultEncoding\"", 1),
        // NavRecordIdPatches.cs — 1
        ("AlRunner/Patches/NavRecordIdPatches.cs", "tNri", "GetProperty", "\"CollationAwareStringComparer\"", 1),
        // NavReportSync.cs — 9
        ("AlRunner/Patches/NavReportSync.cs", "company?.GetType()", "GetMethod", "\"RegisterForm\"", 1),
        ("AlRunner/Patches/NavReportSync.cs", "metaT", "GetProperty", "\"CaptionML\"", 1),
        ("AlRunner/Patches/NavReportSync.cs", "metaT", "GetProperty", "\"Name\"", 1),
        ("AlRunner/Patches/NavReportSync.cs", "navGlobal?", "GetProperty", "\"MetadataProvider\"", 1),
        ("AlRunner/Patches/NavReportSync.cs", "navGlobal?", "GetProperty", "\"NCLMetadata\"", 1),
        ("AlRunner/Patches/NavReportSync.cs", "navReport.GetType()", "GetProperty", "\"__IsAsync\"", 1),
        ("AlRunner/Patches/NavReportSync.cs", "pageProps?.GetType()", "GetProperty", "\"PageType\"", 1),
        ("AlRunner/Patches/NavReportSync.cs", "session?.GetType()", "GetProperty", "\"Company\"", 1),
        ("AlRunner/Patches/NavReportSync.cs", "tMeta", "GetProperty", "\"RequestFormMetadata\"", 1),
        // RecordPatches.CompanySystemTable.cs — 3
        ("AlRunner/Patches/RecordPatches.CompanySystemTable.cs", "company.GetType()", "GetField", "\"companyName\"", 1),
        ("AlRunner/Patches/RecordPatches.CompanySystemTable.cs", "company.GetType()", "GetField", "\"companyTableId\"", 1),
        ("AlRunner/Patches/RecordPatches.CompanySystemTable.cs", "session?.GetType()", "GetProperty", "\"Company\"", 1),
        // RecordPatches.CreateObjectInstance.cs — 2
        ("AlRunner/Patches/RecordPatches.CreateObjectInstance.cs", "current?.GetType()", "GetProperty", "\"ObjectNumber\"", 2),
        // RecordPatches.FieldFindIntercept.cs — 2
        ("AlRunner/Patches/RecordPatches.FieldFindIntercept.cs", "recordId.GetType()", "GetProperty", "\"Fields\"", 1),
        ("AlRunner/Patches/RecordPatches.FieldFindIntercept.cs", "request.GetType()", "GetProperty", "\"RecordId\"", 1),
        // RecordPatches.FieldVirtualTable.cs — 3
        ("AlRunner/Patches/RecordPatches.FieldVirtualTable.cs", "navGuidType", "GetProperty", "\"Default\"", 1),
        ("AlRunner/Patches/RecordPatches.FieldVirtualTable.cs", "navGuidType", "GetField", "\"Default\"", 1),
        ("AlRunner/Patches/RecordPatches.FieldVirtualTable.cs", "tFdp", "GetConstructor", "BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, binder: null, types: new[] { session.GetType() }, modifiers: null", 1),
        // RecordPatches.InstallBaseline.cs — 1
        ("AlRunner/Patches/RecordPatches.InstallBaseline.cs", "dataAccess.GetType()", "GetProperty", "\"DataProvider\"", 1),
        // RecordPatches.MetaQueryFromBcDocument.cs — 3
        ("AlRunner/Patches/RecordPatches.MetaQueryFromBcDocument.cs", "design.GetType()", "GetProperty", "\"DataItems\"", 1),
        ("AlRunner/Patches/RecordPatches.MetaQueryFromBcDocument.cs", "di.GetType()", "GetProperty", "\"QueryColumns\"", 1),
        ("AlRunner/Patches/RecordPatches.MetaQueryFromBcDocument.cs", "o.GetType()", "GetProperty", "name", 1),
        // RecordPatches.NclMetaFormReportBuilder.cs — 2
        ("AlRunner/Patches/RecordPatches.NclMetaFormReportBuilder.cs", "tAppGroup?", "GetProperty", "\"BaseGroup\"", 1),
        ("AlRunner/Patches/RecordPatches.NclMetaFormReportBuilder.cs", "tAppGroup?", "GetField", "\"BaseGroup\"", 1),
        // RecordPatches.NclMetaQueryBuilder.cs — 1
        ("AlRunner/Patches/RecordPatches.NclMetaQueryBuilder.cs", "col.GetType()", "GetProperty", "\"CaptionML\"", 1),
        // RecordPatches.NclMetaTableFromBcDocument.cs — 6
        ("AlRunner/Patches/RecordPatches.NclMetaTableFromBcDocument.cs", "built.GetType()", "GetField", "\"metadataAppGroupMetaTable\"", 1),
        ("AlRunner/Patches/RecordPatches.NclMetaTableFromBcDocument.cs", "metaTable.GetType()", "GetProperty", "\"Fields\"", 1),
        ("AlRunner/Patches/RecordPatches.NclMetaTableFromBcDocument.cs", "original?.GetType()", "GetProperty", "\"Item\"", 1),
        ("AlRunner/Patches/RecordPatches.NclMetaTableFromBcDocument.cs", "t", "GetProperty", "name", 1),
        ("AlRunner/Patches/RecordPatches.NclMetaTableFromBcDocument.cs", "tAppGroup", "GetProperty", "\"BaseGroup\"", 1),
        ("AlRunner/Patches/RecordPatches.NclMetaTableFromBcDocument.cs", "tAppGroup", "GetField", "\"BaseGroup\"", 1),
        // RecordPatches.ObjectMetadataSystemTable.cs — 2
        ("AlRunner/Patches/RecordPatches.ObjectMetadataSystemTable.cs", "tNavEnvironment", "GetProperty", "\"EmitVersion\"", 1),
        ("AlRunner/Patches/RecordPatches.ObjectMetadataSystemTable.cs", "tNavEnvironment", "GetProperty", "\"Instance\"", 1),
        // RecordPatches.PageTriggerMetadata.cs — 1
        ("AlRunner/Patches/RecordPatches.PageTriggerMetadata.cs", "objId.GetType()", "GetProperty", "\"ObjectType\"", 1),
        // RecordPatches.PageControlFieldFromBcDocument.cs — 5
        //
        // #3659, merged the same day this ratchet landed, and the reason the guard exists.
        // GetMetaFieldEditable walks five reflection hops to BC's original Types.Metadata.
        // MetaField and answers `true` — EDITABLE — on every one of them failing. That is not a
        // neutral default: it is a plausible invented answer a caller cannot tell from BC
        // genuinely saying editable. Recorded, not converted: the defaulting rule is the
        // method's stated design and changing it is that method's own decision, not this
        // ratchet's. See docs/silent-reflection-lookup-ratchet.md#the-first-thing-it-caught.
        new("AlRunner/Patches/RecordPatches.PageControlFieldFromBcDocument.cs", "meta.GetType()", "GetField", "\"metadataAppGroupMetaTable\"", 1),
        new("AlRunner/Patches/RecordPatches.PageControlFieldFromBcDocument.cs", "metaTable?.GetType()", "GetProperty", "\"Fields\"", 1),
        new("AlRunner/Patches/RecordPatches.PageControlFieldFromBcDocument.cs", "original?.GetType()", "GetProperty", "\"Item\"", 1),
        new("AlRunner/Patches/RecordPatches.PageControlFieldFromBcDocument.cs", "t", "GetProperty", "\"Editable\"", 1),
        new("AlRunner/Patches/RecordPatches.PageControlFieldFromBcDocument.cs", "t", "GetProperty", "\"Id\"", 1),
        // RecordPatches.QueryJoin.cs — 2
        ("AlRunner/Patches/RecordPatches.QueryJoin.cs", "t", "GetField", "member", 1),
        ("AlRunner/Patches/RecordPatches.QueryJoin.cs", "t", "GetProperty", "member", 1),
        // RecordPatches.QueryProjection.cs — 3
        ("AlRunner/Patches/RecordPatches.QueryProjection.cs", "t", "GetField", "member", 1),
        ("AlRunner/Patches/RecordPatches.QueryProjection.cs", "t", "GetProperty", "member", 1),
        ("AlRunner/Patches/RecordPatches.QueryProjection.cs", "t", "GetProperty", "n", 1),
        // RecordPatches.RecordLinkTable.cs — 3
        ("AlRunner/Patches/RecordPatches.RecordLinkTable.cs", "provider.GetType()", "GetField", "\"primaryTree\"", 1),
        ("AlRunner/Patches/RecordPatches.RecordLinkTable.cs", "provider.GetType()", "GetField", "\"table\"", 1),
        ("AlRunner/Patches/RecordPatches.RecordLinkTable.cs", "table.GetType()", "GetProperty", "\"SystemIdField\"", 1),
        // RecordPatches.SessionVirtualTable.cs — 2
        ("AlRunner/Patches/RecordPatches.SessionVirtualTable.cs", "auth?.GetType()", "GetProperty", "\"AuthenticationMethod\"", 1),
        ("AlRunner/Patches/RecordPatches.SessionVirtualTable.cs", "session.GetType()", "GetProperty", "\"Authenticator\"", 1),
        // RecordPatches.TableTriggerMetadata.cs — 1
        ("AlRunner/Patches/RecordPatches.TableTriggerMetadata.cs", "t", "GetProperty", "\"DefinedTriggers\"", 1),
        // RecordPatches.UserSystemTable.cs — 8
        ("AlRunner/Patches/RecordPatches.UserSystemTable.cs", "auth?.GetType()", "GetField", "\"navUser\"", 2),
        ("AlRunner/Patches/RecordPatches.UserSystemTable.cs", "navUser.GetType()", "GetField", "\"fullName\"", 1),
        ("AlRunner/Patches/RecordPatches.UserSystemTable.cs", "navUser.GetType()", "GetField", "\"userGuid\"", 1),
        ("AlRunner/Patches/RecordPatches.UserSystemTable.cs", "navUser.GetType()", "GetField", "\"userName\"", 1),
        ("AlRunner/Patches/RecordPatches.UserSystemTable.cs", "navUser?.GetType()", "GetField", "\"userGuid\"", 1),
        ("AlRunner/Patches/RecordPatches.UserSystemTable.cs", "session.GetType()", "GetProperty", "\"Authenticator\"", 2),
        // RecordPatches.WindowsLanguageVirtualTable.cs — 2
        ("AlRunner/Patches/RecordPatches.WindowsLanguageVirtualTable.cs", "helper", "GetField", "\"AllCultures\"", 1),
        ("AlRunner/Patches/RecordPatches.WindowsLanguageVirtualTable.cs", "helper", "GetProperty", "\"AllCultures\"", 1),
        // RecordPatches.cs — 5
        ("AlRunner/Patches/RecordPatches.cs", "self?.GetType().BaseType?", "GetField", "\"database\"", 1),
        ("AlRunner/Patches/RecordPatches.cs", "tSqlDbProps", "GetField", "\"applicationFamily\"", 1),
        ("AlRunner/Patches/RecordPatches.cs", "tSqlDbProps", "GetField", "\"databasePropertiesReady\"", 1),
        ("AlRunner/Patches/RecordPatches.cs", "tSqlDbProps", "GetField", "\"invalidIdentifierChars\"", 1),
        ("AlRunner/Patches/RecordPatches.cs", "tSqlDbProps", "GetField", "\"lockObj\"", 1),
        // RecordWritePatches.cs — 3
        ("AlRunner/Patches/RecordWritePatches.cs", "asTask?.GetType()", "GetProperty", "\"Result\"", 1),
        ("AlRunner/Patches/RecordWritePatches.cs", "metaTable.GetType()", "GetProperty", "\"TableType\"", 1),
        ("AlRunner/Patches/RecordWritePatches.cs", "request.GetType()", "GetProperty", "propName", 1),
        // RunnerModalDispatch.cs — 4
        ("AlRunner/Patches/RunnerModalDispatch.cs", "company?.GetType()", "GetMethod", "\"GetRegisteredForm\"", 1),
        ("AlRunner/Patches/RunnerModalDispatch.cs", "form?.GetType()", "GetProperty", "\"ObjectId\"", 1),
        ("AlRunner/Patches/RunnerModalDispatch.cs", "objectId?.GetType()", "GetProperty", "\"ObjectNumber\"", 1),
        ("AlRunner/Patches/RunnerModalDispatch.cs", "session?.GetType()", "GetProperty", "\"Company\"", 1),
        // RunnerPageInstance.cs — 2
        ("AlRunner/Patches/RunnerPageInstance.cs", "session?.GetType()", "GetProperty", "\"TestExecution\"", 1),
        ("AlRunner/Patches/RunnerPageInstance.cs", "type", "GetProperty", "\"Result\"", 1),
        // RunnerTestClientSession.cs — 1
        ("AlRunner/Patches/RunnerTestClientSession.cs", "company?.GetType()", "GetMethod", "\"GetRegisteredForm\"", 1),
        // SessionPatches.cs — 5
        ("AlRunner/Patches/SessionPatches.cs", "setting?.GetType()", "GetProperty", "\"Value\"", 1),
        ("AlRunner/Patches/SessionPatches.cs", "tAppGroup?", "GetProperty", "\"BaseGroup\"", 1),
        ("AlRunner/Patches/SessionPatches.cs", "tAppGroup?", "GetField", "\"BaseGroup\"", 1),
        ("AlRunner/Patches/SessionPatches.cs", "tSettings!", "GetProperty", "\"DefaultApplicationId\"", 1),
        ("AlRunner/Patches/SessionPatches.cs", "tSettings?", "GetProperty", "\"Instance\"", 1),
        // XmlPortPatches.cs — 2
        ("AlRunner/Patches/XmlPortPatches.cs", "meta.GetType()", "GetProperty", "propName", 1),
        ("AlRunner/Patches/XmlPortPatches.cs", "objId?.GetType()", "GetProperty", "\"ObjectNumber\"", 1),
    }.ToDictionary(e => new SiteKey(e.File, e.Recv, e.Kind, e.Member), e => e.Count);

    // ── Arming the ratchet ──────────────────────────────────────────────────────────────
    //
    // A ratchet nobody has seen fail is not a ratchet. These drive the classifier over
    // synthetic sources rather than over the tree, so the arms prove the MECHANISM without
    // needing a real regression committed to prove it.

    /// <summary>
    /// The armed direction: a newly-written silent lookup is found. Each of the three silent
    /// spellings is driven separately, because a scanner that recognised only <c>?.</c> would
    /// still pass an arm that only tested <c>?.</c>.
    /// </summary>
    [Theory]
    [InlineData("var v = t.GetProperty(\"X\", Flags)?.GetValue(o);", "?. absorbs the null")]
    [InlineData("var v = t?.GetType().GetProperty(\"X\", Flags).GetValue(o);", "null-conditional receiver")]
    [InlineData("var p = t.GetProperty(\"X\", Flags) ?? Fallback;", "?? yields an ordinary default")]
    public void ANewSilentLookup_TripsTheScanner(string statement, string why)
    {
        var found = Scan("f.cs", Wrap(statement)).Where(IsSilent).ToList();

        Assert.True(found.Count == 1,
            $"the scanner must see a silent lookup written as `{statement}` ({why}); found {found.Count}");
        Assert.Equal("GetProperty", found[0].Kind);
    }

    /// <summary>
    /// The other half of armed: the SAME site, made loud, is not counted. Without this the
    /// theory above would also pass a scanner that flagged every lookup unconditionally —
    /// which would be maximally "armed" and completely useless.
    /// <para>Both loud spellings were driven end-to-end against a real site in
    /// <c>BlobStoreIsolationPatches</c> while building this: silent failed both tree
    /// assertions naming the file and member, `?? throw` passed, and
    /// <c>BcShape.Property</c> passed. See docs/silent-reflection-lookup-ratchet.md.</para>
    /// </summary>
    [Theory]
    [InlineData("var p = t.GetProperty(\"X\", Flags) ?? throw new InvalidOperationException(\"gone\");",
                "?? throw — the commonest loud shape, 105 sites in the tree")]
    [InlineData("var p = BcShape.Property(t, \"X\", Flags, Surface);",
                "converted to the throwing accessor — not a bare lookup at all")]
    [InlineData("var p = t.GetProperty(\"X\", Flags)!;",
                "null-forgiving — BcInternalsNullForgivingGuardTests' population, not this one")]
    [InlineData("var v = t.GetProperty(\"X\", Flags).GetValue(o);",
                "dereferenced — a failed lookup NREs rather than answering")]
    public void TheSameSiteMadeLoud_IsNotCounted(string statement, string why)
    {
        Assert.Empty(Scan("f.cs", Wrap(statement)).Where(IsSilent));
    }

    /// <summary>
    /// The `??` disambiguation, which is the single correction that moved the measured
    /// population most. `?? throw` is loud and `?? <value>` is silent, and a scanner reading
    /// only the `??` token cannot tell them apart — reading it as silent is how #3663's own
    /// window classifier reported 339 where the local reading is 120.
    /// </summary>
    [Fact]
    public void NullCoalescing_IsSplitByWhatFollowsIt_NotByTheTokenItself()
    {
        const string src = """
            class C {
                void M() {
                    var loud    = t.GetProperty("A", F) ?? throw new InvalidOperationException("x");
                    var silent  = t.GetProperty("B", F) ?? SomeDefault;
                    var chained = t.GetProperty("C", F) ?? t.GetProperty("CLegacy", F);
                }
            }
            """;

        var silent = Scan("f.cs", src).Where(IsSilent).Select(s => Trim(s.Args)).ToList();

        Assert.Equal(new[] { "\"B\", F" }, silent);
    }

    /// <summary>
    /// #3664's real shape, and a design consequence worth pinning rather than only describing:
    /// a genuine conversion can land WITHOUT moving the count. <c>StaticMember</c> probes two
    /// spellings of one BC static and refuses the pair on the following statement, so the code
    /// is loud while both lookups still read as silent — each individually ends in a
    /// <c>?.</c>, and this classifier reads only the expression.
    /// <para>Pinned so that a future reader who sees a conversion land with no ratchet movement
    /// finds an assertion saying that is expected, rather than concluding the guard is
    /// broken.</para>
    /// </summary>
    [Fact]
    public void ARefusalOneStatementAway_DoesNotMakeTheLookupsLoud()
    {
        const string src = """
            class C {
                object StaticMember(Type t, string member)
                {
                    var v = t.GetField(member, BcShape.AnyStatic)?.GetValue(null)
                         ?? t.GetProperty(member, BcShape.AnyStatic)?.GetValue(null);
                    return v ?? throw new BcShapeGapException(Surface, member, "moved");
                }
            }
            """;

        // Both still counted. This is the specified behaviour, not a miss: the classifier is
        // local by design, and widening it to follow `v` is the dataflow analysis this guard
        // deliberately does not attempt.
        Assert.Equal(2, Scan("f.cs", src).Count(IsSilent));
    }

    /// <summary>
    /// An alternate-member-name chain is neither loud nor silent, and must not be counted as
    /// either. It is the shape BC's own FieldNo/No rename produced, and a site asking for both
    /// names is handling exactly the failure this ratchet exists to prevent.
    /// </summary>
    [Fact]
    public void AnAlternateNameChain_IsNotSilent()
    {
        const string src = """
            class C {
                void M() {
                    _pFieldNo = tMetaField.GetProperty("FieldNo", F)
                        ?? tMetaField.GetProperty("No", F);
                }
            }
            """;

        // The FIRST lookup falls back to an alternate name, so it is not silent. The SECOND is
        // a bare trailing lookup with nothing absorbing it — also not silent by this file's
        // rule, because its null flows on to whatever the caller does next rather than being
        // swallowed at the expression.
        Assert.Empty(Scan("f.cs", src).Where(IsSilent));
    }

    /// <summary>
    /// The two excluded classes, driven as source. Both are live in AlRunner/Patches/ — 20
    /// JsonElement sites across five registry loaders and one StackFrame site in
    /// NavAppResourcePatches — so these are exclusions against real code.
    /// </summary>
    [Fact]
    public void JsonElementAndStackFrame_AreNotReflectionLookups()
    {
        const string src = """
            class C {
                void M(System.Text.Json.JsonElement arr) {
                    // JsonElement.GetProperty(string) — a JSON read that happens to share a name.
                    foreach (var e in arr.EnumerateArray())
                        Register(e.GetProperty("id").GetInt32(), e.GetProperty("xml").GetString() ?? "");

                    // StackFrame.GetMethod() — the frame's method, not a member lookup.
                    var trace = new System.Diagnostics.StackTrace(fNeedFileInfo: false);
                    var asm = trace.GetFrame(0)?.GetMethod()?.DeclaringType?.Assembly;

                    // ...and a REAL reflection lookup in the same body, so the arm cannot pass
                    // by excluding everything.
                    var v = t.GetProperty("Real", Flags)?.GetValue(o);
                }
            }
            """;

        // The real pipeline: Excluded() first, then IsSilent(). Both halves matter — the
        // StackFrame site IS silent (`?.` absorbs it) and is removed only by the exclusion, so
        // an arm that skipped Excluded() would report it and mask nothing.
        var code = Blank(src);
        var silent = Scan("f.cs", src).Where(s => !Excluded(s, code) && IsSilent(s)).ToList();

        Assert.Single(silent);
        Assert.Equal("\"Real\", Flags", Trim(silent[0].Args));

        // ...and each exclusion is doing its own work, so neither can rot away unnoticed
        // behind the other.
        var all = Scan("f.cs", src).ToList();
        Assert.Equal(4, all.Count);                                        // 2 json + 1 frame + 1 real
        Assert.Equal(3, all.Count(s => Excluded(s, code) || !IsSilent(s)));
        Assert.Contains(all, s => s.Kind == "GetMethod" && Excluded(s, code));   // StackFrame
        Assert.Equal(2, all.Count(s => s.Kind == "GetProperty" && Excluded(s, code))); // JsonElement
    }

    /// <summary>
    /// #3153's shape, and #3064's before it: a guard that scans raw text matches its own prose
    /// and then has to exclude its own source, which is a hole rather than a fix. This file
    /// describes every spelling it forbids in its header, so it would be the first casualty.
    /// <see cref="Blank"/> is why it is not — comments and string literals contribute no
    /// tokens, so the scanner can safely read anything, including itself.
    /// </summary>
    [Fact]
    public void CommentsAndStringLiterals_DescribingTheShape_AreNotSites()
    {
        const string src = """
            class C {
                // var v = t.GetProperty("InAComment", F)?.GetValue(o);
                /* var v = t.GetProperty("InABlockComment", F)?.GetValue(o); */
                void M() {
                    var s = "t.GetProperty(\"InAString\", F)?.GetValue(o)";
                    var v = t.GetProperty("Live", F)?.GetValue(o);
                }
            }
            """;

        var silent = Scan("f.cs", src).Where(IsSilent).ToList();

        Assert.Single(silent);
        Assert.Equal("\"Live\", F", Trim(silent[0].Args));
    }

    /// <summary>
    /// The scanner's own edge cases, kept separate from the semantic arms above: a wrapped
    /// call still registers, <c>GetProperties</c> is a different method and must not, and
    /// <c>!=</c> is not a null-forgiving <c>!</c>.
    /// </summary>
    [Fact]
    public void Scanner_HandlesWrappedCalls_PluralOverloads_AndNotEquals()
    {
        const string src = """
            class C {
                void M() {
                    var a = t.GetProperty("Wrapped",
                        BindingFlags.Public)?.GetValue(o);
                    var b = t.GetProperties(BindingFlags.Public);
                    if (t.GetProperty("NotEquals", F) != null) return;
                }
            }
            """;

        var all = Scan("f.cs", src).ToList();

        Assert.Equal(2, all.Count);                                    // GetProperties excluded
        Assert.DoesNotContain(all, s => Trim(s.Args).StartsWith("BindingFlags", StringComparison.Ordinal));
        var silent = all.Where(IsSilent).ToList();
        Assert.Single(silent);
        Assert.Equal("\"Wrapped\", BindingFlags.Public", Trim(silent[0].Args));
    }

    private static string Wrap(string statement) =>
        "class C {\n    void M() {\n        " + statement + "\n    }\n}\n";

    // ── The classifier ──────────────────────────────────────────────────────────────────

    private sealed record Site(string File, int Line, string Kind, string Recv, string Args, string Tail);

    private static readonly string[] Kinds =
        { "GetProperty", "GetMethod", "GetField", "GetConstructor", "GetNestedType" };

    /// <summary>
    /// True when a failed lookup at <paramref name="s"/> is ABSORBED at the expression — the
    /// null becomes an ordinary value that flows on, so the caller cannot tell "BC moved the
    /// member" from "the answer is legitimately empty". See this file's header for the full
    /// table, including the two shapes that are deliberately neither.
    /// </summary>
    private static bool IsSilent(Site s)
    {
        if (s.Tail.StartsWith("?.", StringComparison.Ordinal)) return true;

        if (s.Tail.StartsWith("??", StringComparison.Ordinal)
            && !s.Tail.StartsWith("??=", StringComparison.Ordinal))
        {
            var rest = s.Tail.Substring(2).TrimStart();
            // `?? throw ...` refuses; `?? x.GetProperty(...)` asks for an alternate member
            // NAME. Neither swallows the failure, so neither is this population.
            if (rest.StartsWith("throw", StringComparison.Ordinal)) return false;
            if (AlternateLookup.IsMatch(rest)) return false;
            return true;
        }

        // A null-conditional anywhere in the receiver chain absorbs the whole expression:
        // `objId?.GetType().GetProperty(...)` answers null when objId is null, and the lookup's
        // own failure lands in the same place.
        return s.Recv.Contains("?.", StringComparison.Ordinal);
    }

    private static readonly Regex AlternateLookup = new(
        @"^[\w\.\(\)\[\]!\?]*\.(GetProperty|GetMethod|GetField|GetConstructor|GetNestedType)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>JsonElement.GetProperty(string)</c> and <c>StackFrame.GetMethod()</c> share a name
    /// with a reflection lookup and are not one. Discriminated on ARGUMENT SHAPE first — a
    /// reflection lookup in this tree passes <c>BindingFlags</c> or a type array, and neither
    /// of these ever does — then on how the receiver was bound. Inferring from the receiver's
    /// NAME alone was tried and is not sound: the JSON sites bind their receiver in a
    /// <c>foreach</c> over <c>EnumerateArray()</c>, so there is no declaration to read.
    /// </summary>
    private static bool Excluded(Site s, string code)
    {
        var recv = s.Recv.TrimEnd('!', '?');

        if (s.Kind == "GetProperty")
        {
            var a = Trim(s.Args);
            var loneStringLiteral = a.Length > 1 && a[0] == '"' && a[^1] == '"'
                                    && a.Count(c => c == '"') == 2;
            if (loneStringLiteral && !recv.Contains('.') && !recv.Contains('('))
            {
                if (Regex.IsMatch(code,
                        @"foreach\s*\(\s*var\s+" + Regex.Escape(recv) + @"\s+in\s+[^)]*Enumerate(Array|Object)\s*\("))
                    return true;
                if (Regex.IsMatch(code, @"\bJsonElement[\?\s]+" + Regex.Escape(recv) + @"\b"))
                    return true;
            }
        }

        if (s.Kind == "GetMethod" && Trim(s.Args).Length == 0
            && Regex.IsMatch(recv, @"GetFrame\s*\(|StackFrame"))
            return true;

        return false;
    }

    // ── Walking the tree ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<Site>? _silent;

    private static IReadOnlyList<Site> SilentSites()
    {
        if (_silent != null) return _silent;
        var found = new List<Site>();
        foreach (var path in SourceFiles())
        {
            var rel = Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
            var src = File.ReadAllText(path);
            var code = Blank(src);
            found.AddRange(Scan(rel, src).Where(s => !Excluded(s, code) && IsSilent(s)));
        }
        return _silent = found;
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = Path.Combine(RepoRoot, "AlRunner", "Patches");
        Assert.True(Directory.Exists(root), $"AlRunner/Patches/ not found at {root} — the repo root walk is wrong.");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    /// <summary>
    /// The allowlist key. The LINE NUMBER is deliberately not part of it: it goes stale on the
    /// next edit above the site, which would make this guard fail on unrelated changes.
    /// </summary>
    private static SiteKey Key(Site s) => new(s.File, s.Recv, s.Kind, FirstArg(s.Args, s.Kind));

    /// <summary>The first lookup argument — the member NAME, which is the part that identifies
    /// the site. The <c>BindingFlags</c> that follow are long, and often a named constant whose
    /// spelling changes without the site changing.</summary>
    private static string FirstArg(string args, string kind)
    {
        var a = Trim(args);
        if (kind == "GetConstructor") return a;
        var depth = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var c = a[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) return a.Substring(0, i);
        }
        return a;
    }

    /// <summary>Sites in <paramref name="sites"/> beyond what KnownSites accounts for, with
    /// their line numbers, for the failure message.</summary>
    private static IEnumerable<string> Unaccounted(IEnumerable<Site> sites)
    {
        var budget = new Dictionary<SiteKey, int>(KnownSites);
        foreach (var s in sites.OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line))
        {
            var k = Key(s);
            if (budget.TryGetValue(k, out var left) && left > 0) { budget[k] = left - 1; continue; }
            yield return $"{s.File}:{s.Line}  {s.Recv}.{s.Kind}({Trim(s.Args)})";
        }
    }

    private static string Describe(SiteKey k) => $"{k.File}  {k.Recv}.{k.Kind}({k.Member})";

    /// <summary>
    /// Every <c>GetProperty</c> / <c>GetMethod</c> / <c>GetField</c> / <c>GetConstructor</c> /
    /// <c>GetNestedType</c> call in <paramref name="src"/>, with its receiver, argument text
    /// and the token immediately following the closing paren. Comments and string literals are
    /// blanked first, so this file's own header — which spells out every shape it forbids —
    /// is prose rather than a population of sites (#3153, #3064).
    /// </summary>
    private static IEnumerable<Site> Scan(string file, string src)
    {
        var code = Blank(src);
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] != '.') continue;
            var kind = Kinds.FirstOrDefault(k => string.CompareOrdinal(code, i + 1, k, 0, k.Length) == 0);
            if (kind == null) continue;
            var j = i + 1 + kind.Length;
            // GetProperties / GetMethods / GetFields / ... — a different method returning an
            // array, whose empty result is an answer rather than a failed lookup.
            if (j < code.Length && code[j] == 's') continue;
            if (j < code.Length && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) continue;
            while (j < code.Length && char.IsWhiteSpace(code[j])) j++;
            if (j >= code.Length || code[j] != '(') continue;
            var close = MatchClose(code, j);
            if (close < 0) continue;

            var k = close + 1;
            while (k < code.Length && char.IsWhiteSpace(code[k])) k++;

            // The argument text comes from the ORIGINAL source: Blank() erases string bodies,
            // and the member name is exactly what lives inside them.
            yield return new Site(
                file,
                code.Take(i).Count(c => c == '\n') + 1,
                kind,
                Receiver(code, src, i),
                src.Substring(j + 1, close - j - 1),
                Trim(code.Substring(k, Math.Min(90, code.Length - k))));
            i = close;
        }
    }

    /// <summary>Whitespace-normalised, so a wrapped call and a one-liner read alike.</summary>
    private static string Trim(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Receiver(string code, string src, int dot)
    {
        var i = dot - 1;
        while (i >= 0 && char.IsWhiteSpace(code[i])) i--;
        var end = i + 1;
        while (i >= 0)
        {
            var c = code[i];
            if (c is ')' or ']')
            {
                var open = c == ')' ? '(' : '[';
                var depth = 0;
                while (i >= 0)
                {
                    if (code[i] == c) depth++;
                    else if (code[i] == open) { depth--; if (depth == 0) break; }
                    i--;
                }
                i--;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '.' or '!' or '?') { i--; continue; }
            if (char.IsWhiteSpace(c))
            {
                var j = i;
                while (j >= 0 && char.IsWhiteSpace(code[j])) j--;
                if (j >= 0 && code[j] == '.') { i = j; continue; }
                break;
            }
            break;
        }
        return Trim(src.Substring(i + 1, end - i - 1));
    }

    private static int MatchClose(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>
    /// <paramref name="src"/> with comment and string-literal CONTENT replaced by spaces, same
    /// length so every offset still lines up with the original. Adapted from
    /// <c>BcInternalsNullForgivingGuardTests</c>, which needs it for the same reason.
    /// </summary>
    private static string Blank(string src)
    {
        var b = new StringBuilder(src);
        var i = 0;
        while (i < src.Length)
        {
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') { b[i] = ' '; i++; }
                continue;
            }
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                while (i < src.Length && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/'))
                { if (src[i] != '\n') b[i] = ' '; i++; }
                if (i < src.Length) { b[i] = ' '; b[i + 1] = ' '; i += 2; }
                continue;
            }
            if (src[i] == '"' && i >= 2 && src[i - 1] == '"' && src[i - 2] == '"')
            {
                var endFence = src.IndexOf("\"\"\"", i + 1, StringComparison.Ordinal);
                var stop = endFence < 0 ? src.Length : endFence + 3;
                for (; i < stop; i++) if (src[i] != '\n') b[i] = ' ';
                continue;
            }
            if (src[i] == '@' && i + 1 < src.Length && src[i + 1] == '"')
            {
                b[i] = ' '; i += 2;
                while (i < src.Length)
                {
                    if (src[i] == '"' && i + 1 < src.Length && src[i + 1] == '"') { b[i] = ' '; b[i + 1] = ' '; i += 2; continue; }
                    if (src[i] == '"') { b[i] = ' '; i++; break; }
                    if (src[i] != '\n') b[i] = ' ';
                    i++;
                }
                continue;
            }
            if (src[i] == '"')
            {
                i++;
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\\') { b[i] = ' '; i++; if (i < src.Length) { b[i] = ' '; i++; } continue; }
                    b[i] = ' '; i++;
                }
                if (i < src.Length) i++;
                continue;
            }
            if (src[i] == '\'')
            {
                i++;
                while (i < src.Length && src[i] != '\'')
                {
                    if (src[i] == '\\') { b[i] = ' '; i++; if (i < src.Length) { b[i] = ' '; i++; } continue; }
                    b[i] = ' '; i++;
                }
                if (i < src.Length) i++;
                continue;
            }
            i++;
        }
        return b.ToString();
    }
}
