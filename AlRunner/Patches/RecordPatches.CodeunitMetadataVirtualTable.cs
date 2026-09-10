// RecordPatches.CodeunitMetadataVirtualTable — managed provider for the
// "CodeUnit Metadata" (2000000137) system virtual table.
//
// WHY THIS EXISTS (issue #2544)
//   CodeUnit Metadata is virtual on the service tier: one row per codeunit compiled into
//   the application, computed from that codeunit's own AL declaration. It routed to the
//   same empty in-memory store as every other table here, so:
//
//     CodeunitMetadata.Get(<any id>)  -> false, always
//     CodeunitMetadata.FindSet()      -> "There is no CodeUnit Metadata within the filter."
//
//   The first is a silent wrong answer, not an error. It is also the last missing member of
//   a family this file's siblings already cover: Table Metadata (2000000136),
//   Page Metadata (2000000138), Report Metadata (2000000139) and Report Data Items
//   (2000000203) all have managed providers here.
//
// WHAT REAL BC ANSWERS
//   Microsoft.Dynamics.Nav.Runtime.CodeUnitDataProvider (Ncl.dll) declares
//   TableId => 2000000137 and builds each row from NCLMetadata, keyed on field 1, in this
//   column order: the object number; the object name; NCLMetaCodeunit.TableId (AL's TableNo
//   property); IsSingleInstance, forced true for codeunit 1; Subtype; the owning app's id;
//   inherent permissions; inherent entitlements; TestType; RequiredTestIsolation; the
//   object's AL namespace. A codeunit whose metadata cannot be resolved is skipped, not
//   reported as an empty row — which is why the enumeration below drops an id it has no
//   name for instead of inserting a blank.
//
// WHERE THE ROWS COME FROM (two sources, neither invented)
//   1. Codeunits the runner compiles itself — parsed from their AL source
//      (RecordPatches.AlObjectDeclParser.cs: Name / TableNo / SingleInstance / Subtype).
//   2. Codeunits living in a PRECOMPILED dependency (Base Application, System Application,
//      ISV apps) — read from that .app's SymbolReference.json, which states the same four
//      properties (BcAppSymbolCache.Codeunits). This is the only route for an R2R app: it
//      ships no metadata XML.
//   Source-compiled codeunits win over symbol-derived ones for the same id — the source is
//   what this run actually compiled. Both feed through EnumerateKnownAlObjects, the same
//   shared inventory AllObj reads, so AllObj and CodeUnit Metadata cannot disagree about
//   which codeunits exist.
//
// COLUMNS FROM BC'S OWN METADATA DOCUMENT (#3606)
//   AL Namespace, InherentPermissions and InherentEntitlements are read off the document
//   BC's emitter produced for the codeunit — see
//   RecordPatches.CodeunitMetadataFromBcDocument.cs. A codeunit with no document keeps BC's
//   default for all three; that is every codeunit in a precompiled dependency, whose .app
//   ships no metadata XML, and every codeunit on a compile-cache HIT with no replayed
//   sidecar.
//
// COLUMNS STILL NOT IMPLEMENTED, AND WHY NONE IS WAITING ON THE CONVERSION ABOVE
//   App ID, TestType and RequiredTestIsolation get BC's own NavValue.GetDefaultNavValue.
//   App ID is not an object property at all (BC fills it from the PUBLISHING app, per-run
//   state a compiler cannot emit — the same data #2326 tracks for AllObj). TestType is
//   emitted 0 times in Base Application's 1,690 codeunit documents because BC DERIVES it
//   rather than reading it; deriving it is a separate claim about BC needing its own corpus
//   test. RequiredTestIsolation IS stated in the document, as TestIsolation — and reading it
//   is WRONG: a real service tier answers None for every codeunit, so BC's default is the
//   faithful answer and the document value is not. Eight cloud legs measured that (corpus PR
//   296) and Ncl.dll says why: the property BC's row builder reads is never assigned.
//   docs/codeunit-metadata-from-bc.md#requiredtestisolation.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (NCLMetaTable, NCLMetaField, NavValue, ReadOnlyRecordBuffer,
//   TempTableDataProvider), reached through the same helpers the AllObj / Table Metadata /
//   Page Metadata providers resolve. No AL business-logic body is touched.

