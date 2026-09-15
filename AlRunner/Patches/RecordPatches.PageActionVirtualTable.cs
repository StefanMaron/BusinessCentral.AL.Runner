// RecordPatches.PageActionVirtualTable — the "Page Action" system virtual table (2000000143) is
// served by BC's OWN PageActionDataProvider, through the same GetBcVirtualDataAccess factory
// Integer, Date, Table Relations Metadata and Key use.
//
// Third instance of the shape #4088 established and #4147's Key table repeated. BC's provider
// is defective against this runner for one reason only: key field 1 enumerates objects through
// MetadataDataProvider.GetObjectNumberAndInfoWithinRange(ObjectType.Page, …), which reads
// NCLMetadata.GetSnapshotOfAllObjects() — a method NclCecilRewrite.Records.cs replaces with an
// empty list. So handing out BC's provider is necessary and not sufficient.
//
// Key field 2 is forwarded to BC's own public GetActions unchanged. That method reads
// MetadataProvider.GetFrozenPageDefinitionWithExtensionWithoutMergedMultiLanguage(pageNo) and
// walks the page's ActionContainers — page metadata, NOT the object snapshot — so it needs
// nothing from the emptied method and every column of every action row stays BC's own:
// the nesting, the parent ids, the negative auto-ids BC assigns a container that declares
// none, the ToolTip split across four 250-character columns, and the option encodings.
//
// The one difference from Key (2000000063): field 1 there supplies TABLE ids, from
// EnumerateKnownTableMetadata. Here it must supply PAGE ids, so the substitution source is
// EnumerateKnownPageMetadata — the same inventory Page Metadata (2000000138) and Page Control
// Field (2000000139) answer from, so the three tables cannot disagree about which pages exist.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int PageActionVirtualTableId = 2000000143;

    private static bool IsPageActionVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == PageActionVirtualTableId;

    /// <summary>The DataAccess BC's own factory builds for 2000000143 — one over PageActionDataProvider.</summary>
    internal static object GetPageActionVirtualDataAccess(object dataAccessSource, NCLMetaTable table)
        => GetBcVirtualDataAccess(dataAccessSource, table,
            "every Record \"Page Action\" read would answer from an empty store");

    private static MethodInfo? _paGetActions, _paToBuffer, _paGetBounds, _paPassesFieldFilters;
    private static FieldInfo? _paSessionField;
    private static Type? _paBufferType;

    /// <summary>
    /// Body of PageActionDataProvider.GetValuesWithinRangeForKeyField (Cecil-replaced in
    /// NclCecilRewrite.Runtime.cs).
    ///
    /// <para>Field 2 forwards to BC's own GetActions and projects each row through BC's own
    /// ToReadOnlyRecordBuffer, preserving BC's ordering by ActionIndex (slot 2) and BC's own
    /// PassesFieldFilters, so a filtered read answers what the service tier answers.</para>
    ///
    /// <para>Field 1 is BC's own shape with the page ids taken from the runner's inventory
    /// instead of the emptied object snapshot. BC's own field-1 delegate allocates three slots
    /// and sets only the page number, leaving the rest null; this does the same.</para>
    /// </summary>
    public static object PageActionDataProvider_GetValuesWithinRangeForKeyField(
        object self, object keyField, object precedingKeyFieldValues, object range, object sortOrder, object nonPrimaryKeyFilters)
    {
        EnsurePageActionReflection(self);
        var fieldNo = ((NCLMetaField)keyField).FieldNo;
        return fieldNo switch
        {
            1 => PageActionPageIds(range, sortOrder),
            2 => PageActionRows(self, precedingKeyFieldValues, range, sortOrder, nonPrimaryKeyFilters),
            _ => throw new NotSupportedException(),
        };
    }

    private static object PageActionPageIds(object range, object sortOrder)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(_paBufferType!))!;

        var boundsArgs = new object?[] { 0, int.MaxValue, 0, 0 };
        if (!(bool)_paGetBounds!.Invoke(range, boundsArgs)!) return list;
        int low = (int)boundsArgs[2]!, high = (int)boundsArgs[3]!;

        var candidates = EnumerateKnownPageMetadata()
            .Select(p => p.Id).Where(id => id >= low && id <= high).Distinct();
        var ordered = sortOrder.ToString() == "Descending"
            ? candidates.OrderByDescending(id => id)
            : candidates.OrderBy(id => id);

        foreach (var id in ordered)
        {
            // Three slots, page number in slot 1 — BC's own field-1 delegate's shape.
            var slots = new NavValue?[3];
            slots[1] = NavInteger.Create(id);
            var row = Activator.CreateInstance(_paBufferType!, new object?[] { slots });
            if (row != null) list.Add(row);
        }
        return list;
    }

    private static object PageActionRows(
        object provider, object precedingKeyFieldValues, object range, object sortOrder, object nonPrimaryKeyFilters)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(_paBufferType!))!;

        // BC's own body reads precedingKeyFieldValues[1] for the page number — the slot field 1
        // above sets. Forwarded positionally rather than reconstructed.
        var pageNo = ReadBufferSlot(precedingKeyFieldValues, 1);
        if (pageNo == null) return list;

        // includeCustomizations: false — BC's own default for this path. A customization-aware
        // read goes through ApplyPageCustomizations, which this runner does not drive here; the
        // corpus arms measure a page with no customizations, so nothing here claims otherwise.
        var actions = (System.Collections.IEnumerable)_paGetActions!.Invoke(provider, new object?[] { pageNo, range, false })!;

        var rows = new List<object>();
        foreach (var a in actions)
        {
            var buf = _paToBuffer!.Invoke(provider, new[] { a });
            if (buf != null) rows.Add(buf);
        }

        // BC orders by slot 2 (ActionIndex) and then applies the non-key filters. Both are BC's
        // own steps, kept in BC's own order so a filtered, descending read matches the tier.
        // Slot 2 is ActionIndex, a NavInteger. NavValue does NOT implement IComparable, so
        // ordering the NavValue itself throws "Failed to compare two elements in the array";
        // order on the underlying int, which is what BC's own comparison reduces to.
        var indexer = _paBufferType!.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        int Slot2(object r)
        {
            var v = indexer?.GetValue(r, new object[] { 2 });
            return v is NavInteger n ? n.Value : 0;
        }
        var ordered = sortOrder.ToString() == "Descending"
            ? rows.OrderByDescending(Slot2)
            : rows.OrderBy(Slot2);

        foreach (var r in ordered)
        {
            // All SIX arguments, explicitly: MethodInfo.Invoke does NOT apply C# default
            // arguments, and this method declares three of them
            // (includeFlowFields = true, checkAgainstOriginalAndModified = false,
            // bothShouldPass = false). BC's own call site at
            // PageActionDataProvider.GetValuesWithinRangeForKeyField passes the session as the
            // ISortingRulesProvider and takes the three defaults, so these are BC's values.
            if ((bool)_paPassesFieldFilters!.Invoke(null,
                    new object?[] { r, nonPrimaryKeyFilters, GetProviderSession(provider), true, false, false })!)
                list.Add(r);
        }
        return list;
    }

    private static Assembly Ncl(object provider) => provider.GetType().Assembly;

    /// <summary>
    /// BC's own <c>session</c> field, bound once and REQUIRED. It is the ISortingRulesProvider
    /// PassesFieldFilters reads, so a null here does not fail — it silently changes what a
    /// filtered read answers, which is the same shape as binding PassesFieldFilters itself
    /// against the wrong type (#4194). Walking up the hierarchy because the field is declared
    /// on a base of PageActionDataProvider, not on the provider.
    /// </summary>
    private static object? GetProviderSession(object provider)
        => _paSessionField!.GetValue(provider);

    private static FieldInfo? FindSessionField(Type? t)
    {
        for (; t != null; t = t.BaseType)
        {
            var f = t.GetField("session", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }
        return null;
    }

    private static void EnsurePageActionReflection(object provider)
    {
        if (_paGetActions != null) return;
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var t = provider.GetType();
        const string Surface = "page-action-virtual-table";
        const string Detail = "BC shape changed; see #4147";
        // BcShape.FindMethod, not Type.GetMethod: a name-only lookup hands back the
        // most-derived declaration when BC `new`-hides a member and drives the wrong one
        // silently; FindMethod refuses on ambiguity and answers null on absence (#3069).
        var getActions = BcShape.FindMethod(t, "GetActions", inst, Surface, "GetActions", Detail);
        var toBuffer = BcShape.FindMethod(t, "ToReadOnlyRecordBuffer", inst, Surface, "ToReadOnlyRecordBuffer", Detail);
        var rangeType = getActions?.GetParameters().ElementAtOrDefault(1)?.ParameterType;
        var getBounds = rangeType == null ? null
            : BcShape.FindMethod(rangeType, "GetInclusiveIntegerBounds", inst, Surface, "GetInclusiveIntegerBounds", Detail);
        var bufferType = toBuffer?.ReturnType;
        // PassesFieldFilters is a STATIC EXTENSION METHOD on DataHelper, not an instance member
        // of the buffer: BC's decompiled body reads `item2.PassesFieldFilters(...)`, which is
        // extension-method syntax for DataHelper.PassesFieldFilters(item2, ...). Binding it
        // against the buffer type answers null, and an earlier version of this file treated
        // that null as "optional" and passed EVERY row through unfiltered -- so Record.SetRange
        // narrowed nothing and FindFirst answered the first row of the page for every query,
        // silently. Measured: 29 rows in, 29 rows out, four corpus arms reading Actual:<0>.
        var dataHelper = Ncl(provider).GetType("Microsoft.Dynamics.Nav.Runtime.DataHelper");
        var passes = dataHelper == null ? null : BcShape.FindMethod(
            dataHelper, "PassesFieldFilters",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            Surface, "PassesFieldFilters", Detail);

        // Required, not best-effort: see FindSessionField's own note. A `?.` chain here would
        // hand PassesFieldFilters a null sorting-rules provider and change what a filtered read
        // answers, with nothing thrown — the defect class #4194 records.
        var sessionField = FindSessionField(t);

        if (getActions == null || toBuffer == null || getBounds == null || bufferType == null
            || passes == null || sessionField == null)
            // BcShapeGapException, not InvalidOperationException: NavMethodScope_AssertError
            // rethrows only this type, so an `asserterror` around a driver hitting this refusal
            // would otherwise SWALLOW it and PASS — inverting the result rather than hiding it
            // (#2946).
            throw new BcShapeGapException(
                Surface, "PageActionDataProvider",
                "BC does not expose the shape the Page Action virtual-table rewrite drives "
                + "(GetActions, ToReadOnlyRecordBuffer, Range.GetInclusiveIntegerBounds, "
                + "DataHelper.PassesFieldFilters, the provider's own session field) — see #4147");

        _paBufferType = bufferType;
        _paGetActions = getActions;
        _paToBuffer = toBuffer;
        _paGetBounds = getBounds;
        _paPassesFieldFilters = passes;
        _paSessionField = sessionField;
    }
}
