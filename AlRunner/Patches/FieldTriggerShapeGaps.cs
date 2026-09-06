// FieldTriggerShapeGaps — the field-trigger INSTALL path's refusals (#3026).
//
// ── THE DEFECT ───────────────────────────────────────────────────────────────────────────
// RecordPatches.EnsureFieldTriggerReflection resolves BC's private field-trigger members
// once and caches them as nullable statics. Every one of them is null on exactly one kind of
// build: one whose NCLMetaField / EventTriggerData layout is not the shape this reflection
// was written against.
//
// The READ path over that state already refuses. RecordPatches.TryHasFieldLookupTrigger is
// three-valued ON PURPOSE — its own comment says a caller that refuses on `false` would be
// telling the developer "your AL declares no OnLookup", which "would be a lie if the real
// reason were that reflection could not find BC's backing field on a build whose shape
// moved" — and RunnerPageInstance.RaiseSourceFieldOnLookup turns that null into a
// BcShapeGapException (#2999).
//
// The WRITE path over the SAME state defaulted, and did it silently:
//
//     if (kvp.Value.validate != null && _fValidateHandlerBacking != null) { …install… }
//
// so on a build where the member moved, the AL table's OnValidate / OnLookup field trigger
// was NEVER INSTALLED, nothing was printed, and WireFieldTriggerHandlers still returned true
// — its contract for "this table is wired, do not retry". AL that depends on the trigger then
// ran with no trigger at all. That is the silent default .claude/rules/loud-failures.md
// exists to prevent, and it is worse than a refusal: the test does not fail, it PASSES having
// skipped the trigger. Two code paths over one piece of state, one refusing and one
// defaulting, is the invariant mismatch — not the null check itself.
//
// ── WHY BcShapeGapException AND NOT A SCOPE CLAIM ────────────────────────────────────────
// It meets #2995's test exactly: the read could not be PERFORMED. It is a property of which
// BC build is on disk, so it can be true on one matrix leg and false on another in the same
// run — the case that must never be absorbable by an `expect-oos` manifest entry, and the
// reason this is a type rather than a third reason anchor on RunnerOutOfScopeException. See
// AlRunner/Infrastructure/BcShapeGapException.cs.
//
// ── NOT REACHABLE ON ANY SUPPORTED BUILD, WHICH IS THE ARGUMENT FOR FIXING IT NOW ────────
// Measured on the pristine service-tier Microsoft.Dynamics.Nav.Ncl.dll for BC 27.5 and 28.1
// (ilspycmd, NCLMetaField): EventTriggerData declares
//
//     internal FieldTriggerHandler<NavApplicationObjectBase> LookupHandler   { get; set; }
//     internal FieldTriggerHandler<NavApplicationObjectBase> ValidateHandler { get; set; }
//     internal List<FieldTriggerHandler<NavApplicationObjectBase>> OnBeforeValidateHandlers { get; set; }
//     internal List<FieldTriggerHandler<NavApplicationObjectBase>> OnAfterValidateHandlers  { get; set; }
//
// — all four auto-properties, so all four compiler-generated backing fields exist, on every
// BC version the runner supports. No refusal here is reachable today; that is what makes the
// change free now and impossible to make cheaply after a BC update reaches it.
//
// ── PROPORTIONALITY: REFUSE WHERE THERE IS SOMETHING TO INSTALL ──────────────────────────
// Every refusal below fires only once the runner KNOWS it has a handler to install for a
// specific table and field. A table that declares no field trigger at all still wires
// (trivially) and returns true on a moved-layout build, because nothing was skipped for it.
// That is why several of these checks moved OUT of the method-top guards and DOWN to the
// install sites — the top guards refused to answer for every table, which is both louder
// than the fact warrants and, being an early `return`, silent anyway.
//
// The two members that could NOT be made proportional are FieldTriggerHandlerAttribute and
// FieldTriggerType: they are what the SCAN reads, so without them the runner cannot even
// determine whether a table declares a field trigger. There is no "nothing to install" answer
// to give, and on such a build every AL field trigger in the bundle stops firing — the
// maximal instance of this very defect. Those refuse for any table.
//
// ── WHAT STAYS A QUIET SKIP, AND WHY ─────────────────────────────────────────────────────
//   * FindRecordType returning null — the table's own assembly is not loaded into the
//     AppDomain YET. Pre-registration walks every suite's src/ up front, so this is the
//     common, correct, retry-later case; WireFieldTriggerHandlers returns false and the
//     caller deliberately does not record the table as wired. Nothing about BC's shape.
//   * Microsoft.Dynamics.Nav.Ncl not being loaded at all — EnsureFieldTriggerReflection's
//     own First() throws, WireFieldTriggerHandlers' catch keeps swallowing it. Runner state,
//     not BC layout.
//   * BuildFieldTriggerHandler declining a trigger method whose return type is neither void
//     nor ValueTask — that is a property of the AL the runner EMITTED, not of BC's layout,
//     and it already prints. It stays a null return.
//
// ── THE CATCH THAT WOULD HAVE EATEN THIS ─────────────────────────────────────────────────
// WireFieldTriggerHandlers ends in a catch-all that prints and returns false. Left alone it
// would have converted every refusal below into a stderr line plus the same not-installed
// outcome — still green, still no trigger. Its filter now lets a shape gap through, which is
// the only reason any of this reaches AL.
//
// See also:
//   .claude/rules/loud-failures.md            — no silent out-of-scope failures
//   docs/limitations.md#bc-shape-gaps         — the reader-facing write-up
//   AlRunner/Patches/TestPageShapeGaps.cs     — the read-path sibling, and where #3026 was filed

