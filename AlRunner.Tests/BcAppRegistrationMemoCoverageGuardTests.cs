using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Guards the OTHER half of #2888, the half
/// <c>BcAppRegistrationEpochInvalidationTests.NoGenerationKeyUsesTheAppPathCount</c> cannot
/// see: a memo built from the registered-.app set with <b>no generation term at all</b>.
///
/// That guard's first statement is
/// <c>if (!line.Contains("_bcAppPaths.Count")) continue;</c>, so it only ever examines lines
/// that already mention the WRONG key. A memo with no key is invisible to it, and that is the
/// strictly worse defect: a wrong key invalidates at the wrong moment, a missing key never
/// invalidates at all. #4225 is the measured instance —
/// <c>_aovDependencyTableTypes</c> was populated once, cleared by nothing, and
/// <c>AllObjWithCaption</c> reported a stale table subtype after every warm reload while that
/// guard ran green throughout. #4222 and #4137 are two more of the same family.
///
/// <para>Per <c>.claude/rules/guards-need-a-third-state.md</c>, the old guard answered "no
/// offenders" about a file it never inspected: the memo was not <i>fine</i>, it was
/// <i>unexamined</i>.</para>
///
/// What makes this checkable rather than a noisy sweep
/// ---------------------------------------------------
/// "A static mutable memo that should be epoch-keyed" is not syntactically distinguishable
/// from a static field that is legitimately process-lifetime (a compiled Regex, a MethodInfo
/// bind, an immutable lookup). A sweep over every <c>private static … Dictionary</c> would be
/// noise, and a noisy guard gets suppressed — its own failure mode.
///
/// So this keys on the REACH, not on the field's type. The reach is
/// <c>RecordPatches.EnumerateRegisteredBcAppSymbols</c> (RecordPatches.DependencyAppSymbolWalk.cs),
/// the walk that parses each registered .app's symbol file. A static field ASSIGNED inside a
/// method that reaches it is, by construction, holding state derived from the registration set —
/// which is the precondition the whole #2888 family is about. Measured on the tree this landed
/// against: <b>709</b> mutable statics under
/// <c>AlRunner/Patches</c>, of which this reach test selects <b>22</b> assignment sites, and
/// exactly one of those was an offender. That is the signal-to-noise story, and
/// <see cref="TheCandidatePopulation_StaysSmallEnoughToReadByHand"/> pins it so a future
/// widening of the reach cannot quietly turn this into the sweep it is deliberately not.
///
/// Two discharges, both mechanical, both already used on main
/// ----------------------------------------------------------
/// A candidate is reload-safe when EITHER holds:
///
///   1. Its building method reads <c>BcAppRegistrationEpoch</c> — the monotonic counter
///      <c>InvalidateBcAppIndexes</c> bumps, which cannot ABA the way a Count can. This is how
///      almost every site discharges (<c>_knownQueryIds</c>, <c>_pageMetaRows</c>,
///      <c>_reportRows</c>, <c>_tableMetadataRows</c>, …), each alongside its own
///      <c>…BuiltFrom</c> companion, which the analysis selects too and which discharges with it.
///   2. The field is dropped inside <c>InvalidateBcAppIndexes</c> itself — the funnel both
///      REGISTRATION paths end in (AddBcAppPath and ClearPerBundleBcAppPaths).
///      <c>_bcSymbolQueryIndex</c> discharges this way, with no generation key at all. Note
///      this is the inner funnel: <c>ResetForReload</c> wraps it and drops more, which this
///      guard does not read.
///
/// Anything else is either a genuine process-lifetime exempt — on
/// <see cref="AllowedProcessLifetimeStatics"/>, WITH the reason — or an offender.
///
/// What this guard does NOT cover — read this before trusting a green
/// ------------------------------------------------------------------
/// <b>This guard covers memos reachable from <see cref="RegistrationWalk"/>, discharged by the
/// two funnels above. It is not a statement that every registration-derived memo is keyed.</b>
/// A reader who concludes otherwise has made exactly the over-reading the old guard invited,
/// and the point of this section is that the boundary is stated rather than implied.
///
/// <c>EnumerateRegisteredBcAppSymbols</c> is not the only walk over <c>_bcAppPaths</c>. Measured
/// on the tree this landed against, <c>_bcAppPaths.ToArray()</c> appears at six live sites (a
/// seventh is inside a comment), and two of them are walks this guard does not model:
///
///   * <c>BuildKnownAppNameIndex</c> (RecordPatches.AggregatePermissionSetVirtualTable.cs:301)
///     builds a fresh local <c>Dictionary</c> and assigns no static, so it is correctly not a
///     candidate and has nothing to discharge.
///   * <c>EnumerateKnownPermissionSets</c> (RecordPatches.MetadataPermissionSetVirtualTable.cs:199)
///     is the one that matters. It walks <c>_bcAppPaths.ToArray()</c> directly and contains
///     <b>zero</b> references to <see cref="RegistrationWalk"/>, so nothing hanging off it is
///     selected here. Three memoized statics do hang off it, in
///     RecordPatches.PermissionMetadataPopulator.cs — <c>_permMetaPopulatedForCount</c>,
///     <c>_permissionSetIdByName</c> and <c>_permissionSetNameById</c> — and the first is a
///     genuine <c>.Count</c>-keyed latch (<c>if (known.Count == _permMetaPopulatedForCount)
///     return;</c> at line 129), the exact ABA shape #2888 is about.
///
/// <b>Those three are not live defects</b>, and the reason is a third funnel rather than luck:
/// <c>ResetPermissionSetMetadataForReload</c> (line 444 of that file) resets the latch to -1 and
/// nulls both memos, and the reload entry point in RecordPatches.cs invokes it from line 465.
/// The comment above that reset already names the ABA hazard in full — the code knows. What this
/// guard cannot see is that discharge, because that reload entry point is an OUTER funnel:
/// <see cref="ResetFunnel"/> is called from inside it, so a field dropped only by the outer one
/// is invisible to <see cref="ResetFunnelDrops"/>.
///
/// <para>(That outer entry point is deliberately not spelled here as a qualified call.
/// <c>ParserStaticsIsolationGuardTests</c> keys on that exact text to find classes driving the
/// AL parse statics, and this class drives nothing — it reads source files off disk. Writing the
/// qualified form in prose made this file fail that guard, which is #3064's lesson: a comment
/// documenting compliance must not itself read as a violation.)</para>
///
/// Modelling the outer funnel is a real extension and deliberately not attempted here: it would
/// have to follow the reset chain across files, and the narrow version earns its keep first.
///
/// <para><b>The allowlist is deliberately empty today, and that is the finding.</b> Every one
/// of the 22 selected sites discharges mechanically, so nothing needs an exemption. A first
/// pass at this guard used a regex to find the population and "found" five process-lifetime
/// exempts — two field-number binds in <c>AllProfileWritePatches</c> and three MethodInfo binds
/// in <c>RecordPatches.PageMetadataProperties</c>. Roslyn selects none of them: neither file
/// calls the walk at all, and the regex had mis-attributed the assignments to a method it
/// invented while scanning. They are recorded here rather than in the list because "the
/// allowlist started empty" is the claim a later reader should be able to check — an exemption
/// that was never needed is exactly the stale entry
/// <see cref="EveryAllowlistEntry_IsStillACandidate_SoTheListCannotGoStale"/> exists to
/// delete.</para>
/// </summary>
public sealed class BcAppRegistrationMemoCoverageGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string PatchesDir => Path.Combine(RepoRoot, "AlRunner", "Patches");

    /// <summary>The walk this guard keys its reach on: the one that parses each registered
    /// .app's symbol file. NOT the only walk over <c>_bcAppPaths</c> — see the class header's
    /// "What this guard does NOT cover".</summary>
    private const string RegistrationWalk = "EnumerateRegisteredBcAppSymbols";

    /// <summary>The monotonic generation counter <c>InvalidateBcAppIndexes</c> bumps.</summary>
    private const string EpochProperty = "BcAppRegistrationEpoch";

    /// <summary>The single reset funnel both registration paths end in.</summary>
    private const string ResetFunnel = "InvalidateBcAppIndexes";

    /// <summary>
    /// Statics that are assigned inside a method reaching the registration walk and are
    /// nonetheless legitimately process-lifetime, WITH the reason each is genuinely exempt.
    ///
    /// <para>The bar, copied from <c>BaseAppFloorFixtureGuardTests</c>: an entry is added only
    /// with the reason the field's value cannot change when the registration set changes.
    /// "It has not caused a bug yet" is not that reason — #4225 had not caused a reported bug
    /// either until someone probed for it. Every entry below holds state derived from the
    /// LOADED BC ASSEMBLIES or from a record's own metatable, neither of which a bundle
    /// reload moves.</para>
    ///
    /// <para><see cref="EveryAllowlistEntry_IsStillACandidate_SoTheListCannotGoStale"/> deletes
    /// this list's value as decoration: an entry naming a field that is no longer a candidate
    /// is stale, and a stale entry silently re-permits the next field that takes the name.</para>
    /// </summary>
    private static readonly Dictionary<string, string> AllowedProcessLifetimeStatics = new();

    // ── the analysis ─────────────────────────────────────────────────────────

    /// <summary>One static-field assignment inside a method that reaches the registration walk.</summary>
    internal sealed record Candidate(string File, string Method, string Field, bool ReadsEpoch)
    {
        public override string ToString() => $"{File}::{Method} -> {Field}";
    }

    /// <summary>
    /// Every <c>.cs</c> source under <c>AlRunner/Patches</c>. Non-vacuity is asserted at every
    /// caller rather than here, for the reason <c>BaseAppFloorFixtureGuardTests</c> gives
    /// (#3021): a scan that read nothing reports "no offenders" and looks identical to a clean
    /// tree — which is the very defect this class exists to remove.
    /// </summary>
    private static IReadOnlyList<string> PatchSources() =>
        Directory.Exists(PatchesDir)
            ? Directory.GetFiles(PatchesDir, "*.cs", SearchOption.AllDirectories)
            : Array.Empty<string>();

    /// <summary>
    /// The mutable static fields declared across the given sources. <c>readonly</c> is excluded
    /// because a readonly field cannot be reassigned, so it cannot hold a memo that survives an
    /// epoch by being rebuilt-and-kept — the shape #4225 had and the one this guard is for.
    ///
    /// <para><b>A readonly COLLECTION whose CONTENTS outlive an epoch is a real gap here, and it
    /// is not covered.</b> An earlier draft of this comment claimed such a case was "caught
    /// through the <c>.Clear()</c> branch of the funnel scan"; that is false, and
    /// <c>_parsedTables</c> (RecordPatches.cs:138) is the clearest counter-example — a
    /// <c>readonly Dictionary</c> mutated in several methods that reach
    /// <see cref="RegistrationWalk"/>, and cleared in <c>ResetForReload</c>
    /// (RecordPatches.cs:330), <b>not</b> in <see cref="ResetFunnel"/>, whose drop set is exactly
    /// the eight fields <see cref="ResetFunnelDrops"/> reads out of it. So the <c>.Clear()</c>
    /// branch would not see it even if the field were selected, which it is not.</para>
    ///
    /// <para>Same root cause as the outer-funnel gap in this class's header: the guard models one
    /// funnel, and prose that speaks as though it modelled all of them is the thing to avoid.
    /// Recorded rather than quietly fixed, because widening to readonly collections means
    /// selecting on mutation sites rather than assignments — a different analysis, not a flag.</para>
    /// </summary>
    internal static HashSet<string> MutableStaticFields(IEnumerable<(string Name, string Text)> sources)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in sources)
        {
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                var mods = decl.Modifiers;
                if (!mods.Any(SyntaxKind.StaticKeyword)) continue;
                if (mods.Any(SyntaxKind.ReadOnlyKeyword) || mods.Any(SyntaxKind.ConstKeyword)) continue;
                foreach (var v in decl.Declaration.Variables) fields.Add(v.Identifier.ValueText);
            }
        }

        return fields;
    }

    /// <summary>
    /// Every field the reset funnel drops — assigned <c>null</c>/<c>false</c>/<c>default</c>, or
    /// <c>.Clear()</c>ed, inside <see cref="ResetFunnel"/>. This is discharge 2: a field the
    /// funnel drops cannot outlive a registration change, whatever its building method keys on.
    ///
    /// <para>Read from the funnel's own body rather than from a hand-kept list, so the two
    /// cannot drift — the same reasoning #2478 gave for routing both registration paths through
    /// one method instead of duplicating the resets at each call site.</para>
    /// </summary>
    internal static HashSet<string> ResetFunnelDrops(IEnumerable<(string Name, string Text)> sources)
    {
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in sources)
        {
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (m.Identifier.ValueText != ResetFunnel) continue;
                if (m.Body is null) continue;

                foreach (var a in m.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    if (a.Left is IdentifierNameSyntax id) dropped.Add(id.Identifier.ValueText);

                // `_field.Clear()` — a readonly collection emptied rather than reassigned.
                foreach (var inv in m.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    if (inv.Expression is MemberAccessExpressionSyntax ma
                        && ma.Name.Identifier.ValueText == "Clear"
                        && ma.Expression is IdentifierNameSyntax target)
                        dropped.Add(target.Identifier.ValueText);
            }
        }

        return dropped;
    }

    /// <summary>
    /// The candidate population: every static-field assignment inside a method that reaches
    /// <see cref="RegistrationWalk"/> directly or through one intermediate method.
    ///
    /// <para><b>One hop, deliberately.</b> One hop covers the shape every known instance has: a
    /// lazy memo whose builder calls an enumerator that walks the registered .apps (#4225's
    /// <c>DependencyTableTypeName</c> -> <c>EnumerateBcAppTableSymbols</c> -> the walk). A memo
    /// two hops out is not caught here — a stated limit, not an oversight.</para>
    ///
    /// <para><b>The honest cost, measured with this same analysis rather than argued: two hops
    /// selects 25 sites against one hop's 22.</b> An earlier draft claimed a closure would pull
    /// in "most of <c>AlRunner/Patches</c>" and select a population nobody can read; that is not
    /// what the measurement says, and the extra three are cheap. One hop is still the choice —
    /// it is the smallest reach that covers every instance on record, and each widening has to
    /// earn the allowlist entries it may force — but the argument for it is "three more sites
    /// buy no known coverage", not "the alternative is unreadable". Widen it if a two-hop
    /// instance ever turns up; the ceiling in
    /// <see cref="TheCandidatePopulation_StaysSmallEnoughToReadByHand"/> has room.</para>
    /// </summary>
    internal static IReadOnlyList<Candidate> Candidates(IEnumerable<(string Name, string Text)> sources)
    {
        var parsed = sources
            .Select(s => (s.Name, Root: CSharpSyntaxTree.ParseText(s.Text).GetRoot()))
            .ToList();

        var fields = MutableStaticFields(parsed.Select(p => (p.Name, p.Root.ToFullString())));

        var methods = new List<(string File, string Name, MethodDeclarationSyntax Node)>();
        foreach (var (name, root) in parsed)
            foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                methods.Add((Path.GetFileName(name), m.Identifier.ValueText, m));

        // Level 0: methods invoking the walk by name.
        var direct = new HashSet<string>(
            methods.Where(m => Invokes(m.Node, RegistrationWalk)).Select(m => m.Name),
            StringComparer.Ordinal);

        // Level 1: methods invoking one of those.
        var reaching = new HashSet<string>(direct, StringComparer.Ordinal);
        foreach (var m in methods)
            if (!reaching.Contains(m.Name) && direct.Any(d => Invokes(m.Node, d)))
                reaching.Add(m.Name);

        var candidates = new List<Candidate>();
        foreach (var m in methods)
        {
            if (!reaching.Contains(m.Name)) continue;
            var body = (SyntaxNode?)m.Node.Body ?? m.Node.ExpressionBody;
            if (body is null) continue;

            var readsEpoch = body.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == EpochProperty);

            foreach (var a in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (a.Left is not IdentifierNameSyntax id) continue;
                var field = id.Identifier.ValueText;
                if (!fields.Contains(field)) continue;
                candidates.Add(new Candidate(m.File, m.Name, field, readsEpoch));
            }
        }

        return candidates.Distinct().OrderBy(c => c.File, StringComparer.Ordinal)
            .ThenBy(c => c.Field, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// True when <paramref name="method"/> invokes <paramref name="callee"/> by simple name.
    /// Matching is on the invocation's NAME NODE, never on the body's raw text, so a callee
    /// merely named in a comment or a string contributes nothing — the distinction #3064 had
    /// to retrofit onto the raw-text scan in <c>BaseAppFloorFixtureGuardTests</c>.
    /// </summary>
    private static bool Invokes(MethodDeclarationSyntax method, string callee)
    {
        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body is null) return false;

        foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = inv.Expression switch
            {
                IdentifierNameSyntax i => i.Identifier.ValueText,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                GenericNameSyntax g => g.Identifier.ValueText,
                _ => null,
            };
            if (name == callee) return true;
        }

        return false;
    }

    /// <summary>
    /// The verdict for one candidate: null when it is reload-safe, otherwise the reason it is
    /// not. Split out from the scanning fact so the synthetic fixtures below can drive it on
    /// content that is not checked in.
    /// </summary>
    internal static string? OffenceOf(
        Candidate c, IReadOnlyCollection<string> funnelDrops, IReadOnlyDictionary<string, string> allowed)
    {
        if (c.ReadsEpoch) return null;                       // discharge 1: a generation key
        if (funnelDrops.Contains(c.Field)) return null;      // discharge 2: the reset funnel
        if (allowed.ContainsKey(c.Field)) return null;       // a declared process-lifetime exempt
        return $"{c}: built from the registered .app set, but its builder reads no "
            + $"{EpochProperty} and {ResetFunnel} does not drop it";
    }

    private static IReadOnlyList<(string Name, string Text)> CheckedInSources()
    {
        var sources = PatchSources().Select(p => (Name: p, Text: File.ReadAllText(p))).ToList();

        Assert.True(sources.Count > 0,
            $"expected .cs sources under {PatchesDir}, found none — this guard is reading nothing, "
            + "so a memo with no generation key would pass unseen, which is the exact defect (#4227) "
            + "it exists to remove.");

        return sources;
    }

    // ── the facts ────────────────────────────────────────────────────────────

    /// <summary>
    /// The scanning fact. Every memo built from the registration walk either keys on the epoch,
    /// is dropped by the reset funnel, or is on the allowlist with its reason.
    /// </summary>
    [Fact]
    public void EveryMemoBuiltFromTheRegistrationWalk_IsEpochKeyedOrDroppedOrDeclaredExempt()
    {
        var sources = CheckedInSources();
        var candidates = Candidates(sources);
        var drops = ResetFunnelDrops(sources);

        Assert.True(candidates.Count > 0,
            "the reach analysis selected no candidates at all — a guard that examines nothing "
            + $"reports 'no offenders' for the same reason a clean tree does. Did {RegistrationWalk} "
            + "get renamed, or did it stop being the walk that parses registered .app symbols?");

        Assert.True(drops.Count > 0,
            $"{ResetFunnel} was found to drop nothing — discharge 2 is then dead and every field "
            + "relying on it reads as an offender. Did the funnel get renamed or split?");

        var offenders = candidates
            .Select(c => OffenceOf(c, drops, AllowedProcessLifetimeStatics))
            .Where(o => o != null)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These statics hold state derived from the registered .app set with NO generation "
            + "term and no reset (#4227, the blind spot #4225 fell through):\n  "
            + string.Join("\n  ", offenders)
            + $"\n\nGive the builder a generation key on {EpochProperty} (never on _bcAppPaths.Count "
            + $"— it ABAs, #2888), or drop the field in {ResetFunnel}. If the value genuinely cannot "
            + "change when the registration set changes, add it to AllowedProcessLifetimeStatics in "
            + "this file WITH that reason.");
    }

    /// <summary>
    /// The negative direction, and what stops the allowlist becoming decoration: an entry
    /// naming a field that is no longer a candidate is stale, and a stale entry silently
    /// re-permits the next field that takes the name. Same contract as
    /// <c>BaseAppFloorFixtureGuardTests.EveryAllowlistEntry_StillDeclaresTheFloor_SoTheListCannotGoStale</c>.
    /// </summary>
    [Fact]
    public void EveryAllowlistEntry_IsStillACandidate_SoTheListCannotGoStale()
    {
        var candidates = Candidates(CheckedInSources());
        var live = candidates.Select(c => c.Field).ToHashSet(StringComparer.Ordinal);

        var stale = AllowedProcessLifetimeStatics
            .Where(e => !live.Contains(e.Key))
            .Select(e => $"{e.Key} ({e.Value})")
            .ToList();

        Assert.True(stale.Count == 0,
            "These allowlist entries no longer name a static assigned inside a method reaching "
            + $"{RegistrationWalk}. Delete them — a stale entry silently permits the next field "
            + "that takes the name:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// The signal-to-noise claim, pinned rather than asserted in prose. A guard whose candidate
    /// population grows without bound becomes the noisy sweep this design rejects, and the way
    /// that happens is silent: someone widens the reach, the population triples, and the
    /// allowlist absorbs the difference one entry at a time until nobody reads it.
    ///
    /// <para>Measured when this landed: <b>709</b> mutable statics under <c>AlRunner/Patches</c>,
    /// of which the reach test selects <b>22</b> assignment sites — a population small enough to
    /// read by hand, which is the property that makes the allowlist meaningful. The ceiling is
    /// deliberately loose (it must not fail for an honest new memo) but far below the 709 a
    /// type-keyed sweep would select.</para>
    /// </summary>
    [Fact]
    public void TheCandidatePopulation_StaysSmallEnoughToReadByHand()
    {
        var sources = CheckedInSources();
        var candidates = Candidates(sources);
        var allStatics = MutableStaticFields(sources);

        Assert.True(allStatics.Count > 100,
            $"expected the mutable-static population under {PatchesDir} to be in the hundreds; "
            + $"found {allStatics.Count}. A collapse here means the declaration scan stopped "
            + "matching, and the ratio below would then be meaningless.");

        Console.Error.WriteLine(
            $"[memo-coverage] {candidates.Count} candidate site(s) out of {allStatics.Count} mutable "
            + "statics: " + string.Join(", ", candidates.Select(c => $"{c.Field}{(c.ReadsEpoch ? "(epoch)" : "")}")));

        Assert.True(candidates.Count <= 40,
            $"the reach analysis now selects {candidates.Count} assignment sites (it selected 22 "
            + $"when this landed, out of {allStatics.Count} mutable statics). Past a few dozen "
            + "nobody reads the allowlist, and an unread allowlist is the noisy-sweep failure "
            + "this guard was designed to avoid. Narrow the reach rather than raising this bound:\n  "
            + string.Join("\n  ", candidates.Select(c => c.ToString())));
    }

    /// <summary>
    /// Anchors the reach analysis against reality. Every fixture below is synthetic, so without
    /// this fact they could all agree with an analysis that is wrong about what the real code
    /// looks like — the trap <c>BaseAppFloorFixtureGuardTests.ThePlaceholderExpansion_MatchesARealAllowlistedManifest</c>
    /// exists to close.
    ///
    /// <para>Two named sites, one per discharge, both read out of the checked-in tree:
    /// <c>_pageMetaRows</c> (epoch-keyed) and <c>_bcSymbolQueryIndex</c> (dropped by the reset
    /// funnel). If either stops being selected, the analysis has drifted from the code and every
    /// other fact in this class is measuring a population that no longer matches.</para>
    /// </summary>
    [Fact]
    public void TheAnalysis_SelectsTheRealSitesItClaimsTo_OneOfEachDischarge()
    {
        var sources = CheckedInSources();
        var candidates = Candidates(sources);
        var drops = ResetFunnelDrops(sources);

        var pageMetaRows = candidates.SingleOrDefault(c => c.Field == "_pageMetaRows");
        Assert.True(pageMetaRows != null,
            "_pageMetaRows is the model epoch-keyed memo over the registration walk and the "
            + "analysis no longer selects it — the reach test has drifted from the code.");
        Assert.True(pageMetaRows!.ReadsEpoch,
            $"_pageMetaRows' builder must read {EpochProperty}; it is discharge 1's reference site.");
        Assert.Null(OffenceOf(pageMetaRows, drops, AllowedProcessLifetimeStatics));

        Assert.Contains("_bcSymbolQueryIndex", drops);
        var queryIndex = candidates.SingleOrDefault(c => c.Field == "_bcSymbolQueryIndex");
        Assert.True(queryIndex != null,
            "_bcSymbolQueryIndex is discharge 2's reference site — a memo the reset funnel drops "
            + "rather than one keyed on a generation — and the analysis no longer selects it.");
        Assert.False(queryIndex!.ReadsEpoch,
            "_bcSymbolQueryIndex is meant to discharge through the reset funnel, NOT a generation "
            + "key. If it now reads the epoch it is no longer a witness for discharge 2, and this "
            + "class needs a different one — otherwise discharge 2 is untested against real code.");
        Assert.Null(OffenceOf(queryIndex, drops, AllowedProcessLifetimeStatics));
    }

    // ── what the analysis does with shapes nobody has written yet ────────────
    //
    // The facts above can only ever assert about the sources checked in today. These drive
    // Candidates/OffenceOf on synthetic content, which is the only way to show what the guard
    // does with an offender — #4225's is fixed on main, so there is no live one left to point
    // at, and a guard whose RED path is never executed is indistinguishable from one that
    // cannot fire (guards-need-a-third-state.md).

    /// <summary>The walk, plus a one-hop enumerator over it — the real shape, minimally.</summary>
    private const string WalkSource = """
        namespace P;
        internal static partial class Fake
        {
            private static System.Collections.Generic.List<string> _bcAppPaths = new();
            internal static System.Collections.Generic.IEnumerable<int> EnumerateRegisteredBcAppSymbols(string surface)
            {
                foreach (var p in _bcAppPaths) yield return p.Length;
            }
            private static System.Collections.Generic.IEnumerable<int> EnumerateFakeTableSymbols()
            {
                foreach (var t in EnumerateRegisteredBcAppSymbols("tables")) yield return t;
            }
            internal static int BcAppRegistrationEpoch => 0;
        }
        """;

    private static IReadOnlyList<(string Name, string Text)> Fixture(string memoSource, string? funnel = null) =>
        new[]
        {
            ("Walk.cs", WalkSource),
            ("Memo.cs", memoSource),
            ("Funnel.cs", funnel ?? """
                namespace P;
                internal static partial class Fake
                {
                    private static void InvalidateBcAppIndexes() { _unrelatedIndex = null; }
                    private static object? _unrelatedIndex;
                }
                """),
        };

    private static IReadOnlyList<string> OffencesIn(IReadOnlyList<(string Name, string Text)> sources)
    {
        var drops = ResetFunnelDrops(sources);
        return Candidates(sources)
            .Select(c => OffenceOf(c, drops, AllowedProcessLifetimeStatics))
            .Where(o => o != null)
            .Select(o => o!)
            .ToList();
    }

    /// <summary>
    /// The defect itself, in the exact shape #4225 had: a lazy null-checked memo whose builder
    /// walks the registered .apps, with no generation term and no reset. This is the fact that
    /// makes the whole class worth having — every other fact here is green on a tree with this
    /// offender present, which is precisely how the old guard missed it.
    /// </summary>
    [Fact]
    public void AMemoBuiltFromTheWalkWithNoGenerationKey_IsAnOffender()
    {
        var offences = OffencesIn(Fixture("""
            namespace P;
            internal static partial class Fake
            {
                private static System.Collections.Generic.Dictionary<int, string>? _memoTableTypes;
                private static string? MemoTableTypeName(int id)
                {
                    if (_memoTableTypes == null)
                    {
                        var map = new System.Collections.Generic.Dictionary<int, string>();
                        foreach (var t in EnumerateFakeTableSymbols()) map[t] = "Normal";
                        _memoTableTypes = map;
                    }
                    return _memoTableTypes.TryGetValue(id, out var n) ? n : null;
                }
            }
            """));

        Assert.Single(offences);
        Assert.Contains("_memoTableTypes", offences[0]);
        Assert.Contains(EpochProperty, offences[0]);
    }

    /// <summary>
    /// Discharge 1: the same memo, with a generation key on the epoch. Green before AND after
    /// the RED above, which is what makes that RED believable rather than an analysis that
    /// flags everything.
    /// </summary>
    [Fact]
    public void AMemoKeyedOnTheRegistrationEpoch_IsNotAnOffender() =>
        Assert.Empty(OffencesIn(Fixture("""
            namespace P;
            internal static partial class Fake
            {
                private static System.Collections.Generic.Dictionary<int, string>? _memoTableTypes;
                private static int _memoTableTypesBuiltFrom = -1;
                private static string? MemoTableTypeName(int id)
                {
                    var generation = BcAppRegistrationEpoch;
                    if (_memoTableTypes == null || _memoTableTypesBuiltFrom != generation)
                    {
                        var map = new System.Collections.Generic.Dictionary<int, string>();
                        foreach (var t in EnumerateFakeTableSymbols()) map[t] = "Normal";
                        _memoTableTypes = map;
                        _memoTableTypesBuiltFrom = generation;
                    }
                    return _memoTableTypes.TryGetValue(id, out var n) ? n : null;
                }
            }
            """)));

    /// <summary>
    /// Discharge 2: no generation key at all, but the reset funnel drops the field. This is
    /// <c>_bcSymbolQueryIndex</c>'s shape, and it must stay green — a guard that demanded a
    /// generation key from a field the funnel already drops would be asking for a second
    /// mechanism where one suffices, and the cheapest way back to green would be to add a
    /// pointless key.
    /// </summary>
    [Fact]
    public void AMemoTheResetFunnelDrops_IsNotAnOffender() =>
        Assert.Empty(OffencesIn(
            Fixture("""
                namespace P;
                internal static partial class Fake
                {
                    private static System.Collections.Generic.Dictionary<int, string>? _memoTableTypes;
                    private static string? MemoTableTypeName(int id)
                    {
                        if (_memoTableTypes == null)
                        {
                            var map = new System.Collections.Generic.Dictionary<int, string>();
                            foreach (var t in EnumerateFakeTableSymbols()) map[t] = "Normal";
                            _memoTableTypes = map;
                        }
                        return _memoTableTypes.TryGetValue(id, out var n) ? n : null;
                    }
                }
                """,
                funnel: """
                    namespace P;
                    internal static partial class Fake
                    {
                        private static void InvalidateBcAppIndexes() { _memoTableTypes = null; }
                    }
                    """)));

    /// <summary>
    /// The <c>.Clear()</c> spelling of discharge 2 — a readonly collection emptied rather than
    /// reassigned, which is how <c>_bcMissCache</c> and <c>_depPageMetadataXml</c> are dropped.
    /// An analysis reading only assignments would call these offenders and be wrong about four
    /// fields the funnel demonstrably handles.
    /// </summary>
    [Fact]
    public void AMemoTheResetFunnelClears_IsNotAnOffender() =>
        Assert.Empty(OffencesIn(
            Fixture("""
                namespace P;
                internal static partial class Fake
                {
                    private static System.Collections.Generic.Dictionary<int, string>? _memoTableTypes;
                    private static string? MemoTableTypeName(int id)
                    {
                        if (_memoTableTypes == null)
                        {
                            var map = new System.Collections.Generic.Dictionary<int, string>();
                            foreach (var t in EnumerateFakeTableSymbols()) map[t] = "Normal";
                            _memoTableTypes = map;
                        }
                        return _memoTableTypes.TryGetValue(id, out var n) ? n : null;
                    }
                }
                """,
                funnel: """
                    namespace P;
                    internal static partial class Fake
                    {
                        private static void InvalidateBcAppIndexes() { _memoTableTypes.Clear(); }
                    }
                    """)));

    /// <summary>
    /// The control that stops this guard being the noisy sweep it is designed against: a static
    /// memo that does NOT reach the registration walk is not a candidate at all, however much
    /// it looks like one. Without this, the analysis could be selecting on "is a lazy static
    /// Dictionary" — which would flag every compiled Regex and MethodInfo bind in the tree, and
    /// the offender fact above would still be green.
    /// </summary>
    [Fact]
    public void AStaticMemoThatDoesNotReachTheWalk_IsNotACandidate()
    {
        var sources = Fixture("""
            namespace P;
            internal static partial class Fake
            {
                private static System.Collections.Generic.Dictionary<int, string>? _unrelatedMemo;
                private static string? UnrelatedLookup(int id)
                {
                    if (_unrelatedMemo == null)
                    {
                        var map = new System.Collections.Generic.Dictionary<int, string>();
                        for (var i = 0; i < 4; i++) map[i] = "x";
                        _unrelatedMemo = map;
                    }
                    return _unrelatedMemo.TryGetValue(id, out var n) ? n : null;
                }
            }
            """);

        Assert.DoesNotContain(Candidates(sources), c => c.Field == "_unrelatedMemo");
        Assert.Empty(OffencesIn(sources));
    }

    /// <summary>
    /// A <c>readonly</c> static is not a candidate: it cannot be reassigned, so it cannot hold
    /// a memo that survives an epoch by being rebuilt-and-kept. Its CONTENTS changing is a
    /// different question, caught through whatever mutates them.
    /// </summary>
    [Fact]
    public void AReadonlyStatic_IsNotACandidate()
    {
        var sources = Fixture("""
            namespace P;
            internal static partial class Fake
            {
                private static readonly System.Collections.Generic.Dictionary<int, string> _readonlyMemo = new();
                private static string? ReadonlyLookup(int id)
                {
                    foreach (var t in EnumerateFakeTableSymbols()) _readonlyMemo[t] = "Normal";
                    return _readonlyMemo.TryGetValue(id, out var n) ? n : null;
                }
            }
            """);

        Assert.DoesNotContain(Candidates(sources), c => c.Field == "_readonlyMemo");
    }

    /// <summary>
    /// The walk named in a COMMENT or a string does not make a method reach it. Matching is on
    /// the invocation's name node; #3064 is the measured cost of the raw-text alternative, where
    /// a comment written to document compliance with a rule made the file fail that rule.
    /// </summary>
    [Fact]
    public void TheWalkNamedInProseOnly_DoesNotMakeAMethodReachIt()
    {
        var sources = Fixture($$"""
            namespace P;
            internal static partial class Fake
            {
                private static System.Collections.Generic.Dictionary<int, string>? _proseMemo;
                /// <summary>Deliberately does NOT call {{RegistrationWalk}}.</summary>
                private static string? ProseLookup(int id)
                {
                    // Nothing here calls {{RegistrationWalk}}("tables").
                    var note = "{{RegistrationWalk}}";
                    if (_proseMemo == null) _proseMemo = new System.Collections.Generic.Dictionary<int, string> { [0] = note };
                    return _proseMemo.TryGetValue(id, out var n) ? n : null;
                }
            }
            """);

        Assert.DoesNotContain(Candidates(sources), c => c.Field == "_proseMemo");
        Assert.Empty(OffencesIn(sources));
    }

    /// <summary>
    /// The allowlist works on synthetic content too — so the mechanism the checked-in entries
    /// rely on is proved here, rather than inferred from those entries all being green.
    /// </summary>
    [Fact]
    public void AnAllowlistedField_IsNotReportedButIsStillACandidate()
    {
        var sources = Fixture("""
            namespace P;
            internal static partial class Fake
            {
                private static System.Reflection.MethodInfo? _boundMethod;
                private static System.Reflection.MethodInfo? BindOnce()
                {
                    if (_boundMethod == null)
                    {
                        foreach (var t in EnumerateFakeTableSymbols()) { }
                        _boundMethod = typeof(Fake).GetMethod("BindOnce");
                    }
                    return _boundMethod;
                }
            }
            """);

        var drops = ResetFunnelDrops(sources);
        var candidates = Candidates(sources);
        Assert.Contains(candidates, c => c.Field == "_boundMethod");

        // Not allowlisted: an offender.
        Assert.NotEmpty(candidates.Select(c => OffenceOf(c, drops, AllowedProcessLifetimeStatics)).Where(o => o != null));

        // Allowlisted with a reason: silent, and still a candidate, so the stale check can see it.
        var allowed = new Dictionary<string, string> { ["_boundMethod"] = "a MethodInfo bind against the loaded assembly" };
        Assert.Empty(candidates.Select(c => OffenceOf(c, drops, allowed)).Where(o => o != null));
    }
}
