// BcShapeMethodLookupTests — name-only BC method lookups outside the permission slice (#3069).
//
// ── THE MECHANISM, AND WHAT IT IS *NOT* ──────────────────────────────────────────────────
// `Type.GetMethod(name)` / `Type.GetMethod(name, flags)` do NOT silently hand back the wrong
// member when Microsoft ships a second method of that name. Measured, not assumed — the first
// arm below asserts it: reflection's default binder raises AmbiguousMatchException. So the
// defect is not a wrong answer at the reflection layer.
//
// It becomes a wrong answer one layer up. MethodScopePatches.NavMethodScope_AssertError — AL's
// `asserterror` seam — rethrows exactly one type (BcShapeGapException) and absorbs everything
// else. An AmbiguousMatchException raised under an `asserterror` is therefore SWALLOWED, and the
// asserterror PASSES on a call real BC performs fine. Green, and the opposite of BC's answer.
// #3062 fixed that inversion for the three permission-slice files; this is the same repair for
// the record/query paths, behind one shared helper instead of a file-private one.
//
// No `!` appears in the shape, so #3051's sweep of null-forgiving lookups cannot find it: these
// two issues share a SEAM, not a mechanism. #3051 is a member that MOVED (silent null, NRE at
// the first use); this is a member that MULTIPLIED (a throw of the wrong type). BcShape.FindMethod
// deliberately still answers null on absence, so converting a site here does not change what it
// does on a build where the member is gone — that half stays #3051's.
//
// ── WHY IT IS LATENT TODAY, AND WHY IT IS STILL REAL ─────────────────────────────────────
// Measured over Ncl 27.5.46862.48827 and 28.1.49838.50621: every BC method name the converted
// call sites look up has exactly ONE declaration on the type they look it up on, so the
// conversion changes nothing on any BC build the runner has seen.
//
// Microsoft does add overloads to types the runner reflects on, though — measured on the same
// two builds, Microsoft.Dynamics.Nav.Runtime.NavReport.Execute went from 4 declarations to 6
// (`Execute(String, String)` and `Execute(String, String, NavRecordRef)` are new in 28.1). The
// runner does not look that one up by name; the point is that the event is ordinary, and the day
// it lands on a name the runner DOES look up, this is the difference between a named refusal and
// a green asserterror.
//
// ── MEASURED RED (production call sites unconverted, this file unchanged) ────────────────
//   AssertError_TearsThrough_InsteadOfSwallowingTheAmbiguousEvaluate
//       Assert.Throws() Failure: No exception was thrown
//   TryFunction_CannotTrapTheRefusal_OnTheCorrectedEvaluatePath
//       System.Reflection.AmbiguousMatchException : Ambiguous match found.
//   BcShape_FindMethod_* / NoBcTypedNameOnlyMethodLookup_* : green either way (helper + scan)
//
// The first arm IS the inversion: the seam swallowed the AmbiguousMatchException and returned
// normally, which in AL is an `asserterror` that passed. The second shows the other seam already
// refused it, so only asserterror inverts — that arm is a CONTROL, not a second RED.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class BcShapeMethodLookupTests
{
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    // ══ 1. The premise, measured rather than assumed ════════════════════════════════════

    [Fact]
    public void TypeGetMethodByName_ThrowsAmbiguousMatch_WhenBcShipsASecondDeclaration()
    {
        Assert.Throws<AmbiguousMatchException>(
            () => typeof(TwoEvaluates).GetMethod("Evaluate", PublicInstance));

        // ... and the single-declaration type it stands in for does not, so the throw above is
        // about the second declaration and not about the lookup itself.
        Assert.NotNull(typeof(OneEvaluate).GetMethod("Evaluate", PublicInstance));
    }

    /// <summary>
    /// The other half of the premise, and the reason this issue is NOT "reflection returns the
    /// wrong member": a `new`-hidden declaration whose SIGNATURE changed is ambiguous too, so
    /// reflection refuses rather than picking. There is no silent-wrong-member outcome to fix
    /// at this layer — the wrong answer is manufactured by the seam that absorbs the throw.
    /// </summary>
    [Fact]
    public void TypeGetMethodByName_ThrowsAmbiguousMatch_ForAHiddenDeclarationWithANewSignature()
    {
        Assert.Throws<AmbiguousMatchException>(
            () => typeof(HidesEvaluateWithADifferentSignature).GetMethod("Evaluate", PublicInstance));
    }

    /// <summary>
    /// The seam, on the raw expression the production sites used to contain: it returns NORMALLY.
    /// In AL that is `asserterror` passing over a call real BC performs fine. This arm asserts
    /// the FRAMEWORK behaviour the fix routes around, so it stays green after the fix.
    /// </summary>
    [Fact]
    public void AssertError_AbsorbsARawAmbiguousMatch_WhichIsWhatInvertsTheResult()
    {
        var reached = false;

        BcRuntime.NavMethodScope_AssertError(null!, () =>
        {
            reached = true;
            typeof(TwoEvaluates).GetMethod("Evaluate", PublicInstance);
        });

        Assert.True(reached, "the body must actually run, else this arm proves nothing");
    }

    // ══ 2. THE INVERSION, on the real call path ═════════════════════════════════════════
    //
    // RecordPatches.EvaluateFilterExpression is the runner's driver for BC's
    // FilterExpression.Evaluate(NavValue, ISortingRulesProvider) — the WHERE evaluation behind AL
    // query filtering, so it is reachable from AL and therefore from `asserterror`. Both arms
    // drive PRODUCTION code with the two reflection statics it reads injected for one call;
    // nothing here re-implements the helper.

    [Fact]
    public void AssertError_TearsThrough_InsteadOfSwallowingTheAmbiguousEvaluate()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => EvaluateWith(typeof(TwoEvaluates), new TwoEvaluates())));

        Assert.Equal("FilterExpression.Evaluate", ex.Member);
        Assert.Contains("BC declares 2 methods named Evaluate", ex.Detail, StringComparison.Ordinal);
        Assert.Contains("cannot tell which one", ex.Detail, StringComparison.Ordinal);
    }

    // CONTROL, not a second RED: NavApplicationObjectBase_TryInvoke rethrew the raw
    // AmbiguousMatchException before this change too. What it pins is that the corrected refusal
    // still tears through, rather than becoming something an AL [TryFunction] can trap.
    [Fact]
    public void TryFunction_CannotTrapTheRefusal_OnTheCorrectedEvaluatePath()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => EvaluateWith(typeof(TwoEvaluates), new TwoEvaluates())));

        Assert.Equal("FilterExpression.Evaluate", ex.Member);
    }

    /// <summary>
    /// CONTROL, and the arm that makes the two above mean something: with a SINGLE Evaluate the
    /// same production path resolves it and returns the fake's own answer. Both directions, so a
    /// helper that always refused — or one that always answered a constant — fails here.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EvaluateFilterExpression_StillDrivesTheSingleEvaluate_AndReturnsItsAnswer(bool answer)
    {
        Assert.Equal(answer, EvaluateWith(typeof(OneEvaluate), new OneEvaluate(answer)));
    }

    /// <summary>
    /// Absence still refuses the way it did before — with the call site's own
    /// InvalidOperationException, NOT a shape gap. This pins that the conversion moved exactly one
    /// outcome (ambiguity) and left the null-forgiving half to #3051, so the two issues stay
    /// separately fixable.
    /// </summary>
    [Fact]
    public void EvaluateFilterExpression_KeepsItsOwnRefusal_WhenEvaluateIsGoneEntirely()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EvaluateWith(typeof(NoEvaluate), new NoEvaluate()));

        Assert.Contains("FilterExpression.Evaluate", ex.Message, StringComparison.Ordinal);
        Assert.IsNotType<BcShapeGapException>(ex);
    }

    // ══ 3. BcShape.FindMethod / RequiredMethod ══════════════════════════════════════════

    [Fact]
    public void FindMethod_ResolvesTheOnlyDeclaration()
    {
        var m = BcShape.FindMethod(typeof(OneEvaluate), "Evaluate", PublicInstance,
            "surface", "OneEvaluate.Evaluate", "detail");

        Assert.NotNull(m);
        Assert.Equal(2, m!.GetParameters().Length);
    }

    [Fact]
    public void FindMethod_AnswersNull_WhenBcDeclaresNone_SoAbsenceStaysTheCallSitesProblem()
    {
        Assert.Null(BcShape.FindMethod(typeof(NoEvaluate), "Evaluate", PublicInstance,
            "surface", "NoEvaluate.Evaluate", "detail"));
    }

    [Fact]
    public void FindMethod_RaisesAShapeGapNamingTheMemberAndTheCount_WhenBcDeclaresTwo()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcShape.FindMethod(
            typeof(TwoEvaluates), "Evaluate", PublicInstance,
            "Query filtering (WHERE evaluation)", "FilterExpression.Evaluate", "the filter cannot be applied"));

        Assert.Equal("Query filtering (WHERE evaluation)", ex.Surface);
        Assert.Equal("FilterExpression.Evaluate", ex.Member);
        Assert.Contains("BC declares 2 methods named Evaluate on TwoEvaluates", ex.Detail,
            StringComparison.Ordinal);
        Assert.Contains("the filter cannot be applied", ex.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// STRICTER than what it replaces, on purpose and in exactly one case: a `new`-hidden
    /// declaration of the SAME signature. Type.GetMethod hands back the most-derived one without
    /// complaint (asserted here, so the difference is measured), which could silently drive the
    /// wrong declaration; this refuses instead.
    /// </summary>
    [Fact]
    public void FindMethod_RefusesANewHiddenDeclarationOfTheSameSignature_WhichGetMethodWouldHaveGuessed()
    {
        var guessed = typeof(HidesEvaluateWithTheSameSignature).GetMethod("Evaluate", PublicInstance);
        Assert.Equal(typeof(HidesEvaluateWithTheSameSignature), guessed!.DeclaringType);

        var ex = Assert.Throws<BcShapeGapException>(() => BcShape.FindMethod(
            typeof(HidesEvaluateWithTheSameSignature), "Evaluate", PublicInstance,
            "surface", "Hidden.Evaluate", "detail"));

        Assert.Contains("BC declares 2 methods named Evaluate", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FindMethod_ResolvesTheAskedForOverload_WhenTheSignatureIsPinned()
    {
        var two = BcShape.FindMethod(typeof(TwoEvaluates), "Evaluate", PublicInstance,
            "surface", "member", "detail", new[] { typeof(object), typeof(object) });

        Assert.Equal(2, two!.GetParameters().Length);
    }

    // The pinned selection DISCRIMINATES: asked for the one-parameter overload it returns THAT
    // one, so the arm above is not satisfied by a helper that hands back the first candidate.
    [Fact]
    public void FindMethod_ReturnsTheOtherOverload_WhenThatIsTheSignatureAskedFor()
    {
        var one = BcShape.FindMethod(typeof(TwoEvaluates), "Evaluate", PublicInstance,
            "surface", "member", "detail", new[] { typeof(object) });

        Assert.Equal(1, one!.GetParameters().Length);
        Assert.Equal(typeof(object), one.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void RequiredMethod_RaisesAShapeGapNamingTheSignature_WhenTheParametersHaveMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcShape.RequiredMethod(
            typeof(TwoEvaluates), "Evaluate", PublicInstance,
            "surface", "TwoEvaluates.Evaluate", "the filter cannot be applied",
            new[] { typeof(int) }));

        Assert.Equal("TwoEvaluates.Evaluate", ex.Member);
        Assert.Contains("method not found with signature (Int32)", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredMethod_ResolvesWhenItIsThere_SoTheRefusalIsAboutAbsence()
    {
        Assert.Equal("Evaluate", BcShape.RequiredMethod(typeof(OneEvaluate), "Evaluate",
            PublicInstance, "surface", "member", "detail").Name);
    }

    [Fact]
    public void FindMethod_FindsANonPublicMethod_SoTheConvertedSitesKeepTheirOwnFlags()
    {
        var m = BcShape.FindMethod(typeof(OneEvaluate), "HiddenHelper",
            BindingFlags.NonPublic | BindingFlags.Instance, "surface", "member", "detail");

        Assert.NotNull(m);
        Assert.Equal("HiddenHelper", m!.Name);
    }

    // ══ 4. The shape, not just the sites converted here ═════════════════════════════════
    //
    // The issue reports 212 name-only lookups. Re-measured on this tree with an argument-level
    // parser rather than a per-statement regex, the census is:
    //
    //     Type.GetMethod(...) call sites in AlRunner/**/*.cs   324
    //       ... passing an explicit signature                  123
    //       ... resolving by NAME ALONE                        201
    //             of which target typeof(<a runner-owned type>) 103
    //             of which could reach a Microsoft-shipped type  98
    //
    // The 103 are the Cecil rewriter and the hook installers resolving the runner's OWN
    // replacement methods. Microsoft cannot ship an overload into AlRunner.BcRuntime, so no BC
    // update can move them, and converting them would buy nothing. The scan below therefore
    // counts only the second group — a lookup whose declaring type comes from somewhere other
    // than this assembly.
    //
    // What it asserts, and the limit of it: the repo-wide TOTAL, which cannot rise without a
    // failure, so a new name-only BC-typed lookup anywhere in AlRunner/ is caught the moment it
    // is written. It does not pin the per-file distribution, so moving one site out of file A
    // and into file B nets to zero and passes; the per-file breakdown is printed on failure for
    // whoever has to update the number. Deliberately a ratchet rather than a zero: 72 sites
    // remain, and #3069 asks for them file by file so the guard never has to be weakened.
    //
    // 76 -> 72 by #3051, which converted the null-forgiving BC-internals lookups repo-wide.
    // The net is not the gross, so the arithmetic is worth writing down. Six counted sites went
    // away -- NavSymRef.ModuleDefinition.Clone in BcCompiler.Incremental.cs, LoadMetadata in
    // RecordPatches.RealPageMetadata.cs and RecordPatches.RealXmlPortMetadata.cs, and
    // CreateFromMetaTable / CreateForTempTable / CreateTempDataAccess in RecordPatches.cs -- and
    // two arrived, both inside BcShapeGapException.cs itself: BcShape.Method's name-only and
    // name-plus-flags overloads each call the reflection API once. That is exactly where a
    // name-only lookup belongs, in the one helper that refuses by name when it misses.
    // (#3051's other 67 conversions are GetProperty / GetField / GetConstructor, or pass an
    // explicit signature, so this scan never counted them.)

    /// <summary>
    /// Every remaining name-only method lookup that could reach a Microsoft-shipped type. Lower
    /// it as sites are converted; it may never rise. On a mismatch the assertion prints the
    /// per-file breakdown, which is the number to put here.
    /// </summary>
    private const int NameOnlyBcTypedMethodLookups = 72;

    /// <summary>The floor is not cosmetic: a scan that silently narrowed to a handful of files
    /// would report a small number and read as progress. AlRunner/ holds ~195 sources.</summary>
    private const int MinimumProductionSourcesScanned = 150;

    /// <summary>Files this change converted. They must stay at zero, so the slice cannot regress
    /// while the ratchet above is being lowered elsewhere.</summary>
    private static readonly string[] ConvertedFiles =
    {
        "Patches/AsyncStateMachineSpike.cs",
        "Patches/BlobStoreIsolationPatches.cs",
        "Patches/NavRecordRefPatches.cs",
        "Patches/RecordPatches.FieldFindIntercept.cs",
        "Patches/RecordPatches.FieldVirtualTable.cs",
        "Patches/RecordPatches.QueryProjection.cs",
    };

    [Fact]
    public void NameOnlyBcTypedMethodLookups_HaveNotIncreased()
    {
        var byFile = ScanNameOnlyBcTypedLookups(out var filesScanned);

        Assert.True(filesScanned >= MinimumProductionSourcesScanned,
            $"the scan saw only {filesScanned} production sources under AlRunner/, below the "
            + $"{MinimumProductionSourcesScanned} floor — it narrowed, and a narrowed scan reports "
            + "a small number that reads as progress.");

        var total = byFile.Values.Sum();
        Assert.True(total == NameOnlyBcTypedMethodLookups,
            $"name-only BC-typed method lookups are {total}, recorded as "
            + $"{NameOnlyBcTypedMethodLookups}. Converting sites? Lower the constant. Adding one? "
            + "Use BcShape.FindMethod/RequiredMethod instead (#3069). Breakdown:"
            + Environment.NewLine
            + string.Join(Environment.NewLine,
                byFile.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                      .Select(kv => $"  {kv.Value,4}  {kv.Key}")));
    }

    [Fact]
    public void TheConvertedFiles_HaveNoNameOnlyBcTypedMethodLookupLeft()
    {
        var byFile = ScanNameOnlyBcTypedLookups(out _);

        var offenders = ConvertedFiles
            .Where(f => byFile.TryGetValue(f, out var n) && n > 0)
            .Select(f => $"  {byFile[f]}  {f}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "these files were converted by #3069 and must resolve every BC method lookup through "
            + "BcShape, so an added Microsoft overload names the gap instead of arriving as an "
            + "AmbiguousMatchException the asserterror seam absorbs:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// CONTROL for the scan itself: it must actually FIND something in the shape it looks for,
    /// or both assertions above are vacuous. Pins that at least one known-remaining file is still
    /// counted, and that a file made only of runner-owned lookups is not.
    /// </summary>
    [Fact]
    public void TheScan_CountsABcTypedLookupAndIgnoresARunnerOwnedOne()
    {
        var byFile = ScanNameOnlyBcTypedLookups(out _);

        // BcRuntime.cs holds both kinds: dozens of typeof(AlRunner.BcRuntime).GetMethod(nameof(…))
        // hook installs (not counted) and a set of lazily-resolved BC lookups (counted).
        Assert.True(byFile.GetValueOrDefault("BcRuntime.cs") > 0,
            "the scan found no BC-typed name-only lookup in BcRuntime.cs, which has several — the "
            + "target classifier is over-matching and the totals above mean nothing.");

        // NclCecilRewrite.Dispatch.cs is entirely runner-owned helper resolution: every lookup
        // there is typeof(AlRunner.…).GetMethod(nameof(…)) against the runner's own patch classes.
        Assert.Equal(0, byFile.GetValueOrDefault("Infrastructure/NclCecilRewrite.Dispatch.cs"));
    }

    // ── the scanner ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-file counts of <c>Type.GetMethod</c> call sites that pass no signature AND whose
    /// declaring type is not a type of this assembly. Keys are paths relative to <c>AlRunner/</c>.
    /// </summary>
    private static Dictionary<string, int> ScanNameOnlyBcTypedLookups(out int filesScanned)
    {
        var runnerRoot = Path.Combine(RepoRoot, "AlRunner");
        var sources = ProductionSources(runnerRoot);
        filesScanned = sources.Count;

        var runnerTypeNames = RunnerTypeNames();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in sources)
        {
            var code = CodeWithoutLineComments(path);
            var runnerOwnedLocals = RunnerOwnedLocals(code, runnerTypeNames);
            var count = 0;

            foreach (var (target, args) in GetMethodCalls(code))
            {
                if (args.Count == 0) continue;                     // StackFrame.GetMethod()
                if (HasExplicitSignature(args)) continue;
                if (IsRunnerOwnedTarget(target, runnerTypeNames, runnerOwnedLocals)) continue;
                count++;
            }

            if (count > 0)
                result[Path.GetRelativePath(runnerRoot, path).Replace('\\', '/')] = count;
        }

        return result;
    }

    /// <summary>Simple and full names of every type in the AlRunner assembly.</summary>
    private static HashSet<string> RunnerTypeNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in typeof(BcRuntime).Assembly.GetTypes())
        {
            names.Add(t.Name);
            if (t.FullName != null) names.Add(t.FullName.Replace('+', '.'));
        }
        return names;
    }

    /// <summary>
    /// Locals assigned <c>typeof(&lt;a runner type&gt;)</c> in this file. Without this the Cecil
    /// rewriter's <c>var patchTypeMi = typeof(AlRunner.Patches.MediaSetPatches); … patchTypeMi
    /// .GetMethod(nameof(…))</c> would count as a BC lookup, which would ask for conversions that
    /// buy nothing and make the ratchet dishonest.
    /// </summary>
    private static HashSet<string> RunnerOwnedLocals(string code, HashSet<string> runnerTypeNames)
    {
        var locals = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(code, @"\b(?:var|Type)\s+(\w+)\s*=\s*typeof\(\s*([\w\.]+)\s*\)"))
            if (runnerTypeNames.Contains(m.Groups[2].Value)) locals.Add(m.Groups[1].Value);
        return locals;
    }

    private static bool IsRunnerOwnedTarget(
        string target, HashSet<string> runnerTypeNames, HashSet<string> runnerOwnedLocals)
    {
        var typeofMatch = Regex.Match(target, @"typeof\(\s*([\w\.]+)\s*\)\s*[!?]?\s*$");
        if (typeofMatch.Success) return runnerTypeNames.Contains(typeofMatch.Groups[1].Value);

        var identifier = Regex.Match(target, @"([A-Za-z_]\w*)\s*[!?]?\s*$");
        return identifier.Success && runnerOwnedLocals.Contains(identifier.Groups[1].Value);
    }

    /// <summary>
    /// A signature is explicit when <c>types:</c> is named, or when a positional argument is a
    /// <c>Type[]</c> — <c>Type.EmptyTypes</c>, <c>new[] { … }</c>, <c>new Type[] { … }</c>, or an
    /// identifier ending in "Types". Those overloads match one signature and cannot raise
    /// AmbiguousMatchException.
    /// </summary>
    private static bool HasExplicitSignature(IReadOnlyList<string> args)
    {
        if (args.Any(a => a.StartsWith("types:", StringComparison.Ordinal))) return true;

        // GetMethod(name, types) | (name, flags, types) | (name, flags, binder, types, modifiers)
        foreach (var index in new[] { 1, 2, 3 })
            if (index < args.Count && LooksLikeTypeArray(args[index])) return true;
        return false;
    }

    private static bool LooksLikeTypeArray(string arg)
        => arg.StartsWith("Type.EmptyTypes", StringComparison.Ordinal)
           || arg.StartsWith("new[]", StringComparison.Ordinal)
           || arg.StartsWith("new []", StringComparison.Ordinal)
           || arg.StartsWith("new Type[", StringComparison.Ordinal)
           || Regex.IsMatch(arg, @"^[\w\.]*Types$")
           || Regex.IsMatch(arg, @"^\w+ParameterTypes$");

    /// <summary>
    /// Every <c>.GetMethod(</c> call in <paramref name="code"/> as (text preceding the call,
    /// top-level arguments). A character scanner rather than a regex: an argument list holds
    /// nested calls, generics and string literals containing brackets, so a regex either stops at
    /// the first <c>)</c> or swallows the rest of the file.
    /// </summary>
    private static IEnumerable<(string Target, IReadOnlyList<string> Args)> GetMethodCalls(string code)
    {
        foreach (Match m in Regex.Matches(code, @"\.GetMethod\s*\("))
        {
            var i = m.Index + m.Length;
            var depth = 1;
            var inString = false;
            while (i < code.Length && depth > 0)
            {
                var c = code[i];
                if (inString)
                {
                    if (c == '\\') { i += 2; continue; }
                    if (c == '"') inString = false;
                    i++;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                i++;
            }
            var inner = code.Substring(m.Index + m.Length, Math.Max(0, i - 1 - (m.Index + m.Length)));
            var targetStart = Math.Max(0, m.Index - 200);
            yield return (Flatten(code.Substring(targetStart, m.Index - targetStart)), SplitTopLevel(inner));
        }
    }

    private static string Flatten(string s) => Regex.Replace(s, @"\s+", " ").TrimEnd();

    private static IReadOnlyList<string> SplitTopLevel(string s)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        var inString = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < s.Length) { current.Append(s[++i]); continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; current.Append(c); continue; }
            if (c is '(' or '[' or '{' or '<') depth++;
            else if (c is ')' or ']' or '}' or '>') depth--;
            if (c == ',' && depth == 0) { args.Add(Flatten(current.ToString()).Trim()); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.ToString().Trim().Length > 0) args.Add(Flatten(current.ToString()).Trim());
        return args;
    }

    /// <summary>
    /// The source with whole-line <c>//</c> comments removed, for the reason the sibling guards
    /// already document: headers in this repository quote the retired shape in prose on purpose,
    /// and the claim under test is about CODE.
    /// </summary>
    private static string CodeWithoutLineComments(string path)
        => string.Join('\n', File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>
    /// Every production C# source under <paramref name="root"/>, discovered by walking. Throws
    /// when the walk finds nothing — same contract as FieldTriggerShapeGapCallSiteTests, and for
    /// the same reason: a scan with nothing to scan reports zero and reads as success. .claude is
    /// excluded because agent worktrees there are full checkouts of this repository.
    /// </summary>
    private static IReadOnlyList<string> ProductionSources(string root)
    {
        var skipped = new[] { "bin", "obj", ".git", ".claude", ".vs", "node_modules", "packages" };
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (skipped.Contains(name, StringComparer.Ordinal)) continue;
                if (name.EndsWith(".Tests", StringComparison.Ordinal)) continue;
                pending.Push(sub);
            }
            found.AddRange(Directory.EnumerateFiles(dir, "*.cs"));
        }

        if (found.Count == 0)
            throw new InvalidOperationException(
                $"The scan found no C# sources under '{root}'. A scan with nothing to scan reports "
                + "zero violations and reads as success, so it fails here instead.");

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // ══ 4. Plumbing ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drives production <c>RecordPatches.EvaluateFilterExpression</c> with the two reflection
    /// statics it reads pointed at <paramref name="filterExpressionType"/> for exactly one call,
    /// and restores both in a finally. Nothing in production becomes settable for the test.
    /// </summary>
    private static bool EvaluateWith(Type filterExpressionType, object expr) => WithStatics(
        new (string, object?)[] { ("_tFilterExpr", filterExpressionType), ("_mFilterExprEvaluate", null) },
        () => (bool)Invoke("EvaluateFilterExpression", expr, new object(), null)!);

    private static T WithStatics<T>((string Name, object? Value)[] statics, Func<T> body)
    {
        var fields = statics.Select(x => (Field: Static(x.Name), x.Value)).ToArray();
        var saved = fields.Select(f => f.Field.GetValue(null)).ToArray();
        try
        {
            foreach (var (field, value) in fields) field.SetValue(null, value);
            return body();
        }
        finally
        {
            for (var i = 0; i < fields.Length; i++) fields[i].Field.SetValue(null, saved[i]);
        }
    }

    private static FieldInfo Static(string name)
        => typeof(RecordPatches).GetField(name, Priv)
           ?? throw new InvalidOperationException($"test setup: RecordPatches.{name} not found");

    private static object? Invoke(string name, params object?[] args)
    {
        var m = typeof(RecordPatches).GetMethod(name, Priv)
            ?? throw new InvalidOperationException($"test setup: RecordPatches.{name} not found");
        try
        {
            return m.Invoke(null, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    /// <summary>The shape BC declares today: exactly one Evaluate.</summary>
    public sealed class OneEvaluate
    {
        private readonly bool _answer;
        public OneEvaluate(bool answer = true) => _answer = answer;
        public bool Evaluate(object navValue, object? sortingRules) => _answer;
        internal void HiddenHelper() { }
    }

    /// <summary>Stands in for FilterExpression after Microsoft ships a second Evaluate.</summary>
    public sealed class TwoEvaluates
    {
        public bool Evaluate(object navValue, object? sortingRules) => true;
        public bool Evaluate(object navValue) => true;
    }

    /// <summary>Stands in for a build where Evaluate is gone entirely.</summary>
    public sealed class NoEvaluate
    {
    }

    public class EvaluateBase
    {
        public bool Evaluate(object navValue, object? sortingRules) => false;
    }

    public sealed class HidesEvaluateWithADifferentSignature : EvaluateBase
    {
        public new bool Evaluate(object navValue) => true;
    }

    public sealed class HidesEvaluateWithTheSameSignature : EvaluateBase
    {
        public new bool Evaluate(object navValue, object? sortingRules) => true;
    }
}
