// RecordPatches.MetadataOptionEnumOrdinals — where the ordinal of Page Metadata's
// (2000000138) "PageType" option column actually comes from, and why CodeUnit Metadata's
// (2000000137) "SubType" column deliberately does NOT come from the same place (#3080).
//
// ── WHAT WAS WRONG ───────────────────────────────────────────────────────────────────────
//   The Page Metadata populator resolved its one option column by looking the declared member
//   NAME up in that column's own OptionString, and fell back to BC's zero value when the name
//   was not there:
//
//       if (pageTypeOrdinals.TryGetValue(NormalizeObjectTypeName(row.PageType), out var o))
//           return NavOption.Create(field.FieldOptionMetadata, o);
//       // "should not happen — the compiler validated it against the same enum"
//       return NavValue.GetDefaultNavValue(field, false);
//
//   The comment was wrong, and measurably so. That column does NOT list every member the AL
//   compiler accepts. Read out of BC 28.1.49838.53910's own System.app:
//
//     Page Metadata "PageType"  OptionMembers = Card,List,RoleCenter,CardPart,ListPart,
//                               Document,Worksheet,ListPlus,ConfirmationDialog,NavigatePage,
//                               StandardDialog,API,HeadlinePart                    (13)
//
//   while the runtime enum it is filled from carries more:
//
//     Microsoft.Dynamics.Nav.Types.Metadata.PageType (Microsoft.Dynamics.Nav.Types.dll)
//       Card=0 … HeadlinePart=12, ReportPreview=13, ReportProcessingOnly=14, XmlPort=15,
//       ReportViewer=16, FilterPage=17, ListQuery=18, BannerPart=19, PromptDialog=20,
//       ConfigurationDialog=21, UserControlHost=22                                 (23)
//
//   So the option string is a PREFIX of the enum, and `PageType = PromptDialog` /
//   `PageType = UserControlHost` are ordinary, compiler-accepted AL that lands outside it.
//   Not hypothetical: Base Application 28.1 ships page 5836 "Copilot Marketing Text"
//   (PromptDialog) and page 6324 "Power BI Element Addin Host" (UserControlHost). Both were
//   answered `PageType::Card` by this runner, about a page that declared something else —
//   the silent default .claude/rules/loud-failures.md exists to stop.
//
// ── WHAT REAL BC DOES, MEASURED ON A SERVICE TIER ────────────────────────────────────────
//   Not a name lookup. PageDataProvider casts the runtime enum straight to int and hands the
//   raw value to the column, whether or not the column lists a member at that position
//   (Ncl.dll 28.1, decompiled):
//
//     PageDataProvider           buffer[4] = GetOptionValue(5, (int)properties.PageType);
//     MetadataDataProvider       GetOptionValue(no, v) => MetaTable.GetFieldByNo(no)
//                                    .FieldOptionMetadata.CreateNavOption(v);
//     NCLOptionMetadata          CreateNavOption(int value) — takes the out-of-range branch
//                                    for value >= Count and returns
//                                    NavOption.CreateBypassCache(this, value). It does not
//                                    clamp, and it does not default.
//
//   A real service tier confirms it: StefanMaron/BusinessCentral.AL.Language.Tests#196 puts a
//   `PageType = PromptDialog` page in front of eight Cloud legs (27.0 through 28.4) and the
//   column reports 20 on every one — an ordinal with no member name in it at all. So for THIS
//   column the enum is the source of truth for the number, and the option string only names
//   the members it happens to cover.
//
// ── WHY CODEUNIT METADATA'S "SubType" IS NOT OVERLAID THE SAME WAY ───────────────────────
//   Because the same reasoning, applied to that column, produced a wrong answer, and a
//   service tier caught it. CodeUnitDataProvider looks identical —
//   `buffer[4] = GetOptionValue(5, (int)metaCodeunit.Subtype)` — and
//   Microsoft.Dynamics.Nav.Types.CodeunitSubType really does run Normal=0, Test=1,
//   TestRunner=2, Upgrade=3, Install=4, one member past the four its column names. Predicting
//   4 for a `Subtype = Install` codeunit follows, and it is wrong:
//   StefanMaron/BusinessCentral.AL.Language.Tests#201 reads exactly that on a tier and gets
//   ordinal 0, `Subtype::Normal`, on all eight legs. The ROW is there — the same test's
//   `Get` succeeds — only the value differs.
//
//   The step the decompilation missed is one frame upstream: the value handed to that column
//   is not the AL-declared subtype. NCLMetaCodeunit.Subtype returns `options?.Subtype` off the
//   codeunit's NavCodeunitOptionsAttribute — what the AL COMPILER wrote — through
//   GetValueOrDefault(). And the compiler does not write Install.
//
//   Measured over Base Application 28.1.49838.53910's own assemblies, decoding every
//   NavCodeunitOptionsAttribute (1,690 of them) and joining it to the same package's
//   SymbolReference.json and AL sources, which record the DECLARED property:
//
//     declared Subtype = Upgrade      28 codeunits  ->  attribute value 3   (28 of 28)
//     declared Subtype = TestRunner    2 codeunits  ->  attribute value 2   ( 2 of 2)
//     declared SubType = Install       3 codeunits  ->  attribute value 0   (3999, 5000, 7582)
//
//   Across all 1,690 the only values that occur are 0, 1660x / 2, 2x / 3, 28x. Not one
//   codeunit anywhere carries 4. So Install never reaches the column, CreateNavOption's
//   out-of-range branch is never taken for it, and that column's four members are complete
//   with respect to what can actually appear there. Nothing defaults and nothing clamps —
//   `Normal` is what legitimately arrives.
//
//   That collapse is modelled where it happens rather than here: ResolveCodeunitSubtypeOrdinal
//   in RecordPatches.CodeunitMetadataVirtualTable.cs translates a declared `Install` to
//   `Normal` in front of an option-string-only lookup. It needs to, because BOTH of the
//   runner's row sources carry the declared name: the AL parser reads `Subtype = Install;`
//   out of source, and BcAppSymbolCache reads `"Subtype": "Install"` out of
//   SymbolReference.json (verified present for all three Base Application codeunits above).
//
// ── WHAT THIS FILE DOES ──────────────────────────────────────────────────────────────────
//   Builds the (member name -> ordinal) map the Page Metadata populator resolves against from
//   BOTH sources, with BC's own runtime PageType enum winning any name they share, because
//   that enum is what PageDataProvider actually casts. The option string is still read, for
//   two reasons: it is always present, so it keeps working on a BC build where the enum type
//   has moved or been renamed; and a column that lists a member the enum does not is still
//   answerable.
//
//   A name in NEITHER is refused by the caller rather than defaulted — and refusing is only
//   safe BECAUSE of the enum overlay. Refusing on the option string alone, which is what the
//   Table Metadata sibling does for its own columns, would have thrown on any run that loads
//   the Base Application, taking Page Metadata out entirely. That is the asymmetry #3080 asked
//   about, and the reason the columns end up with the same INVARIANT — "answer the ordinal BC
//   answers, refuse a member no source knows" — but not the same lookup. Table Metadata's
//   TableType/DataClassification option strings do cover their enums; CodeUnit Metadata's
//   SubType covers everything the compiler emits.
//
//   The helpers here stay general (they take the enum as a parameter, and accept null) so the
//   CodeUnit Metadata populator can use the same option-string parsing without the overlay,
//   and so a test can hand them an option string and an enum that disagree.
//
// PRECOMPILED-DLL RESPECT
//   Reads a public enum's names and values by reflection. Nothing is rewritten, nothing is
//   constructed, no AL business-logic body is touched.

