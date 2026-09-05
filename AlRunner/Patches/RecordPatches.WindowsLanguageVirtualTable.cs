// RecordPatches.WindowsLanguageVirtualTable — managed provider for the "Windows Language"
// (2000000045) system virtual table.
//
// WHY THIS EXISTS (issue #2581)
//   Windows Language is virtual on the service tier: one row per culture the platform knows
//   about. It routed to the same empty in-memory store as every other table here, so every
//   read answered zero rows — Get(1033) silently returned false, and any AL turning a language
//   id into a name got "no such language" with no error.
//
// WHERE THE VALUES COME FROM
//   BC's own WindowsLanguageDataProvider (Ncl.dll) iterates
//   Microsoft.Dynamics.Nav.Types.WindowsLanguageHelper.AllCultures and fills 16 columns. That
//   helper is a RUNTIME-ENGINE type, which .claude/rules/precompiled-dll-respect.md makes ours
//   to call freely, so the culture list and the three derived values are driven through BC's
//   OWN helper by reflection rather than reimplemented:
//
//       AllCultures            -> the row set, in BC's order
//       LanguageId(culture)    -> "Language ID"
//       PrimaryLanguageId(c)   -> "Primary Language ID"
//       AbbreviatedName(c)     -> "Abbreviated Name"
//
//   The remaining three answerable columns are plain .NET off the same CultureInfo, exactly as
//   BC reads them: EnglishName -> "Name", TextInfo.OEMCodePage -> "Primary CodePage" (the OEM
//   page, NOT the ANSI one — checked against the provider), Name -> "Language Tag".
//
// ── THE COLUMNS WITH NO SOURCE, AND THE VALUES CHOSEN FOR THEM ───────────────────────────
//   Six columns are license-derived. BC computes them from
//   Database.SecurityAndLicense.License.HasLanguagePermission:
//
//       "Enabled"  "Globally Enabled"  "Form Enabled"
//       "Report Enabled"  "Dataport Enabled"  "XMLport Enabled"
//
//   The runner has no license. NavSession.get_License() reads
//   Database.SecurityAndLicense.License, which is null on the skeleton and throws before its
//   own body runs (see NclCecilRewrite.cs's ALDatabase.get_ALSerialNumber note).
//
//   THIS IS A DECLARED DIVERGENCE, NOT A FAITHFUL SUBSTITUTION, and the difference matters.
//   ALDatabase.ALSid(string) is rewritten to return the empty string because the host has no
//   Windows identity store and BC's OWN not-mapped result IS the empty string — there is a
//   defined BC answer to copy. Here there is none: with no license BC does not answer "false",
//   it throws. So nothing is being reproduced. A value is being CHOSEN.
//
//   The choice is PERMISSIVE — permission granted — because the runner exists so that AL tests
//   run without a license at all. Answering "not permitted" would gate the very business logic
//   those tests are there to exercise, turning a missing license into failures that say nothing
//   about the AL under test. Answering "permitted" lets that logic run, which is the whole
//   point of the runner.
//
//   It is declared rather than silent: docs/limitations.md carries it under "Behavioural
//   differences", and tests/runner-extras/windows-language-license-stub asserts the stubbed
//   values, so changing them quietly fails a test.
//
//   Four more columns — "STX File Exist", "ETX File Exist", "Help File Exist" and
//   "Localization Exist" — report whether translation resources are installed. BC answers them
//   from satellite assemblies in its own AppDomain and files under the BC application
//   directory. The runner installs no BC translation resources, so these report false: that is
//   a statement about this process, chosen for the same declared reason and asserted by the
//   same suite.
//
//   BOTH ARE PROVISIONAL, behind ONE seam each, so a future license mock replaces one method
//   rather than scattered literals:
//
//       StubbedLicensePermission()        <- grep this
//       StubbedLocalizationResources()    <- and this
//
//   A mockable license is a planned capability; when it lands, StubbedLicensePermission is the
//   single place that changes, and the runner-extras suite is what will tell you the behaviour
//   moved.
//
// FIELD NAMES ARE MEASURED, NOT ASSUMED
//   Resolved by compiling AL against real BC symbols and dumping the metatable through
//   RecordRef (BC 28.1): 1 "Language ID", 2 "Primary Language ID", 10 "Name",
//   11 "Abbreviated Name", 20 "Enabled", 21 "Globally Enabled", 22 "Form Enabled",
//   23 "Report Enabled", 24 "Dataport Enabled", 25 "XMLport Enabled", 30 "Primary CodePage",
//   31 "STX File Exist", 32 "ETX File Exist", 33 "Help File Exist", 34 "Localization Exist",
//   35 "Language Tag". Matched here BY NAME off the live metatable.

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Every refusal in this file, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification and for
    /// why the anchor is "not-yet-implemented" rather than a docs/scope.md section (#2945).
    /// </summary>
    /// <remarks>
    /// Category (2) for all three, and none of them is the documented Windows Language
    /// DIVERGENCE. The chosen license and installed-resource column values are answers this
    /// provider gives on purpose and never throws for. These three fire when the runner cannot
    /// answer at all: no in-memory provider, or BC's own WindowsLanguageHelper missing or not
    /// the shape its provider uses. All three point at the table's limitations.md section.
    /// </remarks>
    internal static RunnerOutOfScopeException WindowsLanguageShapeGap(string detail)
        => VirtualTableShapeGap("Windows Language (virtual table 2000000045)", "windows-language-virtual-table", detail,
            "docs/limitations.md#windows-language-virtual-table");

    internal const int WindowsLanguageVirtualTableId = 2000000045;

    // One-shot per provider: BC's culture list cannot change during a run.
    private static readonly ConditionalWeakTable<object, object> _wlPopulatedProviders = new();

    private static bool IsWindowsLanguageVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == WindowsLanguageVirtualTableId;

    /// <summary>The six columns BC fills from License.HasLanguagePermission.</summary>
    private static readonly HashSet<string> WindowsLanguageLicenseColumns = new(StringComparer.Ordinal)
    {
        "enabled", "globallyenabled", "formenabled", "reportenabled", "dataportenabled", "xmlportenabled",
    };

    /// <summary>The four columns BC fills from installed translation resources.</summary>
    private static readonly HashSet<string> WindowsLanguageLocalizationColumns = new(StringComparer.Ordinal)
    {
        "stxfileexist", "etxfileexist", "helpfileexist", "localizationexist",
    };

    private static CultureInfo[]? _wlAllCultures;
    private static MethodInfo? _wlLanguageId;
    private static MethodInfo? _wlPrimaryLanguageId;
    private static MethodInfo? _wlAbbreviatedName;

    private static void PopulateWindowsLanguageVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureReportMetadataReflection(metaTable);   // NavBoolean.Create(bool)
        EnsureDataAccessProviderReflection(dataAccess);
        EnsureWindowsLanguageHelperReflection();

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw WindowsLanguageShapeGap("data access has no in-memory provider");

        if (_wlPopulatedProviders.TryGetValue(provider, out _)) return;

        foreach (var culture in _wlAllCultures!)
        {
            var languageId = (int)_wlLanguageId!.Invoke(null, new object?[] { culture })!;
            if (languageId <= 0) continue;   // BC's own list carries no such row
            InsertVirtualRow(provider, metaTable,
                new object[] { WindowsLanguageVirtualTableId, languageId, 0, 0 },
                field => BuildWindowsLanguageValue(field, culture, languageId));
        }

        _wlPopulatedProviders.Add(provider, new object());
    }

    private static object? BuildWindowsLanguageValue(NCLMetaField field, CultureInfo culture, int languageId)
    {
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(
            null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });

        var name = NormalizeObjectTypeName(field.FieldName ?? string.Empty);

        // Declared stubs, not faithful substitution — see the header. Each goes through ONE
        // named seam so a future license mock replaces a method, not a scattered literal.
        if (WindowsLanguageLicenseColumns.Contains(name))
            return NavBoolean(StubbedLicensePermission());

        if (WindowsLanguageLocalizationColumns.Contains(name))
            return NavBoolean(StubbedLocalizationResources());

        return name switch
        {
            "languageid" => _aovNavIntegerCreate!.Invoke(null, new object?[] { languageId }),
            "primarylanguageid" => _aovNavIntegerCreate!.Invoke(
                null, new object?[] { (int)_wlPrimaryLanguageId!.Invoke(null, new object?[] { culture })! }),
            "name" => Text(culture.EnglishName),
            "abbreviatedname" => Text(
                (string?)_wlAbbreviatedName!.Invoke(null, new object?[] { culture }) ?? string.Empty),
            // The OEM code page, not the ANSI one — BC reads TextInfo.OEMCodePage here.
            "primarycodepage" => Text(culture.TextInfo.OEMCodePage.ToString(CultureInfo.InvariantCulture)),
            "languagetag" => Text(culture.Name),
            _ => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false }),
        };
    }

    /// <summary>
    /// The value the six license-derived columns report, pending a mockable license.
    ///
    /// <para>PERMISSIVE BY CHOICE. BC has no no-license answer to copy — get_License() throws
    /// rather than returning anything — so this is not reproducing BC behaviour, it is
    /// deciding one. The runner exists so AL tests run without a license; answering "not
    /// permitted" would gate the business logic those tests exist to exercise, and turn a
    /// missing license into failures that say nothing about the AL under test.</para>
    ///
    /// <para>PROVISIONAL. A mockable license is a planned capability. When it lands, this
    /// method is the single place that changes — which is why the six columns route through it
    /// instead of each returning a literal. Declared, not silent: docs/limitations.md records
    /// it and tests/runner-extras/windows-language-license-stub asserts it.</para>
    /// </summary>
    private static bool StubbedLicensePermission() => true;

    /// <summary>
    /// The value the four installed-resource columns report ("STX File Exist", "ETX File
    /// Exist", "Help File Exist", "Localization Exist").
    ///
    /// <para>False, and for a different reason than the license columns: the runner genuinely
    /// installs no BC translation resources, so reporting none is a statement about this
    /// process. It still diverges from a service tier with localizations installed, so it is
    /// declared the same way — same docs section, same runner-extras suite — and behind its own
    /// seam so the two decisions can move independently.</para>
    /// </summary>
    private static bool StubbedLocalizationResources() => false;

    /// <summary>
    /// Resolve BC's own WindowsLanguageHelper. Everything it supplies is what BC's provider
    /// supplies, so a missing member is a BC shape change and says so rather than being
    /// silently reimplemented with a plausible-looking .NET equivalent.
    /// </summary>
    private static void EnsureWindowsLanguageHelperReflection()
    {
        if (_wlAllCultures != null) return;

        var navTypes = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        var helper = navTypes?.GetType("Microsoft.Dynamics.Nav.Types.WindowsLanguageHelper")
            ?? throw WindowsLanguageShapeGap(
                "Microsoft.Dynamics.Nav.Types.WindowsLanguageHelper was not found, so BC's own "
                + "culture list cannot be read. Reimplementing it from CultureInfo would answer a "
                + "different row set than the service tier");

        const BindingFlags Statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        var cultures = helper.GetField("AllCultures", Statics)?.GetValue(null) as CultureInfo[]
            ?? (helper.GetProperty("AllCultures", Statics)?.GetValue(null) as CultureInfo[]);

        MethodInfo? One(string methodName) => helper
            .GetMethods(Statics)
            .FirstOrDefault(m => m.Name == methodName
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType == typeof(CultureInfo));

        var languageId = One("LanguageId");
        var primaryLanguageId = One("PrimaryLanguageId");
        var abbreviatedName = One("AbbreviatedName");

        if (cultures == null || languageId == null || primaryLanguageId == null || abbreviatedName == null)
            throw WindowsLanguageShapeGap(
                "WindowsLanguageHelper does not expose the shape "
                + $"BC's own provider uses (AllCultures={cultures != null}, "
                + $"LanguageId={languageId != null}, PrimaryLanguageId={primaryLanguageId != null}, "
                + $"AbbreviatedName={abbreviatedName != null}). A BC shape change says so here "
                + "rather than being papered over");

        _wlLanguageId = languageId;
        _wlPrimaryLanguageId = primaryLanguageId;
        _wlAbbreviatedName = abbreviatedName;
        _wlAllCultures = cultures;
    }
}
