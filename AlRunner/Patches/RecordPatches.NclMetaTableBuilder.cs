// RecordPatches.NclMetaTableBuilder — turns ParsedTable into a real NCLMetaTable.
//
// NCLMetaTable is BC's runtime table-metadata object. It's normally built by
// NavGlobal.NCLMetadata from compiled .app metadata; we don't have that, so we
// reflectively call its NonPublic CreateFromMetaTable factory with a
// hand-constructed Microsoft.Dynamics.Nav.Types.Metadata.MetaTable. The data
// classes (MetaTable / MetaField / MetaKey / FieldMetadataRelation) live in
// Types.dll and have public ctors with many named/optional parameters — we
// resolve them by parameter name and fall back to defaults / zero-values for
// any we don't care about.
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Positive-result cache: maps tableId → CLR Type for "Record<id>" subclasses of NavRecord.
    // The uncached form walks every loaded assembly's full type table on every call
    // (NavRecordHandle_CreateTarget fires it for every record handle materialization), which
    // dominated the profile on bucket-1 bundled (~72% inclusive). We cache only HITS — a
    // negative result can later become positive once the test assembly loads, so misses fall
    // through to the scan every time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Type> _recordTypeCache = new();

    internal static Type? FindRecordType(int id)
    {
        if (_recordTypeCache.TryGetValue(id, out var cached)) return cached;
        var name = $"Record{id}";
        // Prefer the current test assembly: on a server reload of the same bundle
        // a same-named Record<id> from the previous assembly is still loaded (.NET
        // cannot unload it) and would otherwise win the scan, serving stale trigger
        // / business-logic IL.
        var preferred = BcRuntime.CurrentTestAssembly;
        if (preferred != null)
        {
            var hit = FindRecordTypeIn(preferred, name);
            if (hit != null) { _recordTypeCache[id] = hit; return hit; }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == preferred) continue;
            var hit = FindRecordTypeIn(asm, name);
            if (hit != null) { _recordTypeCache[id] = hit; return hit; }
        }
        return null;
    }

    private static Type? FindRecordTypeIn(System.Reflection.Assembly asm, string name)
    {
        try
        {
            return Array.Find(asm.GetTypes(),
                x => x.Name == name && typeof(NavRecord).IsAssignableFrom(x));
        }
        catch { return null; }
    }

    internal static NCLMetaTable? GetOrBuildNCLMetaTable(int tableId)
        => (NCLMetaTable?)_metaTableCache.GetOrAdd(tableId, BuildNCLMetaTable);

    /// <summary>
    /// The field's AL-declared Caption, straight from the parsed table source — the same
    /// <see cref="ParsedField.Caption"/> this builder feeds into NCLMetaField's captionML at
    /// construction (see BuildMetaField below). Deliberately bypasses NCLMetaField's own
    /// FieldCaption getter: that getter is JmpHooked (see
    /// RecordPatches.NCLMetaField_get_FieldCaption / BcRuntime's session-free FieldCaption
    /// patch) to unconditionally answer the field's NAME, because its real implementation
    /// dereferences session/language-provider state the skeleton runtime never populates —
    /// and that hook cannot tell "no declared Caption" apart from "declared Caption we chose
    /// not to read", so nothing downstream of it can answer that question either. TestPage
    /// field Caption() (#1777) needs the real answer, so it reads the parse-time source
    /// directly instead of going through the hook.
    /// Null means the field declares no Caption (or the table was never parsed at all —
    /// e.g. a base-app table not pulled through TryPopulateParsedTableFromBcApps yet).
    /// </summary>
    internal static string? TryGetParsedFieldCaption(int tableId, int fieldNo)
    {
        if (!_parsedTables.TryGetValue(tableId, out var parsed)) return null;
        foreach (var f in parsed.Fields)
            if (f.FieldId == fieldNo)
                return string.IsNullOrEmpty(f.Caption) ? null : f.Caption;
        return null;
    }

    private static NCLMetaTable? BuildNCLMetaTable(int tableId)
    {
        if (!_parsedTables.TryGetValue(tableId, out var parsed))
        {
            // Fallback: try to parse the table source from a registered BC dependency
            // .app. Tests under tests/spike-a-baseapp invoke Record types defined in
            // Base App / System App whose AL source isn't part of the test suite's
            // own src/ tree. The .app NAVX zip ships the AL source, which the
            // existing TryParseTableFile can consume verbatim.
            if (!TryPopulateParsedTableFromBcApps(tableId)
                || !_parsedTables.TryGetValue(tableId, out parsed))
                return null;
        }
        if (_tMetaTable == null || _mCreateFromMetaTable == null) return null;

        try
        {
            // Build MetaField[] — include a synthetic timestamp field (id=0, BigInteger)
            // and the BC system fields: SystemId (2000000000), SystemCreatedAt (2000000001),
            // SystemCreatedBy (2000000002), SystemModifiedAt (2000000003), SystemModifiedBy
            // (2000000004). These are required for system-field access via FieldRef and RecordRef.
            var timestampParsed       = new ParsedField(0,          "timestamp",         "BigInteger", 0);
            var systemIdParsed        = new ParsedField(2000000000, "SystemId",          "Guid",       0);
            var systemCreatedAtParsed = new ParsedField(2000000001, "SystemCreatedAt",   "DateTime",   0);
            var systemCreatedByParsed = new ParsedField(2000000002, "SystemCreatedBy",   "Guid",       0);
            var systemModifiedAtParsed= new ParsedField(2000000003, "SystemModifiedAt",  "DateTime",   0);
            var systemModifiedByParsed= new ParsedField(2000000004, "SystemModifiedBy",  "Guid",       0);
            // Merge any tableextension fields for this base table.
            // De-duplicate by field id: precompiled .app SymbolReference.json sometimes lists
            // extension fields both in the base table's Tables[].Fields entry AND in
            // TableExtensions[].Fields (e.g. BC BaseApp table 242 "Source Code Setup" already
            // carries its extension fields in Tables[]). Duplicating them corrupts the
            // NCLMetaTable field layout that R2R-precompiled BC code has baked offsets for.
            // Only append ext fields whose id is NOT already present in the base table's own list.
            var extFields = _parsedExtensionFields.TryGetValue(parsed.TableName.ToLowerInvariant(), out var ef)
                ? ef : Enumerable.Empty<ParsedField>();
            var baseFieldIds = new HashSet<int>(parsed.Fields.Select(f => f.FieldId));
            var extFieldsNew = extFields.Where(f => !baseFieldIds.Contains(f.FieldId));
            var allParsed = new[] { timestampParsed }.Concat(parsed.Fields)
                .Concat(extFieldsNew)
                .Concat(new[] { systemIdParsed, systemCreatedAtParsed, systemCreatedByParsed,
                                systemModifiedAtParsed, systemModifiedByParsed }).ToArray();
            var fields = allParsed.Select((f, idx) =>
                BuildMetaField(f, idx, parsed.PkFieldIds.Contains(f.FieldId), parsed)).ToArray();

            // Build primary key MetaKey via FieldMetadataRelation[]
            var pkRelations = parsed.PkFieldIds
                .Select(fid => BuildFieldMetadataRelation(fid))
                .ToArray();
            var pkKey = BuildMetaKey("PK", pkRelations, clustered: true);

            // Build secondary key MetaKey objects
            var allKeys = new List<object> { pkKey };
            if (parsed.SecondaryKeys != null)
            {
                foreach (var sk in parsed.SecondaryKeys)
                {
                    var skRelations = sk.FieldIds
                        .Select(fid => BuildFieldMetadataRelation(fid))
                        .ToArray();
                    if (skRelations.Length > 0)
                        allKeys.Add(BuildMetaKey(sk.Name, skRelations, clustered: false));
                }
            }

            // Build MetaTable via named-parameter ctor.  The public ctor takes many
            // named params with defaults; we resolve by name and fall back to defaults.
            var defaultMetaTable = CallMetaTableCtor(tableId, parsed.TableName, fields, allKeys.ToArray(),
                parsed.IsTableTypeTemporary, parsed.DataPerCompany);
            if (defaultMetaTable == null) return null;

            // NavAppGroup.BaseGroup
            var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            var tAppGroup = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroup")!;
            var baseGroup = tAppGroup.GetProperty("BaseGroup",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? tAppGroup.GetField("BaseGroup",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            var built = (NCLMetaTable?)_mCreateFromMetaTable.Invoke(null,
                new object?[] { defaultMetaTable, baseGroup });

            // §O — mark metadataLoaded=true on every NCLMetaTable we construct, so when
            // NCLMetadata.GetMetaApplicationObjectInternal later loops and re-checks
            // `!nclMetaApplicationObject.MetadataLoaded`, it sees true and skips Populate()
            // (which would NRE on our hand-built instance — no NCLObjectXmlMetadataLoader,
            // no NavAppMetadata, etc.).
            if (built != null)
            {
                // Hand BC back the MetaTable this NCLMetaTable was built from.
                //
                // BC answers a whole family of metadata questions — field captions
                // (NCLMetaField.CreateCaptionStrings), tooltips, field groups — by calling
                // NCLMetaTable.GetMetaTableOriginal() and reading the ORIGINAL MetaTable.
                // That returns `metadataAppGroupMetaTable?.Item`, which only BC's own
                // AssignFromMetaTable populates, and the runner constructs NCLMetaTable
                // through a different path. Left null, those lookups do not fail loudly —
                // they take a silent fallback. For a caption that fallback is the field
                // NAME, so a field declaring `Caption = 'New Column Name'` answered
                // `NewColumnName`: a plausible-looking wrong value, which is worse than an
                // empty one. Assign it exactly as AssignFromMetaTable does.
                AssignMetaTableOriginal(built, defaultMetaTable);

                EnsureCachePopulatorReflection();
                if (_fNCLMetaAppObjMetadataLoaded != null)
                    AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, built, true);

                // W-8b A-prime: poke a real NavTableTriggerEventHandler into the
                // tableTriggerEventHandler field. NCLMetaTable.TableTriggerEventHandler /
                // TriggerEventHandler are simple field-getter properties — even when their
                // call sites are R2R-inlined into NavRecord.InsertAsync, the inlined code
                // reads our field. EventSubscriberPatches.InjectAll later attaches per-event
                // NavEventSubscription objects to its NavEventScope.registeredSubscriptions.
                var triggerHandler = AlRunner.Patches.EventSubscriberPatches
                    .CreateTableTriggerEventHandler();
                if (triggerHandler != null)
                {
                    var f = built.GetType().GetField("tableTriggerEventHandler",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null)
                        AlRunner.Infrastructure.FieldPoke.SetInstance(f, built, triggerHandler);
                }

                // Field-level OnValidate/OnLookup trigger wiring is deferred to
                // SetTestAssembly time — the AL-emitted Record<id> CLR type that
                // carries the [FieldTriggerHandler] attributes doesn't exist in the
                // AppDomain yet at NCLMetaTable build time (build runs during
                // AddSourceDir, before AL emit). See WireFieldTriggerHandlersAll().

                // For AL `Enum "X"`-typed fields the upstream BC factory builds
                // either a plain NCLOptionMetadataWithCaptions (when EnumTypeId==0)
                // or an NCLFieldEnumMetadata that chains to NavGlobal.MetadataProvider
                // (NREs on skeleton). Both paths produce wrong results for
                // `FieldRef.GetEnumValueCaption/NameFromOrdinalValue(ordinal)` on
                // sparse AL enums (e.g. value(0), value(5), value(10)) because the
                // base GetCaptionFromIndex/GetOptionFromIndex treats the AL ordinal
                // as a 0..Count-1 array index. We swap in AlEnumOptionMetadata which
                // mirrors NCLEnumMetadata semantics (search indexes[] for matching
                // ordinal) using data captured by BcCompiler at AL emit time.
                FixupEnumFieldOptionMetadata(built, parsed, extFields);

                // Register any AutoIncrement fields so NavRecord_ALInsertAsync3 assigns
                // counters. `allParsed` rather than `parsed.Fields`: a tableextension may
                // declare the AutoIncrement field, and since #1711 that property survives the
                // parse. Registering only base-table fields would be the silent half-fix —
                // the NCLMetaField would say autoIncrement=true while no counter ever
                // advanced, so every Insert left the field at 0 and the second row collided.
                foreach (var f in allParsed)
                    if (f.IsAutoIncrement)
                        AlRunner.BcRuntime.RegisterAutoIncrementField(tableId, f.FieldId);
            }
            return built;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[RecordPatches] BuildNCLMetaTable({tableId}) failed: {inner.GetType().Name}: {inner.Message}");
            if (inner.StackTrace != null)
                Console.Error.WriteLine(inner.StackTrace.Split('\n')[0]);
            return null;
        }
    }

    private static object? CallMetaTableCtor(int id, string name, object[] fields, object[] allKeys,
        bool isTableTypeTemporary, bool isDataPerCompany)
    {
        if (_tMetaTable == null) return null;
        var ctor = _tMetaTable.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var ps = ctor.GetParameters();

        // Build arg array, filling in defaults where possible.
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name == "id") { args[i] = id; continue; }
            if (p.Name == "name") { args[i] = name; continue; }
            if (p.Name == "fields")
            {
                // ImmutableArray<MetaField>
                args[i] = MakeImmutableArray(_tMetaField!, fields);
                continue;
            }
            if (p.Name == "keys")
            {
                args[i] = MakeImmutableArray(_tMetaKey!, allKeys);
                continue;
            }
            // MetaTable's own default for this parameter is false; AL's default for a table is
            // TRUE. Left at the ctor default, every runner-built table claimed to be
            // not-per-company, and BC's RecordImplementation.ChangeCompany returns true
            // without ever looking at the company name.
            if (p.Name == "isDataPerCompany") { args[i] = isDataPerCompany; continue; }
            if (p.Name == "tableType" && p.ParameterType.IsEnum)
            {
                var enumName = isTableTypeTemporary ? "Temporary" : "Normal";
                args[i] = Enum.TryParse(p.ParameterType, enumName, ignoreCase: true, out var tableType)
                    ? tableType
                    : (p.HasDefaultValue ? p.DefaultValue : Activator.CreateInstance(p.ParameterType));
                continue;
            }
            if (p.Name == "fieldsById")
            {
                // Build ImmutableDictionary<int, MetaField> from fields
                var immDictType = typeof(ImmutableDictionary<,>).MakeGenericType(typeof(int), _tMetaField!);
                var builderMethod = immDictType.GetMethod("CreateRange",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, object>>) }, null);
                // Use ImmutableDictionary.CreateRange<TKey,TValue>(IEnumerable<KVP>)
                var createRangeMethod = typeof(ImmutableDictionary).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateRange" && m.GetParameters().Length == 1)?
                    .MakeGenericMethod(typeof(int), _tMetaField!);
                if (createRangeMethod != null)
                {
                    // Build KeyValuePair<int, MetaField>[] from fields
                    var kvpType = typeof(System.Collections.Generic.KeyValuePair<,>).MakeGenericType(typeof(int), _tMetaField!);
                    var kvpArray = Array.CreateInstance(kvpType, fields.Length);
                    var kvpCtor = kvpType.GetConstructor(new[] { typeof(int), _tMetaField! })!;
                    for (int j = 0; j < fields.Length; j++)
                    {
                        var fid = (int)_tMetaField!.GetProperty("Id")!.GetValue(fields[j])!;
                        kvpArray.SetValue(kvpCtor.Invoke(new[] { (object)fid, fields[j] }), j);
                    }
                    args[i] = createRangeMethod.Invoke(null, new object[] { kvpArray })!;
                }
                else
                {
                    args[i] = null;
                }
                continue;
            }
            // Use parameter default if available.
            if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
                continue;
            }
            // Provide safe zero-values.
            args[i] = p.ParameterType.IsValueType
                ? Activator.CreateInstance(p.ParameterType)
                : null;
        }
        return ctor.Invoke(args);
    }

    /// <summary>
    /// Populate <c>NCLMetaTable.metadataAppGroupMetaTable</c> so
    /// <c>GetMetaTableOriginal()</c> answers with the MetaTable we built the instance from,
    /// mirroring BC's own <c>AssignFromMetaTable</c>. Best-effort: if the field or the
    /// MetadataExtension&lt;T&gt; shape moves, BC keeps its existing fallbacks rather than
    /// the table failing to build.
    /// </summary>
    private static void AssignMetaTableOriginal(object nclMetaTable, object metaTable)
    {
        try
        {
            var field = nclMetaTable.GetType().GetField("metadataAppGroupMetaTable",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) return;
            // MetadataExtension<MetaTable>(item) — the wrapper BC stores it in.
            var ctor = field.FieldType.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 1
                                     && c.GetParameters()[0].ParameterType.IsInstanceOfType(metaTable));
            if (ctor == null) return;
            AlRunner.Infrastructure.FieldPoke.SetInstance(
                field, nclMetaTable, ctor.Invoke(new[] { metaTable }));
        }
        catch { /* BC keeps its name-based fallbacks */ }
    }

    /// <summary>
    /// A single-language <c>MultiLanguage</c> holding <paramref name="text"/> as ENU — the
    /// shape BC itself builds when it needs one from a plain string
    /// (<c>MultiLanguage.Parse("ENU=" + …)</c> in NCLMetaField). Returns null if the type or
    /// method is not where we expect, so a metadata-shape change degrades to BC's field-name
    /// fallback rather than throwing during table construction.
    /// </summary>
    private static object? BuildEnuMultiLanguage(Type mlType, string text)
    {
        try
        {
            var parse = mlType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { typeof(string) }, modifiers: null);
            return parse?.Invoke(null, new object[] { "ENU=" + text });
        }
        catch { return null; }
    }

    private static object BuildMetaField(ParsedField f, int index, bool isPk, ParsedTable? parentTable = null)
    {
        var ctor = _tMetaField!.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];

        // Build calcFormula object up-front if needed (FlowField).
        object? calcFormulaObj = (f.IsFlowField && f.CalcFormula != null && parentTable != null)
            ? BuildMetaCalcFormula(f.CalcFormula, parentTable)
            : null;

        // TableRelation → ImmutableArray<MetaFieldRelation>, one element per arm. NCLMetaField
        // turns each into the NCLMetaFieldRelation that GetReferencingRelations' reverse
        // index — and therefore Rename propagation (#1730, #1737) — is built from.
        object? relationsObj = f.RelationArms is { Count: > 0 }
            ? BuildMetaFieldRelations(f.RelationArms, parentTable, f.FieldName)
            : null;

        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name == "id") { args[i] = f.FieldId; continue; }
            if (p.Name == "name") { args[i] = f.FieldName; continue; }
            if (p.Name == "type") { args[i] = MapNavType(f.TypeName); continue; }
            if (p.Name == "length") { args[i] = f.Length; continue; }
            if (p.Name == "autoIncrement" && f.IsAutoIncrement) { args[i] = true; continue; }
            // Caption: BC answers Rec.FieldCaption(n) and the Field virtual table's
            // "Field Caption" from NCLMetaField.CreateCaptionStrings, which reads the
            // ORIGINAL MetaField's merged caption MultiLanguage and falls back to the field
            // NAME when there is none. Leaving both unset therefore did not produce an empty
            // caption — it produced a WRONG one that looks plausible (`NewColumnName` where
            // the AL declared `New Column Name`), which is the harder failure to notice.
            // A field with no declared Caption stays unset so BC's name fallback stands.
            if (p.Name == "caption" && !string.IsNullOrEmpty(f.Caption)) { args[i] = f.Caption; continue; }
            if (p.Name == "captionML" && !string.IsNullOrEmpty(f.Caption))
            {
                // The ML type comes from the parameter itself rather than a hard-coded name:
                // BC's caption read is GetMergedCaptionMultiLanguage, so the plain `caption`
                // string above is NOT what it consults — without this the declared caption is
                // stored and then ignored.
                var ml = BuildEnuMultiLanguage(p.ParameterType, f.Caption!);
                if (ml != null) { args[i] = ml; continue; }
            }
            if (p.Name == "enabled") { args[i] = (bool?)true; continue; }
            if (p.Name == "fieldClass" && _tFieldClass != null && (f.IsFlowField || f.IsFlowFilter))
            {
                // #1716 — FlowFilter must reach the metadata as FlowFilter. NCLMetaTable
                // orders FlowFilter fields after the Normal ones (so field indexes shift,
                // which is fine — every consumer here is metadata-driven), and both
                // DataHelper.PassesFieldFilters and FlowFieldsHelper key their behaviour off
                // this enum. Declaring it Normal is what made a flow filter exclude its own
                // table's rows and made `field("Date Filter")` a blank-equality test.
                args[i] = Enum.Parse(_tFieldClass, f.IsFlowField ? "FlowField" : "FlowFilter");
                continue;
            }
            if (p.Name == "calcFormula" && calcFormulaObj != null)
            {
                args[i] = calcFormulaObj;
                continue;
            }
            // ObsoleteState / ObsoleteReason (#1780): the Field virtual table's
            // FieldDataProvider.GetFieldRecordBuffer reads these off the NCLMetaField that
            // CreateFromMetaTable builds from THIS MetaField — a field declared
            // `ObsoleteState = Removed` reported `No` here for every field because the ctor
            // args were never passed, so every field fell through to MetaField's own "No"
            // default. Only pass the non-default member name; "No" undeclared or declared
            // both leave the ctor's own default standing (identical either way).
            if (p.Name == "obsoleteState" && _tObsoleteState != null
                && !string.Equals(f.ObsoleteState, "No", StringComparison.OrdinalIgnoreCase))
            {
                args[i] = Enum.Parse(_tObsoleteState, f.ObsoleteState, ignoreCase: true);
                continue;
            }
            if (p.Name == "obsoleteReason" && !string.IsNullOrEmpty(f.ObsoleteReason))
            {
                args[i] = f.ObsoleteReason;
                continue;
            }
            if (p.Name == "relations" && relationsObj != null)
            {
                args[i] = relationsObj;
                continue;
            }
            // ValidateTableRelation: AL's default is true (MetaField's own null-default also
            // resolves to true), so only the explicit opt-out needs passing. It matters even
            // when the relation itself was not captured — the flag alone is what suppresses
            // rename propagation on a field whose relation a tableextension adds later.
            if (p.Name == "validateRelation" && !f.RelationValidate)
            {
                args[i] = (bool?)false;
                continue;
            }
            if (p.Name == "optionString" && !string.IsNullOrEmpty(f.OptionMembers))
            {
                args[i] = f.OptionMembers;
                continue;
            }
            if (p.Name == "initValue" && !string.IsNullOrEmpty(f.InitValueText))
            {
                // Pass the raw AL InitValue expression text. BC stores it on
                // NCLMetaField.initialValueText and evaluates it via
                // ALSystemVariable.EvaluateIntoNavValue at Init() time. For Text/Code
                // fields the AL compiler stores the literal *without* the surrounding
                // single quotes, so strip them here when present.
                var iv = f.InitValueText;
                var tn = f.TypeName.Trim();
                if ((tn.StartsWith("Text", StringComparison.OrdinalIgnoreCase)
                        || tn.StartsWith("Code", StringComparison.OrdinalIgnoreCase))
                    && iv.Length >= 2 && iv.StartsWith("'") && iv.EndsWith("'"))
                {
                    iv = iv.Substring(1, iv.Length - 2).Replace("''", "'");
                }
                // Enum-typed fields: a quoted-identifier InitValue (e.g. the blank
                // value `InitValue = " ";`, or any member name needing quotes) is
                // captured by the source parser with its quotes intact, but enum
                // member names are captured UNQUOTED at emit time (BcCompiler reads
                // IEnumValueSymbol.Name) and NavOptionEvaluator.InternalEvaluate
                // matches the text exactly (GetIndexFromCaption/GetIndexFromOption,
                // ordinal compare, no trimming) — so `" "` (three chars) could never
                // match the member named ` ` and every Init/Insert of the table threw
                // NavNCLInvalidOptionStringException (#1674). Real BC's compiled
                // metadata stores the member name unquoted — Base App symbol packages
                // carry {"Name":"InitValue","Value":" "} for exactly this shape — so
                // stripping the outer quotes (and un-doubling AL's "" escape) is
                // observably equivalent to real BC. Symbol-loaded fields already
                // arrive unquoted and pass through unchanged; classic Option fields
                // are untouched (their OptionMembers are captured with the same
                // quoting as their InitValue, so the pair stays internally
                // consistent).
                else if (tn.StartsWith("Enum", StringComparison.OrdinalIgnoreCase)
                    && iv.Length >= 2 && iv.StartsWith("\"") && iv.EndsWith("\""))
                {
                    iv = iv.Substring(1, iv.Length - 2).Replace("\"\"", "\"");
                }
                args[i] = iv;
                continue;
            }
            if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
            args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }
        return ctor.Invoke(args)!;
    }

    /// <summary>
    /// Builds the ImmutableArray&lt;MetaFieldRelation&gt; for a field's parsed TableRelation —
    /// one element per arm of an if/else chain (#1737); the plain shape is one condition-less
    /// arm. If-conditions become MetaConditions keyed on the REFERENCING table's fields,
    /// where() entries become MetaFilters keyed on the related table's fields; NavRecord's
    /// own UpdateReferencesOnRenameAsync then evaluates both exactly as real BC does —
    /// including the container-verified asymmetry that an else arm carries no conditions.
    /// The parser hands each arm 1 or 2 name parts; two parts are AL-ambiguous between
    /// `Table.Field` and `Namespace.Table`, so resolution tries `Table.Field` first and falls
    /// back to treating the last part as a namespace-qualified table. Any name — table,
    /// condition field, or filter field — that does not resolve refuses the WHOLE relation
    /// (null, pre-#1730 behaviour: no propagation) rather than guessing: fieldId 0 means
    /// "the primary key", and a half-built arm would propagate renames real BC does not.
    /// </summary>
    private static object? BuildMetaFieldRelations(
        List<ParsedRelationArm> arms, ParsedTable? referencingTable, string forFieldName)
    {
        if (_tMetaFieldRelation == null || _tMetaFilter == null || _tMetaCondition == null
            || _tFilterType == null)
            return null;

        ParsedTable? Resolve(string name) =>
            _parsedTables.Values.FirstOrDefault(t =>
                string.Equals(t.TableName, name, StringComparison.OrdinalIgnoreCase))
            ?? TryPopulateParsedTableByName(name);

        var relationObjects = new List<object>();
        foreach (var arm in arms)
        {
            var target = Resolve(arm.TableName);
            int fieldId = 0;
            if (target != null && arm.FieldName != null)
            {
                var tf = target.Fields.FirstOrDefault(x =>
                    string.Equals(x.FieldName, arm.FieldName, StringComparison.OrdinalIgnoreCase));
                if (tf != null)
                {
                    fieldId = tf.FieldId;
                }
                else
                {
                    // `A.B` where A is a table but B is not its field — try B as the table
                    // (`Namespace.Table` reading) before giving up.
                    target = null;
                }
            }
            if (target == null && arm.FieldName != null)
            {
                target = Resolve(arm.FieldName);
                fieldId = 0;
            }
            if (target == null)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] TableRelation target '{arm.TableName}{(arm.FieldName != null ? "." + arm.FieldName : "")}' did not resolve to a parsed table — relation dropped");
                return null;
            }

            var conditionObjects = new List<object>();
            foreach (var c in arm.Conditions)
            {
                var localField = referencingTable?.Fields.FirstOrDefault(x =>
                    string.Equals(x.FieldName, c.SourceFieldName, StringComparison.OrdinalIgnoreCase));
                if (localField == null)
                {
                    Console.Error.WriteLine(
                        $"[RecordPatches] TableRelation on '{forFieldName}': condition field '{c.SourceFieldName}' not found on '{referencingTable?.TableName}' — relation dropped");
                    return null;
                }
                conditionObjects.Add(BuildMetaCondition(localField.FieldId,
                    c.Kind == ParsedCalcFilterKind.Const ? "CONST" : "FILTER", c.Value ?? ""));
            }

            var filterObjects = new List<object>();
            foreach (var w in arm.Filters)
            {
                var srcField = target.Fields.FirstOrDefault(x =>
                    string.Equals(x.FieldName, w.SourceFieldName, StringComparison.OrdinalIgnoreCase));
                if (srcField == null)
                {
                    Console.Error.WriteLine(
                        $"[RecordPatches] TableRelation on '{forFieldName}': where() field '{w.SourceFieldName}' not found on '{target.TableName}' — relation dropped");
                    return null;
                }
                filterObjects.Add(BuildMetaFilter(srcField.FieldId,
                    w.Kind == ParsedCalcFilterKind.Const ? "CONST" : "FILTER", w.Value ?? ""));
            }

            var ctor = _tMetaFieldRelation.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .First(c => c.GetParameters().Any(p => p.Name == "tableId"));
            var ps = ctor.GetParameters();
            var args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                if (p.Name == "tableId") { args[i] = target.TableId; continue; }
                if (p.Name == "tableName") { args[i] = target.TableName; continue; }
                if (p.Name == "fieldId") { args[i] = fieldId; continue; }
                if (p.Name == "filters") { args[i] = MakeImmutableArray(_tMetaFilter!, filterObjects.ToArray()); continue; }
                if (p.Name == "conditions") { args[i] = MakeImmutableArray(_tMetaCondition!, conditionObjects.ToArray()); continue; }
                if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
                args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
            }
            relationObjects.Add(ctor.Invoke(args)!);
        }
        return MakeImmutableArray(_tMetaFieldRelation, relationObjects.ToArray());
    }

    /// <summary>
    /// One <c>MetaCondition</c> — the metadata form of a single <c>if (...)</c> arm
    /// condition on a TableRelation. Mirrors <see cref="BuildMetaFilter"/>: the type name is
    /// a <c>FilterType</c> member (CONST / FILTER — FIELD is not a condition shape, BC's own
    /// NCLMetaFilter.CreateFromMetaCondition throws on it), and the value is the literal /
    /// filter-expression TEXT, which NCLMetaFilterConst / NCLMetaFilterExpression evaluate
    /// against the referencing field's own type exactly as for a user-typed filter.
    /// </summary>
    private static object BuildMetaCondition(int fieldId, string conditionTypeName, string conditionValue)
    {
        // MetaCondition(int fieldId, FilterType conditionType, string conditionValue)
        var ctor = _tMetaCondition!.GetConstructors()
            .First(c => c.GetParameters().Any(p => p.Name == "fieldId"));
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name == "fieldId") { args[i] = fieldId; continue; }
            if (p.Name == "conditionType") { args[i] = Enum.Parse(_tFilterType!, conditionTypeName); continue; }
            if (p.Name == "conditionValue") { args[i] = conditionValue; continue; }
            if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
            args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }
        return ctor.Invoke(args)!;
    }

    private static object? BuildMetaCalcFormula(ParsedCalcFormula cf, ParsedTable parentTable)
    {
        if (_tMetaCalcFormula == null || _tMetaFilter == null || _tFilterType == null) return null;

        // Resolve source table by name
        var srcTable = _parsedTables.Values.FirstOrDefault(t =>
            string.Equals(t.TableName, cf.SourceTableName, StringComparison.OrdinalIgnoreCase));
        if (srcTable == null)
        {
            // Lazily materialise the source table from the BC .app symbol index by name.
            // Base App FlowFields commonly reference a sibling Base App table that wasn't
            // parsed yet when this table was built (e.g. Purchase Line "Matched Order Lines"
            // → "Matched Order Line"). Without this the formula falls back to the null
            // EmptyFormula, which later throws on EmptyFormula.SourceField (table 0).
            srcTable = TryPopulateParsedTableByName(cf.SourceTableName);
        }
        if (srcTable == null)
        {
            Console.Error.WriteLine($"[RecordPatches] BuildMetaCalcFormula: source table '{cf.SourceTableName}' not found in parsed tables");
            return null;
        }

        // Resolve source field (for Sum/Lookup/Average/Min/Max)
        int srcFieldId = 0;
        if (cf.SourceFieldName != null)
        {
            var srcField = srcTable.Fields.FirstOrDefault(f =>
                string.Equals(f.FieldName, cf.SourceFieldName, StringComparison.OrdinalIgnoreCase));
            if (srcField == null)
            {
                Console.Error.WriteLine($"[RecordPatches] BuildMetaCalcFormula: source field '{cf.SourceFieldName}' not found in table '{cf.SourceTableName}'");
                return null;
            }
            srcFieldId = srcField.FieldId;
        }

        // Build a MetaFilter per where-condition. BC turns each one into the matching
        // NCLMetaFilter subclass on its own (NCLMetaFilterCollection.CreateFromMetaFilter):
        // FIELD → NCLMetaFilterField, CONST → NCLMetaFilterConst, FILTER →
        // NCLMetaFilterExpression. The const literal / filter expression is handed over as
        // TEXT and evaluated by BC against the source field's own type, exactly as it is for
        // a user-typed filter.
        var filterObjects = new List<object>();
        foreach (var filter in cf.Filters)
        {
            var srcFilterField = srcTable.Fields.FirstOrDefault(f =>
                string.Equals(f.FieldName, filter.SourceFieldName, StringComparison.OrdinalIgnoreCase));
            if (srcFilterField == null)
            {
                Console.Error.WriteLine($"[RecordPatches] BuildMetaCalcFormula: filter source field '{filter.SourceFieldName}' not found in '{cf.SourceTableName}'");
                continue;
            }

            switch (filter.Kind)
            {
                case ParsedCalcFilterKind.Field:
                    var parentFilterField = parentTable.Fields.FirstOrDefault(f =>
                        string.Equals(f.FieldName, filter.ParentFieldName, StringComparison.OrdinalIgnoreCase));
                    if (parentFilterField == null)
                    {
                        Console.Error.WriteLine($"[RecordPatches] BuildMetaCalcFormula: filter parent field '{filter.ParentFieldName}' not found in '{parentTable.TableName}'");
                        continue;
                    }
                    // #1716: the two mode flags ride along on the FIELD filter.
                    // NCLMetaFilterField.CreateFromMetaFilter turns them into
                    // NCLMetaFilterModes, and FlowFieldsHelper then reads the parent field as
                    // a filter expression (ValueIsFilter) and/or keeps only the upper bound
                    // of the resolved range (OnlyMaxLimit). Nothing here interprets them.
                    filterObjects.Add(BuildMetaFilter(srcFilterField.FieldId, "FIELD",
                        parentFilterField.FieldId.ToString(),
                        filter.ValueIsFilter, filter.OnlyMaxLimit));
                    break;

                case ParsedCalcFilterKind.Const:
                    filterObjects.Add(BuildMetaFilter(srcFilterField.FieldId, "CONST", filter.Value ?? ""));
                    break;

                case ParsedCalcFilterKind.Filter:
                    filterObjects.Add(BuildMetaFilter(srcFilterField.FieldId, "FILTER", filter.Value ?? ""));
                    break;
            }
        }

        // Construct MetaCalcFormula(int tableId, int fieldId, string flowFieldType, bool reverseSign, ImmutableArray<MetaFilter> filters)
        var ctor = _tMetaCalcFormula.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name == "tableId") { args[i] = srcTable.TableId; continue; }
            if (p.Name == "fieldId") { args[i] = srcFieldId; continue; }
            if (p.Name == "flowFieldType") { args[i] = cf.FormulaType.ToUpperInvariant(); continue; }
            // #1708: `CalcFormula = -sum(...)`. BC's own NCLMetaCalculationFormula.NegateResult
            // is built from this flag, and FlowFieldPatches negates the aggregate with it.
            if (p.Name == "reverseSign") { args[i] = cf.Negated; continue; }
            if (p.Name == "filters")
            {
                args[i] = MakeImmutableArray(_tMetaFilter!, filterObjects.ToArray());
                continue;
            }
            if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
            args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }
        try
        {
            return ctor.Invoke(args);
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[RecordPatches] BuildMetaCalcFormula ctor failed: {inner.GetType().Name}: {inner.Message}");
            return null;
        }
    }

    /// <summary>
    /// One <c>MetaFilter</c> — BC's metadata form of a single CalcFormula where-condition.
    /// <para><paramref name="filterTypeName"/> is a <c>FilterType</c> member (FIELD / CONST /
    /// FILTER) and <paramref name="filterValue"/> is what that type means: the parent field's
    /// id for FIELD, the literal's text for CONST, the filter expression's text for FILTER.
    /// This is the same shape BC's compiled table metadata carries, which is why NCL can build
    /// the right NCLMetaFilter subclass from it without any further help.</para>
    /// <para><paramref name="valueIsFilter"/> / <paramref name="onlyMaxLimit"/> are AL's
    /// <c>field(filter(X))</c> and <c>field(upperlimit(X))</c> (#1716). They only ever apply
    /// to a FIELD filter and are passed straight through to
    /// <c>NCLMetaFilterField.CreateFromMetaFilter</c>, which is the only code that decides
    /// what they mean.</para>
    /// </summary>
    private static object BuildMetaFilter(int sourceFieldId, string filterTypeName, string filterValue,
        bool valueIsFilter = false, bool onlyMaxLimit = false)
    {
        // MetaFilter(int fieldId, FilterType filterType, string filterValue, int filterGroup,
        //            bool valueIsFilter, bool onlyMaxLimit)
        var ctor = _tMetaFilter!.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name == "fieldId") { args[i] = sourceFieldId; continue; }
            if (p.Name == "filterType") { args[i] = Enum.Parse(_tFilterType!, filterTypeName); continue; }
            if (p.Name == "filterValue") { args[i] = filterValue; continue; }
            if (p.Name == "valueIsFilter") { args[i] = valueIsFilter; continue; }
            if (p.Name == "onlyMaxLimit") { args[i] = onlyMaxLimit; continue; }
            if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
            args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }
        return ctor.Invoke(args)!;
    }

    private static object BuildMetaKey(string name, object[] fieldRelations, bool clustered)
    {
        var ctor = _tMetaKey!.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name == "name" || p.Name == "keyName") { args[i] = name; continue; }
            if (p.Name == "clustered") { args[i] = clustered; continue; }
            if (p.Name == "enabled") { args[i] = (bool?)true; continue; }
            if (p.Name == "fieldRelations")
            {
                args[i] = MakeImmutableArray(_tFieldMetadataRelation!, fieldRelations);
                continue;
            }
            if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
            args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }
        return ctor.Invoke(args)!;
    }

    private static object BuildFieldMetadataRelation(int fieldId)
    {
        var ctor = _tFieldMetadataRelation!.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name == "id") { args[i] = fieldId; continue; }
            if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
            args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }
        return ctor.Invoke(args)!;
    }

    private static object MakeImmutableArray(Type elementType, object[] elements)
    {
        // ImmutableArray<T>.Empty.AddRange(elements)
        var arrType = typeof(ImmutableArray<>).MakeGenericType(elementType);
        var empty = arrType.GetField("Empty", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        if (elements.Length == 0) return empty;

        // Use ImmutableArray.CreateRange<T>(IEnumerable<T>)
        var createRange = typeof(ImmutableArray).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "CreateRange" && m.GetParameters().Length == 1)
            .MakeGenericMethod(elementType);
        // Cast elements to IEnumerable<elementType>
        var typedArray = Array.CreateInstance(elementType, elements.Length);
        for (int i = 0; i < elements.Length; i++) typedArray.SetValue(elements[i], i);
        return createRange.Invoke(null, new object[] { typedArray })!;
    }

    private static object MapNavType(string typeName)
    {
        // Map AL type name → NavType enum. Use `ignoreCase: true` because the
        // BC NavType enum uses inconsistent casing (`BLOB`, `GUID`, but
        // `Code`/`Text`/`Decimal`...) and AL field-type strings may come from
        // either AL source (`Blob`) or BC metadata (`BLOB`). Parsing
        // case-sensitively against the wrong casing throws ArgumentException
        // and quarantines the whole table.
        if (_tNavType == null) return 0;
        var n = typeName.Trim().ToUpperInvariant();
        // Code[N] / Text[N] — strip the length suffix
        if (n.StartsWith("CODE")) return Enum.Parse(_tNavType, "Code", ignoreCase: true);
        if (n.StartsWith("TEXT")) return Enum.Parse(_tNavType, "Text", ignoreCase: true);
        if (n == "INTEGER") return Enum.Parse(_tNavType, "Integer", ignoreCase: true);
        if (n == "DECIMAL") return Enum.Parse(_tNavType, "Decimal", ignoreCase: true);
        if (n == "BOOLEAN") return Enum.Parse(_tNavType, "Boolean", ignoreCase: true);
        if (n == "BYTE") return Enum.Parse(_tNavType, "Byte", ignoreCase: true);
        if (n == "DATE") return Enum.Parse(_tNavType, "Date", ignoreCase: true);
        if (n == "TIME") return Enum.Parse(_tNavType, "Time", ignoreCase: true);
        if (n == "DATETIME") return Enum.Parse(_tNavType, "DateTime", ignoreCase: true);
        if (n == "DATEFORMULA") return Enum.Parse(_tNavType, "DateFormula", ignoreCase: true);
        if (n == "DURATION") return Enum.Parse(_tNavType, "Duration", ignoreCase: true);
        if (n.StartsWith("BIGINTEGER") || n == "BIGINT") return Enum.Parse(_tNavType, "BigInteger", ignoreCase: true);
        if (n == "BIGTEXT") return Enum.Parse(_tNavType, "BigText", ignoreCase: true);
        if (n == "SECRETTEXT") return Enum.Parse(_tNavType, "SecretText", ignoreCase: true);
        if (n == "GUID") return Enum.Parse(_tNavType, "GUID", ignoreCase: true);
        if (n == "BLOB") return Enum.Parse(_tNavType, "BLOB", ignoreCase: true);
        if (n == "MEDIA") return Enum.Parse(_tNavType, "Media", ignoreCase: true);
        if (n == "MEDIASET") return Enum.Parse(_tNavType, "MediaSet", ignoreCase: true);
        if (n == "RECORDID") return Enum.Parse(_tNavType, "RecordID", ignoreCase: true);
        if (n == "TABLEFILTER") return Enum.Parse(_tNavType, "TableFilter", ignoreCase: true);
        if (n.StartsWith("OPTION")) return Enum.Parse(_tNavType, "Option", ignoreCase: true);
        // AL `Enum "<name>"` is stored at runtime as NavType.Option — the
        // generated code calls ValidateExpectedType(fieldNo, NavType.Option)
        // when reading enum-typed record fields, so the metadata side must
        // match. Without this, the cluster of TestField/Read*EnumField tests
        // throws NavObjectDefinitionChangedException
        // ("old type: Option, new type: Text") via NavRecord.ValidateExpectedType.
        if (n.StartsWith("ENUM")) return Enum.Parse(_tNavType, "Option", ignoreCase: true);
        return Enum.Parse(_tNavType, "Text", ignoreCase: true); // fallback
    }

    // ── Field-level trigger handler wiring ────────────────────────────────────
    //
    // Real BC, given a compiled .app:
    //   1. NCLMetaApplicationObject.Populate reads NavAppMetadata to learn that
    //      Record<id>.OnValidate_<fieldName> is a [FieldTriggerHandler(OnValidate, fieldNo)]
    //      method.
    //   2. It builds a `FieldTriggerHandler<Record<id>>` (Ncl class, not delegate)
    //      that wraps an `Action<T>` or `Func<T, ValueTask>` calling that method.
    //   3. It assigns that wrapper to `NCLMetaField.EventTriggerDataValue.ValidateHandler`.
    //   4. NavRecord.ValidateAsync(metaField, …) reads ValidateHandler and invokes it.
    //
    // We do (1)–(3) here by reflecting on the AL-emitted Record CLR type. The
    // attribute is `Microsoft.Dynamics.Nav.Runtime.FieldTriggerHandlerAttribute`
    // with constructor (FieldTriggerType, int fieldNo). We do the same for OnLookup
    // for symmetry; OnBeforeValidate/OnAfterValidate aren't AL-author-visible so
    // we don't wire them.
    private static Type? _tFieldTriggerHandlerAttr;
    private static Type? _tFieldTriggerType;
    private static Type? _tFieldTriggerHandler1;
    private static Type? _tEventTriggerData;
    private static FieldInfo? _fEventTriggerDataValueBacking;
    private static FieldInfo? _fValidateHandlerBacking;
    private static FieldInfo? _fLookupHandlerBacking;
    private static PropertyInfo? _pOnBeforeValidateHandlers;
    private static PropertyInfo? _pOnAfterValidateHandlers;
    private static Type? _tFieldTriggerHandlerListClosed;

    private static void EnsureFieldTriggerReflection()
    {
        if (_tFieldTriggerHandlerAttr != null) return;
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        _tFieldTriggerHandlerAttr = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.FieldTriggerHandlerAttribute");
        _tFieldTriggerType = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.FieldTriggerType");
        _tFieldTriggerHandler1 = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.FieldTriggerHandler`1");
        var nclMetaField = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaField")!;
        _tEventTriggerData = nclMetaField.GetNestedType("EventTriggerData", BindingFlags.Public | BindingFlags.NonPublic);
        _fEventTriggerDataValueBacking = nclMetaField.GetField("<EventTriggerDataValue>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (_tEventTriggerData != null)
        {
            _fValidateHandlerBacking = _tEventTriggerData.GetField("<ValidateHandler>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _fLookupHandlerBacking = _tEventTriggerData.GetField("<LookupHandler>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            // List<FieldTriggerHandler<NavApplicationObjectBase>> handler lists for the
            // tableextension OnBeforeValidate / OnAfterValidate field triggers (NavRecord's
            // ValidateFieldAsync runs beforeHandlers, then the base OnValidate, then afterHandlers).
            _pOnBeforeValidateHandlers = _tEventTriggerData.GetProperty("OnBeforeValidateHandlers",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _pOnAfterValidateHandlers = _tEventTriggerData.GetProperty("OnAfterValidateHandlers",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }
        var navAobj = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase");
        if (_tFieldTriggerHandler1 != null && navAobj != null)
            _tFieldTriggerHandlerListClosed = typeof(List<>).MakeGenericType(
                _tFieldTriggerHandler1.MakeGenericType(navAobj));
    }

    /// <summary>
    /// Walk every NCLMetaTable we built and (idempotently) wire its
    /// EventTriggerDataValue to the matching AL-emitted [FieldTriggerHandler]
    /// methods on the now-loaded Record CLR type. Called from
    /// BcRuntime.SetTestAssembly once the test assembly is in the AppDomain.
    /// </summary>
    public static void WireFieldTriggerHandlersAll()
    {
        PrewarmRecordTypeCache();
        foreach (var kvp in _metaTableCache)
        {
            // Skip tables already SUCCESSFULLY wired by a previous call.
            // WireFieldTriggerHandlersAll used to run once per bundle (one
            // SetTestAssembly call); emitting one module per app.json (see AppGroup)
            // now calls it once per app against the SAME growing _metaTableCache, so
            // an unconditional re-wire made this O(apps x cache-size) — measured at
            // 689ms average x 118 calls = 81s of a 111s run on tests/runner-extras (68
            // apps). This guard is the same one WireFieldTriggerHandlersForTable
            // already uses for its lazy per-table path.
            //
            // MUST check success, not just attempt: pre-registration walks every
            // suite's src/ up front, so _metaTableCache already holds tables for
            // suites whose assembly hasn't loaded yet when an EARLIER suite's
            // WireFieldTriggerHandlersAll runs. WireFieldTriggerHandlers silently
            // no-ops (FindRecordType returns null) for those — marking them wired
            // anyway would permanently skip the real wiring once that suite's own
            // assembly does load. Only a table whose type actually resolved is
            // recorded, so later calls retry the rest.
            if (_fieldTriggersWiredTables.ContainsKey(kvp.Key)) continue;
            if (kvp.Value is NCLMetaTable mt && WireFieldTriggerHandlers(mt, kvp.Key))
                _fieldTriggersWiredTables.TryAdd(kvp.Key, 1);
        }
    }

    // Tables whose field-trigger handlers have been wired, so the per-table call on the hot
    // record-materialisation path is a no-op after the first time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _fieldTriggersWiredTables = new();

    /// <summary>
    /// Wire a single table's field-trigger handlers (the field OnValidate/OnLookup methods on the
    /// Record CLR type) onto its NCLMetaField EventTriggerData. Called from the record-materialisation
    /// chokepoints so a table built LAZILY at runtime — notably a precompiled BaseApp table whose
    /// metatable did not exist when WireFieldTriggerHandlersAll ran at SetTestAssembly — still gets
    /// its field-validate logic wired. Without this, validating a field on such a table never runs
    /// the field's OnValidate body (e.g. Purchase Header."Buy-from Vendor No." copying Vendor.Name
    /// into "Buy-from Vendor Name"). Idempotent + guarded so the hot path pays the cost once.
    /// </summary>
    internal static void WireFieldTriggerHandlersForTable(int tableId, object metaTableObj)
    {
        if (metaTableObj is not NCLMetaTable mt) return;
        if (_fieldTriggersWiredTables.ContainsKey(tableId)) return; // already wired
        // Called from the record-materialisation path, where the table's own Record
        // CLR type is expected to already be loaded — but only record success (see
        // WireFieldTriggerHandlersAll) so a stale/premature call cannot permanently
        // block a later, correct one.
        if (WireFieldTriggerHandlers(mt, tableId))
            _fieldTriggersWiredTables.TryAdd(tableId, 1);
    }

    // Single AppDomain walk that finds every NavRecord-derived "Record<N>" type and bulk-populates
    // _recordTypeCache. Without this, each WireFieldTriggerHandlers(tableId) call triggers its own
    // cold-miss full-AppDomain walk via FindRecordType — O(N×M) where N=tables, M=total types.
    // Prewarm is O(M); subsequent FindRecordType calls become O(1) dictionary hits.
    private static void PrewarmRecordTypeCache()
    {
        // TryAdd is first-wins, so visit the current test assembly first: on a
        // server reload its freshly-emitted Record<id> types must win over the
        // same-named types still loaded from the previous bundle assembly.
        var preferred = BcRuntime.CurrentTestAssembly;
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        var ordered = preferred != null
            ? new[] { preferred }.Concat(loaded.Where(a => a != preferred))
            : (IEnumerable<System.Reflection.Assembly>)loaded;
        foreach (var asm in ordered)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }
            foreach (var t in types)
            {
                var name = t.Name;
                if (name.Length < 7 || !name.StartsWith("Record", StringComparison.Ordinal)) continue;
                if (!int.TryParse(name.AsSpan(6), out var id)) continue;
                if (!typeof(NavRecord).IsAssignableFrom(t)) continue;
                _recordTypeCache.TryAdd(id, t);
            }
        }
    }

    // Returns whether wiring actually resolved the table's Record CLR type — NOT just
    // whether the call completed. FindRecordType returning null (the table's own
    // assembly is not loaded into the AppDomain yet) is the common case for a suite
    // wired before its own module has loaded: pre-registration walks every suite's
    // src/ up front, so WireFieldTriggerHandlersAll for app #1 also sees tables that
    // belong to app #50, whose assembly hasn't loaded. The caller must NOT record a
    // false "wired" for that case, or the real wiring (once the assembly does load)
    // is silently skipped by the wired-tables guard.
    private static bool WireFieldTriggerHandlers(NCLMetaTable built, int tableId)
    {
        try
        {
            EnsureFieldTriggerReflection();
            if (_tFieldTriggerHandlerAttr == null || _tFieldTriggerType == null
                || _tFieldTriggerHandler1 == null || _tEventTriggerData == null
                || _fEventTriggerDataValueBacking == null)
                return false;

            var recordType = FindRecordType(tableId);
            if (recordType == null) return false;

            // Map fieldNo -> (validateMethod, lookupMethod)
            var byField = new Dictionary<int, (MethodInfo? validate, MethodInfo? lookup)>();
            foreach (var mi in recordType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                     | BindingFlags.Instance | BindingFlags.Static
                                                     | BindingFlags.DeclaredOnly))
            {
                var attrs = mi.GetCustomAttributes(_tFieldTriggerHandlerAttr, inherit: false);
                if (attrs.Length == 0) continue;
                foreach (var a in attrs)
                {
                    var fieldNo = (int)_tFieldTriggerHandlerAttr.GetProperty("FieldNo")!.GetValue(a)!;
                    var ttObj = _tFieldTriggerHandlerAttr.GetProperty("TriggerType")!.GetValue(a)!;
                    var ttName = Enum.GetName(_tFieldTriggerType, ttObj);
                    byField.TryGetValue(fieldNo, out var pair);
                    if (ttName == "OnValidate") pair.validate = mi;
                    else if (ttName == "OnLookup") pair.lookup = mi;
                    byField[fieldNo] = pair;
                }
            }
            // recordType WAS resolved even if it declares no field triggers — that is a
            // real, complete result (nothing to wire on the base table itself), not a
            // "try again later" case. A tableextension may still contribute field
            // triggers, so extension wiring must run regardless (issue #1835: the
            // usual PTE-extends-a-triggerless-table shape skipped it entirely).
            //
            // "Complete" also leans on the EXTENSION assemblies being loaded, which
            // this return value cannot see. That holds at both call sites: bundled
            // mode runs WireFieldTriggerHandlersAll once after every assembly has
            // loaded (see BcRuntime.SetTestAssembly's wireFieldTriggers contract),
            // and lazily-built precompiled tables wire at record-materialisation
            // time, after all loads. The one shape this does not cover — a bundle
            // reload introducing a NEW tableextension on a table already recorded in
            // _fieldTriggersWiredTables — is skipped by the wired-tables guard, and
            // always has been for the OnBefore/OnAfterValidate lists too.
            if (byField.Count == 0)
            {
                WireExtensionValidateHandlers(built, tableId);
                return true;
            }

            // For each field with handler(s): build EventTriggerData, set ValidateHandler/LookupHandler,
            // poke onto NCLMetaField.EventTriggerDataValue backing field.
            foreach (var kvp in byField)
            {
                var fieldNo = kvp.Key;
                NCLMetaField? metaField;
                try { metaField = built.GetFieldByNo(fieldNo, /*trapError:*/ false); }
                catch { continue; }
                if (metaField == null) continue;

                var existing = _fEventTriggerDataValueBacking.GetValue(metaField);
                var etd = existing ?? Activator.CreateInstance(_tEventTriggerData)!;

                if (kvp.Value.validate != null && _fValidateHandlerBacking != null)
                {
                    var handler = BuildFieldTriggerHandler(kvp.Value.validate, recordType);
                    if (handler != null)
                        AlRunner.Infrastructure.FieldPoke.SetInstance(_fValidateHandlerBacking, etd, handler);
                }
                if (kvp.Value.lookup != null && _fLookupHandlerBacking != null)
                {
                    var handler = BuildFieldTriggerHandler(kvp.Value.lookup, recordType);
                    if (handler != null)
                        AlRunner.Infrastructure.FieldPoke.SetInstance(_fLookupHandlerBacking, etd, handler);
                }
                AlRunner.Infrastructure.FieldPoke.SetInstance(_fEventTriggerDataValueBacking, metaField, etd);
            }

            WireExtensionValidateHandlers(built, tableId);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RecordPatches] WireFieldTriggerHandlers({tableId}) failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Wire the OnBeforeValidate / OnAfterValidate field triggers declared in a tableextension's
    /// `modify(field) { trigger OnBeforeValidate(); trigger OnAfterValidate(); }` block into the
    /// base table's NCLMetaField before/after handler lists, and the OnValidate / OnLookup
    /// triggers of fields the tableextension ADDS onto those fields' ValidateHandler /
    /// LookupHandler slots (issue #1835: the AL compiler emits a new ext field's trigger on
    /// TableExtension{id}, never on Record{id}, so base-table wiring alone leaves the slot
    /// empty and Rec.Validate silently skips the trigger). NavRecord.ValidateFieldAsync runs
    /// the before/after lists around the field's ValidateHandler; each handler's HandlerType
    /// is the TableExtension{id} CLR type so InvokeFieldTriggerHandler dispatches to the
    /// registered extension instance (see RegisterParsedTableExtensions). Valid AL cannot
    /// collide an extension OnValidate with a base-table one — modify() blocks only accept
    /// OnBefore/OnAfterValidate, and an extension can only declare OnValidate on fields it
    /// adds. Idempotent: rebuilds the lists from scratch on every call
    /// (WireFieldTriggerHandlersAll may run on each bundle (re)load).
    /// </summary>
    private static void WireExtensionValidateHandlers(NCLMetaTable built, int tableId)
    {
        if (_pOnBeforeValidateHandlers == null || _pOnAfterValidateHandlers == null
            || _tFieldTriggerHandlerListClosed == null || _fEventTriggerDataValueBacking == null
            || _tEventTriggerData == null)
            return;
        if (!_parsedTables.TryGetValue(tableId, out var parsed)) return;
        if (!_extensionIdsByBaseTable.TryGetValue(parsed.TableName.ToLowerInvariant(), out var extIds)
            || extIds.Count == 0)
            return;

        // fieldNo → (before handlers, after handlers), preserving extension declaration order.
        var beforeByField = new Dictionary<int, List<object>>();
        var afterByField = new Dictionary<int, List<object>>();
        // fieldNo → single handler for the ext-added field's own OnValidate / OnLookup.
        var validateByField = new Dictionary<int, object>();
        var lookupByField = new Dictionary<int, object>();

        foreach (var extId in extIds)
        {
            var extType = FindTableExtensionType(extId);
            if (extType == null) continue;

            foreach (var mi in extType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attrs = mi.GetCustomAttributes(_tFieldTriggerHandlerAttr!, inherit: false);
                if (attrs.Length == 0) continue;
                foreach (var a in attrs)
                {
                    var fieldNo = (int)_tFieldTriggerHandlerAttr!.GetProperty("FieldNo")!.GetValue(a)!;
                    var ttObj = _tFieldTriggerHandlerAttr.GetProperty("TriggerType")!.GetValue(a)!;
                    var ttName = Enum.GetName(_tFieldTriggerType!, ttObj);
                    if (ttName == "OnValidate" || ttName == "OnLookup")
                    {
                        var single = BuildFieldTriggerHandler(mi, extType);
                        if (single == null) continue;
                        if (ttName == "OnValidate") validateByField[fieldNo] = single;
                        else lookupByField[fieldNo] = single;
                        continue;
                    }
                    var target = ttName == "OnBeforeValidate" ? beforeByField
                               : ttName == "OnAfterValidate" ? afterByField
                               : null;
                    if (target == null) continue;
                    var handler = BuildFieldTriggerHandler(mi, extType);
                    if (handler == null) continue;
                    if (!target.TryGetValue(fieldNo, out var list))
                        target[fieldNo] = list = new List<object>();
                    list.Add(handler);
                }
            }
        }

        var fieldsToWire = new HashSet<int>(beforeByField.Keys);
        fieldsToWire.UnionWith(afterByField.Keys);
        fieldsToWire.UnionWith(validateByField.Keys);
        fieldsToWire.UnionWith(lookupByField.Keys);
        foreach (var fieldNo in fieldsToWire)
        {
            NCLMetaField? metaField;
            try { metaField = built.GetFieldByNo(fieldNo, /*trapError:*/ false); }
            catch { continue; }
            if (metaField == null) continue;

            var etd = _fEventTriggerDataValueBacking.GetValue(metaField)
                      ?? Activator.CreateInstance(_tEventTriggerData)!;
            if (beforeByField.TryGetValue(fieldNo, out var before))
                _pOnBeforeValidateHandlers.SetValue(etd, ToHandlerList(before));
            if (afterByField.TryGetValue(fieldNo, out var after))
                _pOnAfterValidateHandlers.SetValue(etd, ToHandlerList(after));
            if (validateByField.TryGetValue(fieldNo, out var extValidate) && _fValidateHandlerBacking != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(_fValidateHandlerBacking, etd, extValidate);
            if (lookupByField.TryGetValue(fieldNo, out var extLookup) && _fLookupHandlerBacking != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(_fLookupHandlerBacking, etd, extLookup);
            AlRunner.Infrastructure.FieldPoke.SetInstance(_fEventTriggerDataValueBacking, metaField, etd);
        }
    }

    // Box a List<object> of FieldTriggerHandler<NavApplicationObjectBase> into the strongly
    // typed List<FieldTriggerHandler<NavApplicationObjectBase>> the EventTriggerData expects.
    private static object ToHandlerList(List<object> handlers)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(_tFieldTriggerHandlerListClosed!)!;
        foreach (var h in handlers) list.Add(h);
        return list;
    }

    private static Type? _tNavApplicationObjectBase;

    private static object? BuildFieldTriggerHandler(MethodInfo target, Type recordType)
    {
        // FieldTriggerHandler<T> is closed by BC over T = NavApplicationObjectBase
        // (the base class for all AL-emitted Record/Codeunit/etc. types) — this is
        // visible because NCLMetaField+EventTriggerData.<ValidateHandler>k__BackingField
        // is typed FieldTriggerHandler<NavApplicationObjectBase>. The handlerType
        // property carries the concrete (Record100003) type, and the Action/Func
        // takes the base instance and is responsible for any cast.
        if (_tNavApplicationObjectBase == null)
        {
            var navNcl = AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            _tNavApplicationObjectBase = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase");
        }
        if (_tNavApplicationObjectBase == null) return null;

        var ftHandler = _tFieldTriggerHandler1!.MakeGenericType(_tNavApplicationObjectBase);
        var ret = target.ReturnType;

        // We can't Delegate.CreateDelegate directly for an Action<NavApplicationObjectBase>
        // binding to a method whose first param (the implicit `this`) is Record100003 —
        // the runtime rejects the variance. Wrap via a typed helper closure that casts.
        if (ret == typeof(System.Threading.Tasks.ValueTask))
        {
            var funcT = typeof(Func<,>).MakeGenericType(_tNavApplicationObjectBase, typeof(System.Threading.Tasks.ValueTask));
            var helper = typeof(RecordPatches).GetMethod(nameof(MakeAsyncTriggerInvoker),
                BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(recordType);
            var del = (Delegate)helper.Invoke(null, new object?[] { target })!;
            // del is Func<TConcrete, ValueTask>; we need Func<NavApplicationObjectBase, ValueTask>.
            // Build via a small wrapper closure.
            var wrapper = BuildAsyncWrapper(del, _tNavApplicationObjectBase, recordType);
            var ctor = ftHandler.GetConstructor(new[] { typeof(Type), funcT });
            return ctor?.Invoke(new object[] { recordType, wrapper });
        }
        else if (ret == typeof(void))
        {
            var actT = typeof(Action<>).MakeGenericType(_tNavApplicationObjectBase);
            var helper = typeof(RecordPatches).GetMethod(nameof(MakeSyncTriggerInvoker),
                BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(recordType);
            var del = (Delegate)helper.Invoke(null, new object?[] { target })!;
            var wrapper = BuildSyncWrapper(del, _tNavApplicationObjectBase, recordType);
            var ctor = ftHandler.GetConstructor(new[] { typeof(Type), actT });
            return ctor?.Invoke(new object[] { recordType, wrapper });
        }
        Console.Error.WriteLine($"[RecordPatches] WireFieldTriggerHandlers: skip {target.DeclaringType?.Name}.{target.Name} — unsupported return type {ret.Name}");
        return null;
    }

    // Helpers that produce strongly typed delegates over the concrete recordType.
    private static Func<TRec, System.Threading.Tasks.ValueTask> MakeAsyncTriggerInvoker<TRec>(MethodInfo target)
        => (Func<TRec, System.Threading.Tasks.ValueTask>)
           Delegate.CreateDelegate(typeof(Func<TRec, System.Threading.Tasks.ValueTask>), target);

    private static Action<TRec> MakeSyncTriggerInvoker<TRec>(MethodInfo target)
        => (Action<TRec>)Delegate.CreateDelegate(typeof(Action<TRec>), target);

    // Wrap a Func<TConcrete, ValueTask> as Func<NavApplicationObjectBase, ValueTask> that casts.
    private static Delegate BuildAsyncWrapper(Delegate inner, Type baseType, Type concreteType)
    {
        // Build via expression tree: (NavApplicationObjectBase x) => innerDel((TConcrete)x)
        var prm = System.Linq.Expressions.Expression.Parameter(baseType, "x");
        var cast = System.Linq.Expressions.Expression.Convert(prm, concreteType);
        var call = System.Linq.Expressions.Expression.Invoke(
            System.Linq.Expressions.Expression.Constant(inner), cast);
        var funcT = typeof(Func<,>).MakeGenericType(baseType, typeof(System.Threading.Tasks.ValueTask));
        return System.Linq.Expressions.Expression.Lambda(funcT, call, prm).Compile();
    }

    private static Delegate BuildSyncWrapper(Delegate inner, Type baseType, Type concreteType)
    {
        var prm = System.Linq.Expressions.Expression.Parameter(baseType, "x");
        var cast = System.Linq.Expressions.Expression.Convert(prm, concreteType);
        var call = System.Linq.Expressions.Expression.Invoke(
            System.Linq.Expressions.Expression.Constant(inner), cast);
        var actT = typeof(Action<>).MakeGenericType(baseType);
        return System.Linq.Expressions.Expression.Lambda(actT, call, prm).Compile();
    }

    // ── Enum-typed-field metadata fix-up ─────────────────────────────────────
    //
    // After NCLMetaField.CreateFromMetaField has run for every field on a new
    // NCLMetaTable, replace `fieldOptionMetadata` on enum-typed fields with an
    // AlEnumOptionMetadata built from the AlEnumMetadataRegistry. This makes
    // FieldRef.GetEnumValue{Caption,Name}FromOrdinalValue and other consumers
    // (NavOption.Create, GetOptionFromIndex, IsValidOrdinal, ...) behave with
    // BC NCLEnumMetadata semantics — ordinal-keyed, not array-index-keyed.
    private static System.Reflection.FieldInfo? _fNCLMetaFieldFieldOptionMetadata;
    private static System.Text.RegularExpressions.Regex _rxEnumTypeName = new(
        "^\\s*Enum\\s+(?:\"([^\"]+)\"|([A-Za-z_][\\w]*))\\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Re-apply enum field-option metadata for every cached NCLMetaTable. Called
    // from BcRuntime.SetTestAssembly after Emit, by which time AlEnumMetadataRegistry
    // is populated for the bucket's enums. The first BuildNCLMetaTable pass runs
    // during AddSourceDir, *before* AL emit, so the registry is empty then and the
    // initial fixup misses anything.
    public static void FixupEnumFieldOptionMetadataAll()
    {
        foreach (var kvp in _metaTableCache)
        {
            if (!(kvp.Value is NCLMetaTable mt)) continue;
            if (!_parsedTables.TryGetValue(kvp.Key, out var parsed)) continue;
            var extFields = _parsedExtensionFields.TryGetValue(parsed.TableName.ToLowerInvariant(), out var ef)
                ? (IEnumerable<ParsedField>)ef : Enumerable.Empty<ParsedField>();
            FixupEnumFieldOptionMetadata(mt, parsed, extFields);
        }
    }

    private static void FixupEnumFieldOptionMetadata(NCLMetaTable table, ParsedTable parsed, IEnumerable<ParsedField> extFields)
    {
        try
        {
            // Map fieldId -> EnumTypeName (parsed from the AL `Enum "<n>"` TypeName).
            var enumNameByFieldId = new Dictionary<int, string>();
            foreach (var f in parsed.Fields.Concat(extFields))
            {
                var m = _rxEnumTypeName.Match(f.TypeName ?? string.Empty);
                if (!m.Success) continue;
                var enumName = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrEmpty(enumName))
                    enumNameByFieldId[f.FieldId] = enumName;
            }
            if (enumNameByFieldId.Count == 0) return;

            // Resolve NCLMetaField.fieldOptionMetadata once.
            if (_fNCLMetaFieldFieldOptionMetadata == null)
            {
                var tNCLMetaField = typeof(NCLMetaField);
                _fNCLMetaFieldFieldOptionMetadata = tNCLMetaField.GetField(
                    "fieldOptionMetadata",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            if (_fNCLMetaFieldFieldOptionMetadata == null) return;

            // Snapshot the registry once (by name) so we don't repeat the scan per field.
            var byName = new Dictionary<string, AlEnumMetadataRegistry.Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in AlEnumMetadataRegistry.Snapshot())
                byName[e.Name] = e;

            foreach (var pair in enumNameByFieldId)
            {
                if (!table.TryGetFieldByNo(pair.Key, out var nclField) || nclField == null) continue;
                if (!byName.TryGetValue(pair.Value, out var entry)) continue;
                try
                {
                    var meta = new AlEnumOptionMetadata(entry.Name, entry.Id, entry.Options, entry.Indexes, entry.Implementations);
                    AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaFieldFieldOptionMetadata, nclField, meta);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[RecordPatches] FixupEnumFieldOptionMetadata: field {pair.Key} ({pair.Value}) failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RecordPatches] FixupEnumFieldOptionMetadata({parsed.TableId}) failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