using System.Collections.Concurrent;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>BC's runtime enum behind Page Metadata's <c>PageType</c> column — the type
    /// <c>PageDataProvider</c> casts to int. Lives in Microsoft.Dynamics.Nav.Types.dll, not
    /// in Ncl, so it is resolved across the whole load context rather than off a metatable's
    /// own assembly.</summary>
    internal const string BcPageTypeEnumName = "Microsoft.Dynamics.Nav.Types.Metadata.PageType";

    private static readonly ConcurrentDictionary<string, Type?> _optionEnumTypes = new();
    private static readonly ConcurrentDictionary<string, byte> _optionEnumMisses = new();

    /// <summary>
    /// The runtime enum a metadata option column is filled from, or null when this BC build
    /// does not carry it under that name. Cached either way — a miss is reported once and then
    /// costs nothing, since it degrades to the option-string-only map rather than failing.
    /// </summary>
    internal static Type? ResolveBcOptionEnum(string fullName)
    {
        if (_optionEnumTypes.TryGetValue(fullName, out var cached) && cached != null) return cached;

        // Assembly-qualified first, so the type resolves even if Microsoft.Dynamics.Nav.Types
        // has not been touched yet in this process; the scan is the fallback for a BC build
        // that ships it under a different assembly name. A MISS is deliberately not cached:
        // the alternative is a lookup that ran a moment too early and stays wrong for the rest
        // of the process, and the map this feeds is itself cached per column, so the scan runs
        // about twice per run in the worst case.
        var t = Type.GetType(fullName + ", Microsoft.Dynamics.Nav.Types", throwOnError: false);
        if (t is not { IsEnum: true })
        {
            t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var candidate = a.GetType(fullName, throwOnError: false);
                if (candidate is { IsEnum: true }) { t = candidate; break; }
            }
        }

        if (t != null) _optionEnumTypes[fullName] = t;
        return t;
    }

    /// <summary>
    /// The (normalized member name -> ordinal) map an option column resolves against: the
    /// column's own <c>OptionString</c> by position, optionally overlaid with a BC runtime
    /// enum by value.
    /// <para>Where an overlay is supplied it wins every name the two share, because the
    /// provider filling that column casts the enum and never consults the option string. On
    /// every artifact measured the two agree on the names they share and the enum simply
    /// reaches further, so the overlay changes no answer that was already right; what it adds
    /// is the answers that were previously wrong.</para>
    /// <para>Whether a column SHOULD be overlaid is the caller's call, not this method's, and
    /// the two callers answer it differently on purpose: Page Metadata's <c>PageType</c> passes
    /// its enum, CodeUnit Metadata's <c>SubType</c> passes null. The enum reaching past the
    /// column is a necessary condition for an overlay, not a sufficient one — what settles it
    /// is whether the AL compiler ever writes a value out there. See this file's header; a
    /// service tier decided both.</para>
    /// </summary>
    /// <param name="optionString">The column's own <c>NCLOptionMetadata.OptionString</c>.</param>
    /// <param name="bcRuntimeEnum">BC's enum for that column, or null either because the caller
    /// wants the option string alone or because the enum could not be resolved on this build —
    /// in which case the map is the option string alone, exactly as before #3080.</param>
    internal static Dictionary<string, int> BuildMetadataOptionOrdinals(string? optionString, Type? bcRuntimeEnum)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);

        var parts = (optionString ?? string.Empty).Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            var key = NormalizeObjectTypeName(parts[i]);
            if (key.Length == 0) continue;
            map.TryAdd(key, i);
        }

        if (bcRuntimeEnum is { IsEnum: true })
        {
            var names = Enum.GetNames(bcRuntimeEnum);
            var values = Enum.GetValues(bcRuntimeEnum);
            for (int i = 0; i < names.Length; i++)
            {
                var key = NormalizeObjectTypeName(names[i]);
                if (key.Length == 0) continue;
                // Convert.ToInt32 rather than an unbox: the enum's underlying type is BC's to
                // choose, and a byte- or short-backed enum would fail a direct (int) unbox.
                map[key] = Convert.ToInt32(values.GetValue(i));
            }
        }

        return map;
    }

    /// <summary>
    /// <see cref="BuildMetadataOptionOrdinals"/> with the enum resolved by name, reporting a
    /// miss ONCE per column so a BC build that renamed the type is visible in the log rather
    /// than only in a wrong answer months later. A miss is not fatal: the option-string map
    /// still answers every member the column lists, which is every member the corpus and the
    /// overwhelming majority of AL declares.
    /// </summary>
    internal static Dictionary<string, int> BuildMetadataOptionOrdinals(
        string? optionString, string bcRuntimeEnumName, string columnLabel)
    {
        var enumType = ResolveBcOptionEnum(bcRuntimeEnumName);
        if (enumType == null && _optionEnumMisses.TryAdd(bcRuntimeEnumName, 0))
            Console.Error.WriteLine(
                $"[RecordPatches] {columnLabel}: BC's own \"{bcRuntimeEnumName}\" enum is not loaded, so the "
                + "column's ordinals come from its OptionString alone. A member the option string does not "
                + "list will be refused rather than answered.");
        return BuildMetadataOptionOrdinals(optionString, enumType);
    }
}
