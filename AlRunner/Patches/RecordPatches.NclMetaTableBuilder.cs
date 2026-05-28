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

namespace AlRunnerV2.Patches;

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
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = Array.Find(asm.GetTypes(),
                    x => x.Name == name && typeof(NavRecord).IsAssignableFrom(x));
                if (t != null)
                {
                    _recordTypeCache[id] = t;
                    return t;
                }
            }
            catch { }
        }
        return null;
    }

    internal static NCLMetaTable? GetOrBuildNCLMetaTable(int tableId)
        => (NCLMetaTable?)_metaTableCache.GetOrAdd(tableId, BuildNCLMetaTable);

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
            var extFields = _parsedExtensionFields.TryGetValue(parsed.TableName.ToLowerInvariant(), out var ef)
                ? ef : Enumerable.Empty<ParsedField>();
            var allParsed = new[] { timestampParsed }.Concat(parsed.Fields)
                .Concat(extFields)
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
            var defaultMetaTable = CallMetaTableCtor(tableId, parsed.TableName, fields, allKeys.ToArray(), parsed.IsTableTypeTemporary);
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
                EnsureCachePopulatorReflection();
                if (_fNCLMetaAppObjMetadataLoaded != null)
                    AlRunnerV2.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, built, true);

                // W-8b A-prime: poke a real NavTableTriggerEventHandler into the
                // tableTriggerEventHandler field. NCLMetaTable.TableTriggerEventHandler /
                // TriggerEventHandler are simple field-getter properties — even when their
                // call sites are R2R-inlined into NavRecord.InsertAsync, the inlined code
                // reads our field. EventSubscriberPatches.InjectAll later attaches per-event
                // NavEventSubscription objects to its NavEventScope.registeredSubscriptions.
                var triggerHandler = AlRunnerV2.Patches.EventSubscriberPatches
                    .CreateTableTriggerEventHandler();
                if (triggerHandler != null)
                {
                    var f = built.GetType().GetField("tableTriggerEventHandler",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null)
                        AlRunnerV2.Infrastructure.FieldPoke.SetInstance(f, built, triggerHandler);
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

                // Register any AutoIncrement fields so NavRecord_ALInsertAsync3 assigns counters.
                foreach (var f in parsed.Fields)
                    if (f.IsAutoIncrement)
                        AlRunnerV2.BcRuntime.RegisterAutoIncrementField(tableId, f.FieldId);
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

    private static object? CallMetaTableCtor(int id, string name, object[] fields, object[] allKeys, bool isTableTypeTemporary)
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

        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name == "id") { args[i] = f.FieldId; continue; }
            if (p.Name == "name") { args[i] = f.FieldName; continue; }
            if (p.Name == "type") { args[i] = MapNavType(f.TypeName); continue; }
            if (p.Name == "length") { args[i] = f.Length; continue; }
            if (p.Name == "autoIncrement" && f.IsAutoIncrement) { args[i] = true; continue; }
            if (p.Name == "enabled") { args[i] = (bool?)true; continue; }
            if (p.Name == "fieldClass" && f.IsFlowField && _tFieldClass != null)
            {
                args[i] = Enum.Parse(_tFieldClass, "FlowField");
                continue;
            }
            if (p.Name == "calcFormula" && calcFormulaObj != null)
            {
                args[i] = calcFormulaObj;
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
                args[i] = iv;
                continue;
            }
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

        // Build MetaFilter[] for each FIELD-type filter
        var filterObjects = new List<object>();
        foreach (var filter in cf.Filters)
        {
            var srcFilterField = srcTable.Fields.FirstOrDefault(f =>
                string.Equals(f.FieldName, filter.SourceFieldName, StringComparison.OrdinalIgnoreCase));
            var parentFilterField = parentTable.Fields.FirstOrDefault(f =>
                string.Equals(f.FieldName, filter.ParentFieldName, StringComparison.OrdinalIgnoreCase));
            if (srcFilterField == null)
            {
                Console.Error.WriteLine($"[RecordPatches] BuildMetaCalcFormula: filter source field '{filter.SourceFieldName}' not found in '{cf.SourceTableName}'");
                continue;
            }
            if (parentFilterField == null)
            {
                Console.Error.WriteLine($"[RecordPatches] BuildMetaCalcFormula: filter parent field '{filter.ParentFieldName}' not found in '{parentTable.TableName}'");
                continue;
            }
            filterObjects.Add(BuildMetaFilter(srcFilterField.FieldId, parentFilterField.FieldId));
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
            if (p.Name == "reverseSign") { args[i] = false; continue; }
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

    private static object BuildMetaFilter(int sourceFieldId, int parentFieldId)
    {
        // MetaFilter(int fieldId, FilterType filterType, string filterValue, ...)
        var ctor = _tMetaFilter!.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.Name == "fieldId") { args[i] = sourceFieldId; continue; }
            if (p.Name == "filterType") { args[i] = Enum.Parse(_tFilterType!, "FIELD"); continue; }
            if (p.Name == "filterValue") { args[i] = parentFieldId.ToString(); continue; }
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
        }
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
            if (kvp.Value is NCLMetaTable mt)
                WireFieldTriggerHandlers(mt, kvp.Key);
        }
    }

    // Single AppDomain walk that finds every NavRecord-derived "Record<N>" type and bulk-populates
    // _recordTypeCache. Without this, each WireFieldTriggerHandlers(tableId) call triggers its own
    // cold-miss full-AppDomain walk via FindRecordType — O(N×M) where N=tables, M=total types.
    // Prewarm is O(M); subsequent FindRecordType calls become O(1) dictionary hits.
    private static void PrewarmRecordTypeCache()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
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

    private static void WireFieldTriggerHandlers(NCLMetaTable built, int tableId)
    {
        try
        {
            EnsureFieldTriggerReflection();
            if (_tFieldTriggerHandlerAttr == null || _tFieldTriggerType == null
                || _tFieldTriggerHandler1 == null || _tEventTriggerData == null
                || _fEventTriggerDataValueBacking == null)
                return;

            var recordType = FindRecordType(tableId);
            if (recordType == null) return;

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
            if (byField.Count == 0) return;

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
                        AlRunnerV2.Infrastructure.FieldPoke.SetInstance(_fValidateHandlerBacking, etd, handler);
                }
                if (kvp.Value.lookup != null && _fLookupHandlerBacking != null)
                {
                    var handler = BuildFieldTriggerHandler(kvp.Value.lookup, recordType);
                    if (handler != null)
                        AlRunnerV2.Infrastructure.FieldPoke.SetInstance(_fLookupHandlerBacking, etd, handler);
                }
                AlRunnerV2.Infrastructure.FieldPoke.SetInstance(_fEventTriggerDataValueBacking, metaField, etd);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RecordPatches] WireFieldTriggerHandlers({tableId}) failed: {ex.GetType().Name}: {ex.Message}");
        }
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
                    var meta = new AlEnumOptionMetadata(entry.Name, entry.Id, entry.Options, entry.Indexes);
                    AlRunnerV2.Infrastructure.FieldPoke.SetInstance(_fNCLMetaFieldFieldOptionMetadata, nclField, meta);
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