using System.Collections.Concurrent;
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
    /// Category (2) for all four. One is a store-wiring gap; the other three are BC metadata
    /// shapes this file reads rather than owns. Refusing beats guessing an option ordinal: the
    /// ordinal is a stored column value, so a wrong guess mis-keys every row it writes and no
    /// test can see it.
    /// </remarks>
    internal static RunnerOutOfScopeException CodeunitMetadataShapeGap(string detail)
        => VirtualTableShapeGap("CodeUnit Metadata (virtual table 2000000137)", "codeunit-metadata-virtual-table", detail);

    internal const int CodeunitMetadataVirtualTableId = 2000000137;

    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, byte>>
        _cmvPopulatedByProvider = new();

    private static bool IsCodeunitMetadataVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == CodeunitMetadataVirtualTableId;

    /// <summary>
    /// One codeunit as CodeUnit Metadata exposes it. <see cref="TableNo"/> is already
    /// resolved to a real table id (0 only when the codeunit genuinely declares no
    /// <c>TableNo</c>, or when the declared table could not be resolved — which is
    /// reported, never silent; the same rule Table Metadata applies to LookupPageId).
    /// <see cref="Subtype"/> is the AL name as written (<c>Normal</c>, <c>Test</c>,
    /// <c>Install</c>, …), matched against the live option string by name at row-build time.
    /// </summary>
    private sealed record CodeunitMetaRow(int Id, string Name, int TableNo, bool SingleInstance, string Subtype);

    private static List<CodeunitMetaRow>? _codeunitMetaRows;
    // The .app term is RecordPatches' registration EPOCH, never _bcAppPaths.Count (#2888):
    // the registered set can SHRINK since #2755 / PR #2873, so a count cannot tell a set that
    // lost N entries and gained N different ones from the one it was built against — and in
    // --watch mode (same bundle, one edited file) that is the NORMAL case, not a corner. The
    // remaining terms stay counts and are sound as counts, because the dictionaries they count
    // are only ever cleared by ResetForReload, which bumps the epoch in the same breath.
    private static (int Epoch, int Decls) _codeunitMetaRowsBuiltFrom = (-1, -1);
    private static readonly object _codeunitMetaRowsLock = new();

    // Resolved once per process from the parsed CodeUnit Metadata metatable's own "Subtype"
    // field option string — same technique AllObj uses for its "Object Type" column.
    private static Dictionary<string, int>? _cmvSubtypeOrdinals;

    // The same column's own OptionString, kept so the ordinal can be resolved BEFORE a row is
    // handed to InsertVirtualRow — see PopulateCodeunitMetadataVirtualTable. Set in the same
    // breath as _cmvSubtypeOrdinals and read only after it.
    private static string? _cmvSubtypeOptionString;

    /// <summary>
    /// Populate the in-memory store behind CodeUnit Metadata (2000000137) with one row per
    /// codeunit the runner knows about. Idempotent per (provider, codeunit id); called on
    /// every handout so codeunits registered later in the run still show up.
    /// </summary>
    private static void PopulateCodeunitMetadataVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureReportMetadataReflection(metaTable);   // NavBoolean.Create(bool)
        EnsureDataAccessProviderReflection(dataAccess);
        var subtypeOrdinals = EnsureCodeunitSubtypeOrdinals(metaTable);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw CodeunitMetadataShapeGap("data access has no in-memory provider");

        var done = _cmvPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<int, byte>());

        foreach (var row in EnumerateKnownCodeunitMetadata())
        {
            if (!done.TryAdd(row.Id, 0)) continue;

            // Keep BOTH resolutions OUT of the per-field builder: a throw from inside
            // InsertVirtualRow escapes GetDataAccessForTable and no row of the table is served
            // at all (#3536, docs/limitations.md#codeunit-metadata-subtype). The document read
            // joins the subtype resolution here for exactly that reason — it refuses on a
            // malformed document and on a permission mask it cannot read, either of which
            // would otherwise take the whole table down. The `[warn]` tag is
            // load-bearing too — any other tag is dropped at default verbosity by Log.cs.
            int subtypeOrdinal;
            BcCodeunitDocumentValues? document;
            try
            {
                subtypeOrdinal = ResolveCodeunitSubtypeOrdinal(
                    subtypeOrdinals, _cmvSubtypeOptionString, row.Subtype, row.Id);
                document = TryReadCodeunitMetadataDocument(row.Id);
            }
            catch (RunnerOutOfScopeException ex)
            {
                Console.Error.WriteLine(
                    $"[warn] RecordPatches: CodeUnit Metadata has NO ROW for codeunit {row.Id} "
                    + $"\"{row.Name}\" — {ex.Message}. Every other "
                    + "codeunit is still reported, but AL asking for this one will fail to find "
                    + "it: Get() answers false and a FindSet/Count is one row short, with no "
                    + "error raised at the read.");
                continue;
            }

            InsertVirtualRow(provider, metaTable,
                new object[] { CodeunitMetadataVirtualTableId, row.Id, 0, 0 },
                field => BuildCodeunitMetadataValue(field, row, subtypeOrdinal, document));
        }
    }

    /// <summary>
    /// One column of a CodeUnit Metadata row, matched by the metatable's own FIELD NAME so
    /// the mapping tracks whatever the System package in the resolved artifact declares
    /// rather than a hardcoded field-number table.
    /// </summary>
    /// <param name="document">BC's own metadata document for this codeunit, already parsed, or
    /// null when none is registered for it. The three columns it states fall through to BC's
    /// default when it is null, which is the honest answer for a codeunit whose .app ships no
    /// metadata XML — never a value derived from something else.</param>
    private static object? BuildCodeunitMetadataValue(
        NCLMetaField field, CodeunitMetaRow row, int subtypeOrdinal,
        BcCodeunitDocumentValues? document)
    {
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(
            null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });
        object? Default() => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });

        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "id":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.Id });
            case "name":
                return Text(row.Name);
            case "tableno":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { row.TableNo });
            case "singleinstance":
                return NavBoolean(row.SingleInstance);
            case "subtype":
                // Already resolved by the caller, which is what keeps a refusal from escaping
                // mid-insert and taking the whole table with it (#3536).
                return _aovNavOptionCreate!.Invoke(null, new object?[]
                {
                    field.FieldOptionMetadata, subtypeOrdinal
                });
            case "alnamespace":
                return document == null ? Default() : Text(document.AlNamespace);
            case "inherentpermissions":
                return document == null ? Default() : Text(document.InherentPermissions);
            case "inherententitlements":
                return document == null ? Default() : Text(document.InherentEntitlements);
            default:
                return Default();
        }
    }

    /// <summary>
    /// AL's default <c>Subtype</c> for a codeunit that declares none. Pinned upstream by
    /// <c>Record_CodeunitMetadata_Get_DeclaredCodeunit_ReturnsMatchingRow</c> in the al-language
    /// corpus, which asserts <c>Subtype::Normal</c> for ALT Codeunit Meta Probe — a fixture
    /// that declares no <c>Subtype</c> — and is green on a real BC service tier.
    /// <para>Both row sources already substitute it before a row is built
    /// (<c>d.Subtype ?? "Normal"</c> in <see cref="EnumerateKnownCodeunitMetadata"/>), so this
    /// is the backstop, and the one place the substitution is stated as a MEMBER NAME resolved
    /// against the live column rather than as a position.</para>
    /// </summary>
    private const string AlDefaultCodeunitSubtype = "Normal";

    /// <summary>
    /// The one AL codeunit subtype the AL compiler does not carry into object metadata, and the
    /// member it collapses to. <c>Install</c> is a subtype AL accepts and
    /// <c>Microsoft.Dynamics.Nav.Types.CodeunitSubType</c> numbers 4, one past the last member
    /// this column names — but nothing ever puts a 4 in front of the column, because the
    /// compiler writes 0 for it.
    /// <para>Measured, not inferred, on Base Application 28.1.49838.53910: decoding every
    /// <c>NavCodeunitOptionsAttribute</c> in the package's own assemblies (1,690) and joining
    /// it to the same package's <c>SymbolReference.json</c> and AL sources, which record the
    /// DECLARED property —</para>
    /// <para><c>Subtype = Upgrade</c>: 28 codeunits, attribute value 3, 28 of 28.
    /// <c>Subtype = TestRunner</c>: 2 codeunits, attribute value 2, 2 of 2.
    /// <c>SubType = Install</c>: codeunits 3999, 5000 and 7582 — attribute value <b>0</b>, all
    /// three. Across all 1,690 the only values that occur are 0, 2 and 3; not one carries 4.</para>
    /// <para>A real service tier agrees:
    /// <c>Record_CodeunitMetadata_Get_InstallCodeunit_ReportsSubtypeNormal</c> in the
    /// al-language corpus reads an <c>Install</c> fixture through this column on eight Cloud
    /// legs and gets ordinal 0, <c>Subtype::Normal</c>, on every one — while a
    /// <c>Subtype = Test</c> codeunit read in the same run reports 1, so the column is not
    /// simply always Normal.</para>
    /// </summary>
    private const string AlSubtypeTheCompilerDoesNotEmit = "Install";

    /// <summary>
    /// The ordinal CodeUnit Metadata's <c>SubType</c> column carries for one codeunit: the
    /// declared member when the codeunit states the property,
    /// <see cref="AlDefaultCodeunitSubtype"/> when it does not,
    /// <see cref="AlDefaultCodeunitSubtype"/> again when it declares
    /// <see cref="AlSubtypeTheCompilerDoesNotEmit"/>, and a refusal when the column's option
    /// string does not know the name.
    /// <para>The lookup is the column's own <c>OptionString</c> and nothing else — no runtime
    /// enum is overlaid here, unlike Page Metadata's <c>PageType</c> (#3080). The enum DOES
    /// reach one member further, and reading only that far predicts 4 for an <c>Install</c>
    /// codeunit; a service tier says 0. The reason is upstream of the provider:
    /// <c>NCLMetaCodeunit.Subtype</c> returns <c>options?.Subtype</c> off the codeunit's
    /// <c>NavCodeunitOptionsAttribute</c> — what the compiler wrote, not what the AL author
    /// declared — through <c>GetValueOrDefault()</c>. See
    /// <see cref="AlSubtypeTheCompilerDoesNotEmit"/> for the measurement and
    /// RecordPatches.MetadataOptionEnumOrdinals.cs for the contrast with PageType.</para>
    /// <para>The translation has to live here rather than in either row source, because BOTH
    /// sources carry the declared name: the AL parser reads <c>Subtype = Install;</c> out of
    /// source, and BcAppSymbolCache reads <c>"Subtype": "Install"</c> out of
    /// SymbolReference.json (present there for all three Base Application codeunits above). One
    /// translation in front of one lookup keeps the two paths answering the same thing.</para>
    /// <para>A name the option string does not know is refused rather than defaulted (#3080),
    /// in both directions — declared miss, and default-member miss. Before this, a miss fell
    /// through to <c>NavValue.GetDefaultNavValue</c> — ordinal 0 — under a comment claiming the
    /// case could not arise. With the <c>Install</c> translation in front, the refusal can only
    /// fire on a subtype AL does not accept at all, which is what it is for.</para>
    /// <para>Split out of <c>BuildCodeunitMetadataValue</c> so the resolution can be driven with
    /// an option string the caller chooses; the cases that matter — a column that orders its
    /// members differently, or omits the default — are ones no single artifact can present.</para>
    /// </summary>
    /// <param name="ordinals">Member name (normalized) to ordinal, from
    /// <see cref="EnsureCodeunitSubtypeOrdinals"/> or
    /// <see cref="BuildMetadataOptionOrdinals(string?, Type?)"/> with a null enum.</param>
    /// <param name="optionString">The column's own option string, for the refusal message.</param>
    /// <param name="declaredSubtype">What the codeunit declares, or null/blank when it declares nothing.</param>
    /// <param name="codeunitId">The codeunit, for the refusal message.</param>
    internal static int ResolveCodeunitSubtypeOrdinal(
        IReadOnlyDictionary<string, int> ordinals, string? optionString, string? declaredSubtype, int codeunitId)
    {
        // What the compiler wrote, not what the author declared. Unconditional and in front of
        // the lookup, so an "Install" key that somehow reached the map — an overlaid enum, an
        // artifact whose column grew a fifth member — still cannot make this answer 4.
        var effectiveSubtype =
            string.Equals(NormalizeObjectTypeName(declaredSubtype ?? string.Empty),
                          NormalizeObjectTypeName(AlSubtypeTheCompilerDoesNotEmit), StringComparison.Ordinal)
                ? AlDefaultCodeunitSubtype
                : declaredSubtype;

        if (string.IsNullOrWhiteSpace(effectiveSubtype))
        {
            if (ordinals.TryGetValue(NormalizeObjectTypeName(AlDefaultCodeunitSubtype), out var defaultOrdinal))
                return defaultOrdinal;
            throw CodeunitMetadataShapeGap(
                $"codeunit {codeunitId} declares no Subtype, and the default for it "
                + $"('{AlDefaultCodeunitSubtype}') is not a member of that column's own option set "
                + $"('{optionString}')");
        }
        if (ordinals.TryGetValue(NormalizeObjectTypeName(effectiveSubtype), out var ordinal))
            return ordinal;
        // Name the value LOOKED UP, and only when it differs from the declared one: claiming
        // the translation unconditionally makes the message false for a value it never touched.
        var translated = !string.Equals(effectiveSubtype, declaredSubtype, StringComparison.Ordinal);
        throw CodeunitMetadataShapeGap(
            $"codeunit {codeunitId} declares Subtype = '{declaredSubtype}'"
            + (translated
                ? $", which this resolver looks up as '{effectiveSubtype}' because the AL compiler "
                  + $"emits '{AlSubtypeTheCompilerDoesNotEmit}' as '{AlDefaultCodeunitSubtype}', and that"
                : ", which")
            + $" is not a member of that column's own option set ('{optionString}')"
            // Why "not in the option string" is the right test for THIS column and the wrong
            // one for Page Metadata's PageType. A standing fact, not an event.
            + (translated
                ? string.Empty
                : $". The one subtype AL accepts that this column does not name — "
                  + $"'{AlSubtypeTheCompilerDoesNotEmit}' — is looked up as "
                  + $"'{AlDefaultCodeunitSubtype}' before it reaches here, so a miss is not an "
                  + "AL codeunit subtype at all"));
    }

    /// <summary>
    /// Every codeunit the runner has real metadata for: source-parsed codeunits of the app
    /// under test and of any source-compiled dependency first, then codeunits declared by
    /// the SymbolReference.json of every registered precompiled dependency .app.
    /// </summary>
    private static List<CodeunitMetaRow> EnumerateKnownCodeunitMetadata()
    {
        var generation = (BcAppRegistrationEpoch, _parsedObjectDecls.Count);
        if (_codeunitMetaRows != null && _codeunitMetaRowsBuiltFrom == generation) return _codeunitMetaRows;
        lock (_codeunitMetaRowsLock)
        {
            generation = (BcAppRegistrationEpoch, _parsedObjectDecls.Count);
            if (_codeunitMetaRows != null && _codeunitMetaRowsBuiltFrom == generation) return _codeunitMetaRows;

            var rows = new Dictionary<int, CodeunitMetaRow>();
            var unresolvedTables = new List<string>();

            int ResolveTableNo(string? reference, int codeunitId)
            {
                if (string.IsNullOrWhiteSpace(reference)) return 0;   // declares none — truthful 0
                if (int.TryParse(reference, out var literal) && literal > 0) return literal;
                var resolved = ResolveTableIdByName(reference);
                if (resolved > 0) return resolved;
                unresolvedTables.Add($"codeunit {codeunitId} TableNo -> table '{reference}'");
                return 0;
            }

            // 1. Codeunits the runner source-compiled.
            foreach (var d in _parsedObjectDecls.Values)
            {
                if (NormalizeObjectTypeName(d.Kind) != "codeunit" || d.Id <= 0) continue;
                rows[d.Id] = new CodeunitMetaRow(
                    d.Id, d.Name,
                    ResolveTableNo(d.TableNo, d.Id),
                    // Codeunit 1 is single-instance in BC whatever it declares — see
                    // CodeUnitDataProvider, which ORs `item.Key == 1` into the flag.
                    d.SingleInstance || d.Id == 1,
                    d.Subtype ?? "Normal");
            }

            // 2. Codeunits declared by precompiled dependency .app packages.
            foreach (var symbol in EnumerateBcAppCodeunitSymbols())
            {
                if (rows.ContainsKey(symbol.Id)) continue;   // source-compiled wins
                rows[symbol.Id] = new CodeunitMetaRow(
                    symbol.Id, symbol.Name,
                    ResolveTableNo(symbol.TableNo, symbol.Id),
                    symbol.SingleInstance || symbol.Id == 1,
                    symbol.Subtype ?? "Normal");
            }

            // Loud, never silent (#3540). This is the runner answering a column WRONG on
            // purpose, not progress: the row's TableNo reads 0, which is also the truthful
            // answer for a codeunit declaring none, so the read cannot tell the two apart and
            // nothing else in the run records which one it was. `[warn]` is the severity shape
            // Log.cs exempts by design; `[RecordPatches]` is dropped at default verbosity along
            // with its 379 lines of ordinary chatter, which is where this sentence used to go.
            if (unresolvedTables.Count > 0)
                Console.Error.WriteLine(
                    $"[warn] RecordPatches: CodeUnit Metadata: {unresolvedTables.Count} declared TableNo "
                    + "reference(s) could not be resolved to a table id, so those rows answer TableNo = 0 — "
                    + "the same value a codeunit that declares no TableNo answers, and AL branching on "
                    + "TableNo will fail to tell the two apart: "
                    + string.Join("; ", unresolvedTables.Take(10))
                    + (unresolvedTables.Count > 10 ? $" (+{unresolvedTables.Count - 10} more)" : string.Empty));

            _codeunitMetaRows = rows.Values.ToList();
            _codeunitMetaRowsBuiltFrom = generation;
            return _codeunitMetaRows;
        }
    }

    /// <summary>
    /// Codeunits declared by the registered precompiled dependency .app packages, read off
    /// the SAME <c>Objects</c> list AllObj enumerates — so the two tables cannot disagree
    /// about which codeunits a dependency contains. The codeunit-only properties ride on
    /// <see cref="BcAppSymbolCache.ObjectSymbol"/>; see its doc comment.
    /// </summary>
    /// <remarks>
    /// #3143: NOT swallowed — see RecordPatches.DependencyAppSymbolWalk.cs. Reads the SAME
    /// surface AllObj does, through the same walk, so the two tables cannot now disagree
    /// about which dependency contributed codeunits either.
    /// </remarks>
    private static IEnumerable<BcAppSymbolCache.ObjectSymbol> EnumerateBcAppCodeunitSymbols()
    {
        foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols("objects (CodeUnit Metadata)"))
            foreach (var o in symbols.Objects)
                if (o.Id > 0 && NormalizeObjectTypeName(o.Kind) == "codeunit")
                    yield return o;
    }

    /// <summary>
    /// Read the "Subtype" option field's ordinals out of the parsed CodeUnit Metadata
    /// metatable's own NCLOptionMetadata.OptionString, matched by name — never a hardcoded
    /// table, so the mapping tracks whatever the System package in the resolved artifact
    /// declares. Mirrors Page Metadata's EnsurePageTypeOrdinals for its "PageType" column.
    /// <para>The option string is the ONLY source for this column, which is not true of Page
    /// Metadata's PageType sibling. BC's CodeUnitDataProvider does write
    /// GetOptionValue(5, (int)metaCodeunit.Subtype) — the ordinal of its own CodeunitSubType
    /// enum, which carries Install one past this column's last member — but the value reaching
    /// it is the one the AL compiler wrote, and the compiler emits no Install. Measured, and
    /// confirmed on a service tier: see ResolveCodeunitSubtypeOrdinal (#3080).</para>
    /// </summary>
    private static Dictionary<string, int> EnsureCodeunitSubtypeOrdinals(NCLMetaTable metaTable)
    {
        if (_cmvSubtypeOrdinals != null) return _cmvSubtypeOrdinals;

        var field = (GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => NormalizeObjectTypeName(f.FieldName ?? string.Empty) == "subtype")
            ?? throw CodeunitMetadataShapeGap("metatable has no \"Subtype\" field");

        var optionMetadata = field.FieldOptionMetadata
            ?? throw CodeunitMetadataShapeGap("\"Subtype\" carries no option metadata");

        // Deliberately no runtime-enum overlay: this column is filled from what the AL compiler
        // wrote, and the compiler never writes a value past the members the column names. See
        // ResolveCodeunitSubtypeOrdinal and RecordPatches.MetadataOptionEnumOrdinals.cs.
        var map = BuildMetadataOptionOrdinals(optionMetadata.OptionString, bcRuntimeEnum: null);
        if (map.Count == 0)
            throw CodeunitMetadataShapeGap("\"Subtype\" option string is empty");

        _cmvSubtypeOptionString = optionMetadata.OptionString;
        _cmvSubtypeOrdinals = map;
        return map;
    }
}
