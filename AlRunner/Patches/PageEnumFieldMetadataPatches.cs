// PageEnumFieldMetadataPatches — NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions
// (both overloads), the step real BC runs while materialising a page's control tree that
// stamps each Enum-typed field's OptionString/OptionCaption/OptionValues from the enum's own
// metadata.
//
// Issue #1896: a page with a global control bound to an Enum-typed variable
// (`field(Combi; CombiVar)` where `CombiVar: Enum "Repro Combi"`) throws
//
//   NavALException: You tried to invoke the Enum object with the ID 60040 from the object
//   Repro Enum Modal Test. An object with that ID does not exist in the current application
//   compiled with emit version 28014.
//
// on Page.RunModal() — even though the SAME page variable's procedures already work fine
// without materialising the form (the enum is compiled and reachable; only form
// materialisation fails), and even though the exception NAMES THE CALLING TEST CODEUNIT
// rather than the page. That misattribution is a genuine clue, not noise (see below) — it is
// how NavALException reports metadata failures with no page-scoped NavMethodScope on the
// stack yet.
//
// Root cause (verified via ilspycmd decompile of Ncl.dll + a full C# stack trace captured
// with AL_RUNNER_DIAG_FIRSTCHANCE=MetadataNotFound):
//
//   NavFormHandle.RunModalAsync(record, fieldNo)
//    -> NavForm.RunModalAsync(record, fieldNo)
//     -> NavTestExecution.FindPageType(form) -> NavForm.get_MasterPage()
//      -> NavForm.EnsureMetadataLoaded() -> ApplicationObjectRootScope.AddApplicationObjectRootScope(this, ...)
//       -> NavForm.GetMasterPage() -> MetadataProvider.GetMasterPage(...) -> ...GetMergedMasterPage
//        -> MetadataProvider.GetPageDefinition -> NCLMetaForm.GetFrozenPageDefinitionWithExtensionWithoutMergedMultiLanguage()
//         -> NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions(MetaPageDefinition)
//
// This ENTIRE chain runs synchronously, still inside the CALLING codeunit's own
// NavMethodScope — NavForm.RunModalAsync does not push a page-scoped NavMethodScope until
// AFTER metadata is loaded. So when ApplyAppGroupAwareEnumMetadataToPageExpressions's real
// (unmodified) body does:
//
//   if (!NavGlobal.NCLMetadata.TryGetMetaApplicationObject(ObjectType.Enum, enumId, out var meta, requireCompiled: false))
//       throw new NavMetadataNotFoundException(ObjectType.Enum, enumId);
//
// the NavMetadataNotFoundException that results is caught by NavMethodScope.Run() —
// specifically the CALLING codeunit's scope, because no page-scoped NavMethodScope exists
// yet — and remapped to NavALException with the object name read from the CURRENT scope
// (RemapToALExceptionAndThrow: `text2 = ApplicationObject.ObjectName` when
// `ParentScope?.ApplicationObject` is null). That IS the test codeunit's name. The
// misattribution is therefore a correct symptom of "this failed before the page's own scope
// existed", exactly matching what materialisation-time-only failure implies.
//
// The actual gap: NCLMetadata.TryGetMetaApplicationObject(ObjectType.Enum, ...) can NEVER
// succeed on the runner. Every other object type the runner constructs (Table, Page, Report,
// Query, XmlPort) is registered into NCLMetadata's real cache dictionary by
// RecordPatches.NclMetadataCachePopulator (see NCLMetadata_GetMetaApplicationObjectByType) —
// but Enum objects never were; AL enums are served entirely through the
// NCLEnumMetadata.Create(int) hook (EnumMetadataPatches.cs), a DIFFERENT codepath that
// NavForm's page-materialisation step never calls. Same class of bug as #1926's
// CallMetaTableCtor finding: a metadata object the runner builds that never gets its
// object/enum inventory populated, so a by-id lookup at a DIFFERENT consumption point finds
// nothing.
//
// Fix: rather than growing NCLMetadata's real per-app-group NCLMetaEnum/NCLEnumMetadata
// object graph (which would need a genuine MultiLanguage-backed caption-resolution pipeline —
// itself dependent on tenant/session infrastructure the skeleton runtime does not have, the
// same reason NCLEnumMetadata.Create(int) was hooked away in the first place), Cecil-rewrite
// ApplyAppGroupAwareEnumMetadataToPageExpressions directly: reuse AlEnumMetadataRegistry (the
// SAME emit-captured (name, options[], indexes[], captions[]) data already used, and already
// accepted as faithful, for Enum::"X".Ordinals()/.Names()/Format() via
// NCLEnumMetadata_CreateByIdAlAware) to compute the OptionString/OptionCaption/OptionValues
// strings directly, in the exact comma-joined shape real BC's NCLEnumMetadata produces (see
// the decompiled body this mirrors). A genuinely unknown enum id (never compiled, never a
// loaded dependency) still throws the SAME NavMetadataNotFoundException real BC would throw —
// this is a substitution for the lookup's data source, not a "never fail" shortcut.
//
// Allowed surface per .claude/rules/precompiled-dll-respect.md: NCLMetaForm lives in Ncl.dll
// (runtime engine), not an AL-business-logic DLL.
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner;