using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

/// <summary>
/// One place the field-trigger install path's <see cref="BcShapeGapException"/>s are built, so
/// a refusal cannot drift back into a silent skip one call site at a time. See this file's
/// header for the defect and for the per-member classification.
/// </summary>
internal static class FieldTriggerShapeGap
{
    private const string Doc = BcShapeGapException.DefaultDoc;

    private static string Surface(int tableId, int fieldNo)
        => $"AL field trigger installation (table {tableId}, field {fieldNo})";

    private static string Surface(int tableId)
        => $"AL field trigger installation (table {tableId})";

    /// <summary>
    /// The <c>ValidateHandler</c> / <c>LookupHandler</c> slot on BC's
    /// <c>NCLMetaField.EventTriggerData</c>, or a refusal naming it. Called only once a
    /// handler for <paramref name="fieldNo"/> is in hand, so this can never fire for a field
    /// that declares no trigger.
    /// </summary>
    internal static FieldInfo RequireHandlerBacking(
        FieldInfo? backing, string handlerProperty, int tableId, int fieldNo)
        => backing ?? throw new BcShapeGapException(
            Surface(tableId, fieldNo),
            $"NCLMetaField.EventTriggerData.{handlerProperty}",
            $"backing field <{handlerProperty}>k__BackingField not found on this BC build, so the "
            + $"field's AL trigger cannot be installed — the trigger would never fire and the AL "
            + "relying on it would pass having silently run without it",
            Doc);

    /// <summary>
    /// The <c>OnBeforeValidateHandlers</c> / <c>OnAfterValidateHandlers</c> list property a
    /// tableextension's <c>modify(field)</c> triggers are installed through. Called only once
    /// at least one such handler has been built.
    /// </summary>
    internal static PropertyInfo RequireHandlerListProperty(
        PropertyInfo? property, string propertyName, int tableId, int fieldNo)
        => property ?? throw new BcShapeGapException(
            Surface(tableId, fieldNo),
            $"NCLMetaField.EventTriggerData.{propertyName}",
            $"property not found on this BC build, so the tableextension's AL {propertyName} field "
            + "triggers cannot be installed — they would never fire",
            Doc);

    /// <summary>
    /// The closed <c>List&lt;FieldTriggerHandler&lt;NavApplicationObjectBase&gt;&gt;</c> the
    /// before/after handler lists are boxed into.
    /// </summary>
    internal static System.Type RequireHandlerListType(System.Type? listType, int tableId, int fieldNo)
        => listType ?? throw new BcShapeGapException(
            Surface(tableId, fieldNo),
            "List<FieldTriggerHandler<NavApplicationObjectBase>>",
            "the handler-list type could not be closed on this BC build (FieldTriggerHandler`1 or "
            + "NavApplicationObjectBase is absent), so the tableextension's before/after field "
            + "triggers cannot be installed",
            Doc);

    /// <summary>BC's <c>NCLMetaField.EventTriggerData</c> nested type.</summary>
    internal static System.Type RequireEventTriggerDataType(System.Type? type, int tableId)
        => type ?? throw new BcShapeGapException(
            Surface(tableId),
            "NCLMetaField.EventTriggerData",
            "nested type not found on this BC build, so no AL field trigger can be installed on "
            + "this table's metafields",
            Doc);

    /// <summary>BC's <c>NCLMetaField.&lt;EventTriggerDataValue&gt;k__BackingField</c>.</summary>
    internal static FieldInfo RequireEventTriggerDataValueBacking(FieldInfo? backing, int tableId)
        => backing ?? throw new BcShapeGapException(
            Surface(tableId),
            "NCLMetaField.EventTriggerDataValue",
            "backing field <EventTriggerDataValue>k__BackingField not found on this BC build, so "
            + "built handlers cannot be attached to this table's metafields",
            Doc);

    /// <summary>
    /// A type the field-trigger SCAN needs. Deliberately not proportional — without these the
    /// runner cannot tell whether a table declares a field trigger at all, and on such a build
    /// every AL field trigger in the bundle would stop firing silently.
    /// </summary>
    internal static System.Type RequireScanType(System.Type? type, string typeName, int tableId)
        => type ?? throw new BcShapeGapException(
            Surface(tableId),
            $"Microsoft.Dynamics.Nav.Runtime.{typeName}",
            "type not found on this BC build, so the runner cannot determine which AL methods are "
            + "field triggers — every field trigger in the bundle would silently never fire",
            Doc);

    /// <summary>
    /// A type or constructor <c>BuildFieldTriggerHandler</c> needs to wrap one already-found AL
    /// trigger method. Named by the method being wrapped, which is what a reader needs.
    /// </summary>
    internal static BcShapeGapException HandlerConstruction(string member, MethodInfo target, string detail)
        => new(
            $"AL field trigger {target.DeclaringType?.Name}.{target.Name}",
            member,
            detail,
            Doc);
}
