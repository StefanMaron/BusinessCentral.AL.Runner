// RecordPatches.NclMetaTableFromBcDocument — build a compiled table's NCLMetaTable by
// letting BC construct it from BC's own emitted metadata document, instead of deriving
// one by hand (issue #3552, first conversion of the chain #3562 tracks).
//
// THE SEAM
//   NCLMetaTable has no public constructor and no conversion from Types.Metadata.MetaTable;
//   it builds itself FROM A LOADER. NCLMetaApplicationObject.Populate() calls
//   NCLMetaTable.LoadMetadata(), which reaches
//   ObjectLoader.MetaObjectCache.GetMetaTable(objectId, appGroup) →
//   INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata(...) →
//   MetaTable.CreateMetaTableFromXml(document, appGroup.GroupId), and then does the
//   AssignFromMetaTable / metadataAppGroupMetaTable / RuntimeInfo work itself.
//   RunnerXmlMetadataLoader already implements that interface for reports, pages and
//   xmlports; answering ObjectType.Table out of AlObjectMetadataRegistry (#3548) is what
//   makes this path reachable for a table the runner compiled.
//   See docs/object-metadata-from-bc.md for the decompiled chain and what each step supplies.
//
// AVAILABILITY DECIDES THE ROUTE, AND A FAILURE IS NEVER RE-ROUTED
//   The route is taken only when a document is registered for (Table, id), and nothing here
//   catches a failure and tries the derivation instead: a weaker answer substituted on error
//   would be wrong metadata under a green build, which is what
//   .claude/rules/loud-failures.md exists to prevent.
//
//   How far a failure then travels differs by call site, and only one of the two is loud:
//   the post-emit sweep (RebuildTablesFromBcMetadataAll ← BcRuntime.SetTestAssembly) has no
//   handler over it, so a throw there aborts bundle load naming the member; the cold build
//   sits inside BuildNCLMetaTable's pre-existing `catch → Console.Error → return null`, which
//   swallows it into "no metatable" exactly as it does for a derivation failure. That swallow
//   predates this change and is #3590 — do not read the paragraph above as a claim that the
//   cold path tears through, because it does not.
using System.Linq;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const string BcTableMetadataKind = "Table";

    /// <summary>
    /// Table ids whose live NCLMetaTable already carries BC's document. Reloading one a second
    /// time is not idempotent from BC's side: <c>AssignFromMetaTable</c> rebuilds the
    /// NCLMetaField array, and a data provider already open over that table then raises
    /// <c>NavObjectDefinitionChangedException</c> ("the definition of the … field has
    /// changed"). That is not hypothetical — <c>SetTestAssembly</c> runs once per emitted
    /// assembly, so a consolidated bundle reloaded table 60710 eight times and lost a whole
    /// suite to it. Cleared with <c>_metaTableCache</c> in ResetForReload, because a
    /// --watch/--server cycle replaces the instances this is asserting about.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte>
        _bcDocumentBackedTables = new();

    internal static void ClearBcDocumentBackedTables() => _bcDocumentBackedTables.Clear();

    private static MethodInfo? _mCreateEmptyNCLMetaTable;
    private static MethodInfo? _mNclMetaTableLoadMetadata;
    private static readonly object _bcDocumentShapeLock = new();

    /// <summary>
    /// True when BC's emitter handed the runner a metadata document for this table id, so
    /// <see cref="BuildNCLMetaTableFromBcDocument"/> can construct it through BC's own loader.
    /// A table with no document — a precompiled dependency's, a virtual system table, or one
    /// served from a cache written before #3548 — answers false and keeps the derivation.
    /// </summary>
    internal static bool HasBcTableMetadataDocument(int tableId)
        => AlObjectMetadataRegistry.TryGet(BcTableMetadataKind, tableId, out var xml)
           && !string.IsNullOrEmpty(xml);

    /// <summary>
    /// The one predicate both routes into BC's document consult — the cold build in
    /// <c>BuildNCLMetaTable</c> and the post-emit reload in
    /// <see cref="RebuildTablesFromBcMetadataAll"/>. It is a single method rather than the same
    /// conditions written twice because the two paths write the same state: a table that took
    /// one route and not the other would carry half of each answer.
    ///
    /// <para><b>#3600 narrowed this from "any tableextension names the table" to the two cases
    /// BC genuinely does not fold.</b> Measured on the <c>ObjectMetadataCapture</c> fixture: a
    /// same-app, add-only tableextension's fields AND keys are already in the base table's own
    /// <c>&lt;MetaTable&gt;</c> — BC's compiler folds them in at compile time — so excluding
    /// those tables sent 5 of the al-language corpus's 6 excluded tables to a hand-derivation
    /// the document already answered. What is genuinely NOT folded: a <c>modify(...)</c> block,
    /// which lands only in the extension's own delta (<c>&lt;FieldChange&gt;</c>); and a
    /// cross-app extension, whose <c>FieldAdd</c> delta the base table's own document — emitted
    /// by ITS app's compile, before or independently of the extending app's — cannot see. See
    /// the issue comment thread for the measurement and docs/object-metadata-from-bc.md.</para>
    ///
    /// <para><b>Gate on <see cref="_extensionSourceInfo"/>, never on the merged field/key
    /// count.</b> Fields and keys travel separate channels into
    /// <see cref="MergeExtensionFields"/>, and a <c>modify(...)</c>-only or key-only extension
    /// contributes no fields at all — measured: a cross-app key-only tableextension left
    /// <c>_parsedExtensionFields</c> empty, a COUNT-based guard passed, the base app's own
    /// document won, and <c>RecordRef.KeyCount()</c> silently answered 2 where the derivation
    /// answers 3. Exit 0, no diagnostic (pinned by
    /// <c>BaseTableWithAKeyOnlyExtensionInAnotherApp_KeepsTheDerivation</c> in
    /// AlRunner.Tests/TableMetadataFromBcDocumentTests.cs). <see cref="_extensionSourceInfo"/>
    /// records one entry per
    /// extension REGARDLESS of whether it contributed any field or key, which is what keeps a
    /// modify-only or key-only extension visible here even though the other three collections
    /// beside it would show nothing for it.</para>
    ///
    /// <para><b>Unknown reads as cross-app, not as same-app.</b> A precompiled <c>.app</c>'s
    /// extension always carries <c>OwningAppId = null</c> (see
    /// <c>RecordPatches.BcAppFallback.EnsureBcSymbolExtensionIndex</c>), and an AL-source
    /// extension or table whose app.json could not be resolved also carries null. Two nulls are
    /// never treated as matching — only two RESOLVED, EQUAL app ids relax the guard — so an
    /// unresolvable boundary keeps the table on the derivation instead of guessing it is safe.</para>
    /// </summary>
    internal static bool ShouldBuildTableFromBcDocument(int tableId, ParsedTable parsed)
    {
        if (!HasBcTableMetadataDocument(tableId)) return false;
        var key = parsed.TableName.ToLowerInvariant();
        if (!_extensionSourceInfo.TryGetValue(key, out var extensions) || extensions.Count == 0)
            return true;

        foreach (var (owningAppId, hasModify) in extensions)
        {
            if (hasModify) return false;
            if (owningAppId is not { } extApp
                || parsed.OwningAppId is not { } tableApp
                || extApp != tableApp)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Construct the table through BC: an empty NCLMetaTable bound to
    /// <see cref="RunnerMetaApplicationObjectLoader"/>, then the load call that parses the
    /// document and assigns every field, key, relation and caption.
    /// Never called unless <see cref="HasBcTableMetadataDocument"/> is true.
    /// </summary>
    private static NCLMetaTable BuildNCLMetaTableFromBcDocument(int tableId, object? baseGroup)
    {
        EnsureBcDocumentShape();

        var built = (NCLMetaTable?)_mCreateEmptyNCLMetaTable!.Invoke(null, new object?[]
        {
            RunnerMetaApplicationObjectLoader.Instance, tableId, baseGroup, -1, string.Empty,
        }) ?? throw new BcShapeGapException(
            "AL table metadata construction",
            "NCLMetaTable.CreateEmptyNCLMetaTable",
            $"BC's factory returned null for table {tableId}");

        // LoadMetadata(), NOT Populate(). Populate() is the natural entry point and BC's own
        // callers use it, but this runner Cecil-rewrites NCLMetaApplicationObject.Populate()
        // to a void no-op (NclCecilRewrite.Runtime.cs) because it NREs on the hand-built
        // skeleton metas the derivation produces. Calling it here therefore builds an EMPTY
        // table in silence — measured: TableName "", Fields null, and the first record access
        // NREs in NCLMetaTable.get_PrimaryKey. LoadMetadata is what Populate would have
        // called; NCLMetaApplicationObject.LoadMetadata (the base call it starts with) has an
        // empty body, so nothing is skipped by entering one level down.
        _mNclMetaTableLoadMetadata!.Invoke(built, Array.Empty<object?>());

        // The two bookkeeping assignments Populate() makes around LoadMetadata. metadataLoaded
        // is what stops NCLMetadata.GetMetaApplicationObjectInternal calling the no-op'd
        // Populate later and concluding the table still needs loading.
        EnsureCachePopulatorReflection();
        if (_fNCLMetaAppObjMetadataLoaded != null)
            AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, built, true);

        _bcDocumentBackedTables[tableId] = 1;
        return built;
    }

    /// <summary>
    /// One line per built table naming which of the two routes produced it. Off unless
    /// <c>AL_RUNNER_TRACE_TABLE_METADATA_SOURCE=1</c>, and the only place the choice is
    /// observable — the two routes produce the same TYPE, so nothing downstream can be asked
    /// which one ran. Same shape and rationale as
    /// <c>AL_RUNNER_TRACE_OBJECT_METADATA</c> (docs/object-metadata-capture.md).
    /// </summary>
    private static void TraceTableMetadataSource(int tableId, string source, NCLMetaTable? built = null)
    {
        var level = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_TABLE_METADATA_SOURCE");
        if (level != "1" && level != "2") return;
        Console.Out.WriteLine($"[table-metadata] {tableId} source={source}");
        if (level != "2" || built == null) return;

        // Per-field Editable / DataClassification / EnumTypeId / EnumTypeName. All four live
        // on Types.Metadata.MetaField, reachable only through the ORIGINAL MetaTable the
        // NCLMetaTable was built from: NCLMetaField carries DataClassification but neither
        // Editable nor the enum type, and no AL surface exposes any of them for a table field.
        // So this trace is where those values are observable at all — see
        // docs/object-metadata-from-bc.md#reading-the-values-back.
        foreach (var line in DescribeMetaFields(built))
            Console.Out.WriteLine($"[table-metadata] {tableId} {line}");
    }

    private static IEnumerable<string> DescribeMetaFields(NCLMetaTable built)
    {
        var original = built.GetType()
            .GetField("metadataAppGroupMetaTable", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(built);
        var metaTable = original?.GetType()
            .GetProperty("Item", BindingFlags.Public | BindingFlags.Instance)?.GetValue(original);
        if (metaTable == null) yield break;

        var fields = metaTable.GetType()
            .GetProperty("Fields", BindingFlags.Public | BindingFlags.Instance)?.GetValue(metaTable);
        if (fields is not System.Collections.IEnumerable seq) yield break;

        foreach (var f in seq)
        {
            if (f == null) continue;
            var t = f.GetType();
            string Read(string name) =>
                t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(f)?.ToString() ?? "<none>";
            yield return $"field={Read("Id")} name={Read("Name")}"
                       + $" editable={Read("Editable")}"
                       + $" dataClassification={Read("DataClassification")}"
                       + $" enumTypeId={Read("EnumTypeId")} enumTypeName={Read("EnumTypeName")}";
        }
    }

    /// <summary>
    /// Rebuild every already-cached table for which BC's document has since arrived.
    ///
    /// Ordering, and the reason this method exists: <c>BuildNCLMetaTable</c> first runs during
    /// AddSourceDir, and <c>Emit</c> — which is what registers the documents — runs after it.
    /// So the first build of a compiled table sees an EMPTY registry and takes the derivation
    /// even though a document is about to exist. Measured on a three-field fixture: one
    /// <c>BuildNCLMetaTable(72282331)</c> call, with <c>AlObjectMetadataRegistry.Count == 0</c>.
    /// Called from <c>BcRuntime.SetTestAssembly</c>, the same post-emit point
    /// <c>FixupEnumFieldOptionMetadataAll</c> and <c>WireFieldTriggerHandlersAll</c> use for
    /// exactly this reason. A table first touched AFTER emit needs nothing from here — its
    /// single build already finds the document.
    /// </summary>
    public static void RebuildTablesFromBcMetadataAll()
    {
        foreach (var kvp in _metaTableCache)
        {
            if (kvp.Value is not NCLMetaTable built) continue;
            // Once, per table, per bundle — see _bcDocumentBackedTables.
            if (_bcDocumentBackedTables.ContainsKey(kvp.Key)) continue;
            if (!_parsedTables.TryGetValue(kvp.Key, out var parsed)) continue;
            if (!ShouldBuildTableFromBcDocument(kvp.Key, parsed)) continue;

            ReloadFromBcDocumentInPlace(built);
            // #3600 — same reasoning as BuildNCLMetaTable's cold-build call: a same-app
            // add-only extension's fields are already inside the document BC just (re)loaded,
            // but the enum-type-name / AutoIncrement side information ApplyRunnerFieldWiring
            // needs for THOSE fields lives only in the parse-time ParsedField, not on the
            // built NCLMetaTable.
            var extFields = _parsedExtensionFields.TryGetValue(parsed.TableName.ToLowerInvariant(), out var ef)
                ? ef : Enumerable.Empty<ParsedField>();
            ApplyRunnerFieldWiring(built, parsed, extFields, parsed.Fields.Concat(extFields));
            _bcDocumentBackedTables[kvp.Key] = 1;
            TraceTableMetadataSource(kvp.Key, "bc-document", built);
        }
    }

    /// <summary>
    /// Re-run BC's own load against the CACHED instance rather than replacing it.
    ///
    /// Evicting and rebuilding was tried first, and produced three runner-extras regressions
    /// against a clean baseline: a [ConfirmHandler] that stopped firing, a source-field
    /// OnLookup trigger that stopped being found, and a cross-app OnAfterValidate mutation
    /// that stopped propagating — each one a consumer still holding the instance the table had
    /// been cached as. Preserving the identity and reassigning only the metadata is what
    /// BC's own AssignFromMetaTable does anyway.
    ///
    /// The loader is supplied first because the derivation built this instance without one —
    /// CreateFromMetaTable passes null — and LoadMetadata dereferences it to reach
    /// MetaObjectCache.
    /// </summary>
    private static void ReloadFromBcDocumentInPlace(NCLMetaTable built)
    {
        EnsureBcDocumentShape();
        AlRunner.Infrastructure.FieldPoke.SetInstance(
            RequireObjectLoaderBackingField(built.GetType()),
            built, RunnerMetaApplicationObjectLoader.Instance);
        _mNclMetaTableLoadMetadata!.Invoke(built, Array.Empty<object?>());
    }

    private static FieldInfo? _fNclMetaAppObjObjectLoader;

    private static FieldInfo RequireObjectLoaderBackingField(Type nclMetaTableType)
        => _fNclMetaAppObjObjectLoader ??= BcShape.RequiredField(
            nclMetaTableType, "<ObjectLoader>k__BackingField",
            "AL table metadata construction",
            "the auto-property backing field NCLMetaApplicationObject.ObjectLoader reads, which "
            + "LoadMetadata dereferences to reach MetaObjectCache");

    /// <summary>
    /// <c>NavAppGroup.BaseGroup</c> — the metadata app group every runner-built table belongs
    /// to, and the one BC's loader path resolves the object's owner against.
    /// </summary>
    private static object? ResolveNavAppBaseGroup()
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        var tAppGroup = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroup")!;
        return tAppGroup.GetProperty("BaseGroup", BindingFlags.Public | BindingFlags.Static)
                   ?.GetValue(null)
               ?? tAppGroup.GetField("BaseGroup", BindingFlags.Public | BindingFlags.Static)
                   ?.GetValue(null);
    }

    /// <summary>
    /// The runner's own wiring onto a freshly built NCLMetaTable, shared by both construction
    /// routes because neither BC nor the derivation supplies it: the table-trigger event
    /// handler, enum-field option metadata, and AutoIncrement registration. Everything BC
    /// *can* state about the table comes from whichever route built it.
    /// </summary>
    private static void ApplyRunnerFieldWiring(
        NCLMetaTable built, ParsedTable parsed,
        IEnumerable<ParsedField> extFields, IEnumerable<ParsedField> autoIncrementCandidates)
    {
        // W-8b A-prime: poke a real NavTableTriggerEventHandler into the
        // tableTriggerEventHandler field. NCLMetaTable.TableTriggerEventHandler /
        // TriggerEventHandler are simple field-getter properties — even when their call
        // sites are R2R-inlined into NavRecord.InsertAsync, the inlined code reads our
        // field. EventSubscriberPatches.InjectAll later attaches per-event
        // NavEventSubscription objects to its NavEventScope.registeredSubscriptions.
        var triggerHandler = EventSubscriberPatches.CreateTableTriggerEventHandler();
        if (triggerHandler != null)
        {
            var f = built.GetType().GetField("tableTriggerEventHandler",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(f, built, triggerHandler);
        }

        // For AL `Enum "X"`-typed fields the upstream BC factory builds either a plain
        // NCLOptionMetadataWithCaptions (when EnumTypeId==0) or an NCLFieldEnumMetadata that
        // chains to NavGlobal.MetadataProvider (NREs on skeleton). Both paths produce wrong
        // results for `FieldRef.GetEnumValueCaption/NameFromOrdinalValue(ordinal)` on sparse
        // AL enums (e.g. value(0), value(5), value(10)) because the base
        // GetCaptionFromIndex/GetOptionFromIndex treats the AL ordinal as a 0..Count-1 array
        // index. We swap in AlEnumOptionMetadata which mirrors NCLEnumMetadata semantics
        // (search indexes[] for matching ordinal) using data captured by BcCompiler at AL
        // emit time. This applies to the BC-document route too: the document states a real
        // EnumTypeId, so BC builds the chaining NCLFieldEnumMetadata rather than the plain
        // one, and the provider it chains to is the skeleton's.
        FixupEnumFieldOptionMetadata(built, parsed, extFields);

        // Register any AutoIncrement fields so NavRecord_ALInsertAsync3 assigns counters.
        // A tableextension may declare the AutoIncrement field, and since #1711 that
        // property survives the parse. Registering only base-table fields would be the
        // silent half-fix — the NCLMetaField would say autoIncrement=true while no counter
        // ever advanced, so every Insert left the field at 0 and the second row collided.
        foreach (var f in autoIncrementCandidates)
            if (f.IsAutoIncrement)
                AlRunner.BcRuntime.RegisterAutoIncrementField(parsed.TableId, f.FieldId);
    }

    private static void EnsureBcDocumentShape()
    {
        if (_mCreateEmptyNCLMetaTable != null && _mNclMetaTableLoadMetadata != null) return;
        lock (_bcDocumentShapeLock)
        {
            _mCreateEmptyNCLMetaTable ??= BcShape.RequiredMethod(
                typeof(NCLMetaTable), "CreateEmptyNCLMetaTable",
                BindingFlags.NonPublic | BindingFlags.Static,
                "AL table metadata construction", "NCLMetaTable.CreateEmptyNCLMetaTable",
                "BC's own factory for a loader-backed NCLMetaTable — see docs/object-metadata-from-bc.md");

            _mNclMetaTableLoadMetadata ??= BcShape.RequiredMethod(
                typeof(NCLMetaTable), "LoadMetadata",
                BindingFlags.NonPublic | BindingFlags.Instance,
                "AL table metadata construction", "NCLMetaTable.LoadMetadata",
                "the call that parses the loader's document into this table — entered directly "
                + "because Populate() is a Cecil no-op here",
                types: Type.EmptyTypes);
        }
    }
}