public static partial class BcRuntime
{
    /// <summary>
    /// Replacement for <c>NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions(MetaPageDefinition)</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static MetaPageDefinition NCLMetaForm_ApplyEnumMetadataToMetaPageExpressions(MetaPageDefinition pageDefinition)
    {
        if (pageDefinition == null) return pageDefinition!;

        bool any = false;
        var result = new System.Collections.Generic.List<MetaDataFieldDefinition>(pageDefinition.Expressions.Count);
        foreach (var expr in pageDefinition.Expressions)
        {
            if (expr.EnumIdSpecified)
            {
                any = true;
                var (optionString, optionCaption, optionValues) = ResolveEnumOptionStrings(expr.EnumId);
                result.Add(expr.With(
                    optionString: optionString,
                    optionCaption: optionCaption,
                    optionValues: optionValues));
            }
            else
            {
                result.Add(expr);
            }
        }

        return any ? pageDefinition.WithExpressions(result) : pageDefinition;
    }

    /// <summary>
    /// Replacement for <c>NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions(PageDefinition)</c>
    /// — the mutable (thawed) sibling of the frozen overload above. <c>DataFieldDefinition</c>
    /// has plain settable properties (unlike the immutable <c>MetaDataFieldDefinition</c>'s
    /// <c>With</c> builder), matching the real body's in-place mutation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static PageDefinition NCLMetaForm_ApplyEnumMetadataToPageExpressions(PageDefinition pageDefinition)
    {
        if (pageDefinition == null) return pageDefinition!;

        foreach (var expr in pageDefinition.Expressions)
        {
            if (!expr.EnumIdSpecified) continue;
            var (optionString, optionCaption, optionValues) = ResolveEnumOptionStrings(expr.EnumId);
            expr.OptionString = optionString;
            expr.OptionCaption = optionCaption;
            expr.OptionValues = optionValues;
        }

        return pageDefinition;
    }

    /// <summary>
    /// (OptionString, OptionCaption, OptionValues) for enum <paramref name="enumId"/>, in the
    /// same comma-joined shape real BC's <c>NCLEnumMetadata</c> produces (see
    /// <c>ApplyAppGroupAwareEnumMetadataToPageExpressions</c>'s decompiled body:
    /// <c>string.Join(",", nCLEnumMetadata.OrdinalValues...)</c> etc.) — sourced from
    /// <see cref="AlEnumMetadataRegistry"/>, the SAME emit-captured data already used (and
    /// already accepted as faithful) for <c>Enum::"X".Ordinals()/.Names()</c> via
    /// <see cref="NCLEnumMetadata_CreateByIdAlAware"/>.
    ///
    /// Throws the SAME <c>NavMetadataNotFoundException</c> real BC's own body throws for an
    /// enum id with no registered metadata — a genuinely-unknown enum id must still fail
    /// loudly, not silently resolve to something.
    /// </summary>
    private static (string OptionString, string OptionCaption, string OptionValues) ResolveEnumOptionStrings(int enumId)
    {
        if (!AlEnumMetadataRegistry.TryGet(enumId, out var entry))
            throw new NavMetadataNotFoundException(ObjectType.Enum, enumId);

        var optionString = string.Join(",", entry.Options.Select(o => o ?? string.Empty));
        var optionCaption = string.Join(",", entry.Options.Select((o, i) =>
            (entry.Captions != null && i < entry.Captions.Length ? entry.Captions[i] : null) ?? o ?? string.Empty));
        var optionValues = string.Join(",", entry.Indexes.Select(v => v.ToString(CultureInfo.InvariantCulture)));

        return (optionString, optionCaption, optionValues);
    }
}
