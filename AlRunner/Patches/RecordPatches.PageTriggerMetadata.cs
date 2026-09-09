// RecordPatches.PageTriggerMetadata — NCLMetaForm's twelve Is<Trigger>Defined flags (#3447).
//
// BC computes them in NCLMetaForm.DefinedTriggers by reflecting over the page's compiled class
// (ApplicationObjectClrType) and then over each NCLPageExtension in orderedExtensionObjects.
// The runner builds its NCLMetaForm through CreateEmptyNCLMetaForm and never populates that
// extension list, so a trigger a PAGEEXTENSION declares read false. This replacement keeps BC's
// algorithm — same names off BC's own PageTriggers, the same "…Async" fallback and DeclaringType
// comparison, the same page-only exclusion — and supplies the extensions from the runner's own
// pageextension registry. See docs/page-rowset-triggers.md#page-trigger-metadata.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // BC's own exclusion: OnFindRecord, OnNextRecord and OnInit are page-only — a pageextension
    // declaring one does NOT set the flag (NCLMetaForm.DefinedTriggers, BC 27.5 and 28.4).
    private static readonly HashSet<string> _pageOnlyTriggers =
        new(StringComparer.Ordinal) { "OnFindRecord", "OnNextRecord", "OnInit" };

    private static readonly ConditionalWeakTable<object, StrongBox<int>> _definedTriggersCache = new();

    private static KeyValuePair<string, int>[]? _pageTriggerNames;

    /// <summary>
    /// Replacement for the private <c>NCLMetaForm.get_DefinedTriggers</c>, the single source of
    /// all twelve <c>Is&lt;Trigger&gt;Defined</c> flags. Returns the <c>PageTriggers</c> bitmask
    /// as its underlying <see cref="int"/>.
    /// </summary>
    /// <remarks>
    /// Observably equivalent to BC's own body — same names off BC's own <c>PageTriggers</c>, same
    /// declaration check, same page-only exclusion — for a page whose extensions BC would have
    /// loaded (NCLMetaForm.DefinedTriggers, BC 27.0, 27.5 and 28.4). One deliberate difference: the
    /// answer is cached only once the page's CLR type resolves, because the runner resolves that
    /// type lazily from the loaded assemblies and BC's unconditional cache would freeze an
    /// all-false answer taken too early.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NCLMetaForm_get_DefinedTriggers(object self)
    {
        if (self == null) return 0;
        if (_definedTriggersCache.TryGetValue(self, out var cached)) return cached.Value;

        var pageClrType = NCLMetaApplicationObject_get_ApplicationObjectClrType(self);
        if (pageClrType == null) return 0;

        EnsurePageTriggerNames(self.GetType());

        int mask = 0;
        var extensionTypes = PageExtensionTypesFor(self, out var allExtensionsResolved);
        foreach (var trigger in _pageTriggerNames!)
        {
            if (IsTriggerImplemented(typeof(Microsoft.Dynamics.Nav.Runtime.NavForm),
                                     pageClrType, trigger.Key, isPublic: false, PageTriggerSurface))
            {
                mask |= trigger.Value;
                continue;
            }
            if (_pageOnlyTriggers.Contains(trigger.Key)) continue;
            foreach (var extType in extensionTypes)
                if (IsTriggerImplemented(typeof(Microsoft.Dynamics.Nav.Runtime.Extensions.NavFormExtension),
                                         extType, trigger.Key, isPublic: true, PageTriggerSurface))
                {
                    mask |= trigger.Value;
                    break;
                }
        }

        PageTriggerAudit.Record(self, pageClrType, extensionTypes, _pageTriggerNames!, mask);
        // Cache only an answer computed over EVERY extension this page has. One whose compiled
        // class has not loaded yet is the same lazily-resolving-type problem the page arm guards
        // against above, and caching around it would freeze a mask missing that extension's
        // triggers for the life of the process.
        if (allExtensionsResolved) _definedTriggersCache.AddOrUpdate(self, new StrongBox<int>(mask));
        return mask;
    }

    /// <summary>
    /// BC's own declaration check, transcribed rather than called — shared by the page flags
    /// (#3447) and the table flags (#3556).
    ///
    /// <para>Calling BC's <c>NCLMetaApplicationObject.IsTriggerImplemented</c> is not portable
    /// across the versions this runner supports: the <b>static</b>
    /// <c>(Type, string, bool)</c> overload the extension arm needs — the runner holds an
    /// extension's Type, not an NCLPageExtension / NCLTableExtension instance — exists on 27.5
    /// and 28.x and does NOT exist on 27.0, which declares only the instance <c>(string, bool)</c>
    /// form reading its own receiver. Resolving it and answering "no triggers" when it is absent
    /// is what made every flag false on the 27.0 leg of PR #3557 while 28.x passed.</para>
    ///
    /// <para>The body below is BC's <c>CheckTrigger</c> local function, whose decision is
    /// identical on 27.0, 27.5 and 28.4: look the name up, then the <c>…Async</c> spelling, and
    /// answer whether the resolved method was declared somewhere OTHER than
    /// <paramref name="platformBase"/>. Not a transcription character for character — the guard
    /// before the throw is the runner's, and it is what decides that an absent name is a BC-shape
    /// gap rather than an ordinary "this object declares nothing".</para>
    /// </summary>
    internal readonly record struct TriggerSurface(string Surface, string Subject, string Doc);

    private static readonly TriggerSurface PageTriggerSurface = new(
        "page trigger metadata", "page", "docs/page-rowset-triggers.md#page-trigger-metadata");

    private static bool IsTriggerImplemented(
        Type platformBase, Type clrType, string triggerName, bool isPublic, TriggerSurface surface)
        => CheckTrigger(platformBase, clrType, triggerName, isPublic, surface)
        || CheckTrigger(platformBase, clrType, triggerName + "Async", isPublic, surface);

    private static bool CheckTrigger(
        Type platformBase, Type clrType, string name, bool isPublic, TriggerSurface surface)
    {
        var flags = BindingFlags.Instance | (isPublic ? BindingFlags.Public : BindingFlags.NonPublic);
        var method = LookupTrigger(clrType, name, flags, surface);
        if (method != null) return method.DeclaringType != platformBase;

        // BC throws here, and so must this: the lookup searches the whole hierarchy, so a null
        // means platformBase itself no longer declares the trigger — the shape this computation
        // rests on. Answering false instead would silently report "declares nothing" for every
        // object in the run, which is #3447 reappearing with no signal.
        if (LookupTrigger(clrType, name + "Async", flags, surface) != null
         || LookupTrigger(platformBase, name, flags, surface) != null) return false;

        throw new AlRunner.Infrastructure.BcShapeGapException(
            surface.Surface, $"{platformBase.Name}.{name}",
            $"neither {name} nor {name}Async resolves on {clrType.Name}, so the runner cannot say "
            + $"which {surface.Subject} triggers this {surface.Subject} declares — see {surface.Doc}");
    }

    /// <summary>
    /// One BC-typed method lookup for both trigger-metadata computations, through
    /// <see cref="BcShape"/> (#3069): a trigger name that grows a second declaration must arrive
    /// as a named gap, not as an <c>AmbiguousMatchException</c> an <c>asserterror</c> can absorb.
    /// No <c>types:</c> filter — the triggers have different signatures and BC resolves them by
    /// name too.
    /// </summary>
    private static MethodInfo? LookupTrigger(
        Type declaring, string name, BindingFlags flags, TriggerSurface surface)
        => BcShape.FindMethod(declaring, name, flags,
            surface: surface.Surface, member: $"{declaring.Name}.{name}",
            detail: $"the runner asks which {surface.Subject} triggers this {surface.Subject} declares, "
                  + "and two declarations of one trigger name leave no way to say which body BC "
                  + $"would have counted — see {surface.Doc}");

    /// <summary>The compiled <c>PageExtension{id}</c> classes extending this page, in id order.</summary>
    private static List<Type> PageExtensionTypesFor(object metaForm, out bool allResolved)
    {
        var types = new List<Type>();
        allResolved = true;
        if (!TryGetMetaObjectNumber(metaForm, out _, out var pageId)) return types;

        foreach (var extensionId in GetPageExtensionIdsForPage(pageId))
        {
            var t = FindClrTypeByName("PageExtension" + extensionId);
            if (t != null) { types.Add(t); continue; }

            allResolved = false;
            // Loud, once per (page, extension): the flags would otherwise report this page as
            // declaring fewer triggers than it does, and the reader has no way to tell that
            // answer apart from a page whose extension genuinely declares none.
            // `[warn]` on stdout, the shape this file's neighbours use - see the tag note in
            // RunnerPageInstance.TryCreate.
            if (_unresolvedExtensionTypesWarned.Add((pageId, extensionId)))
                Console.Out.WriteLine(
                    $"[warn] RecordPatches: page {pageId}: pageextension {extensionId} extends it, but no "
                    + $"compiled PageExtension{extensionId} type is loaded, so the triggers it declares are "
                    + "missing from this page's Is<Trigger>Defined flags");
        }
        return types;
    }

    private static readonly HashSet<(int Page, int Extension)> _unresolvedExtensionTypesWarned = new();

    /// <summary>The twelve <c>PageTriggers</c> members, read off BC's own private nested enum.</summary>
    private static void EnsurePageTriggerNames(Type metaFormType)
    {
        if (_pageTriggerNames != null) return;

        var pageTriggers = metaFormType.GetNestedType("PageTriggers", BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new AlRunner.Infrastructure.BcShapeGapException(
                "page trigger metadata", "NCLMetaForm.PageTriggers",
                "BC no longer declares the enum whose members name the twelve page triggers, so the "
                + "runner cannot compute the Is<Trigger>Defined flags at all");

        var names = new List<KeyValuePair<string, int>>();
        foreach (var v in Enum.GetValues(pageTriggers))
        {
            var name = v.ToString();
            if (string.IsNullOrEmpty(name) || name == "Unknown") continue;
            names.Add(new KeyValuePair<string, int>(name!, Convert.ToInt32(v)));
        }

        if (names.Count == 0)
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "page trigger metadata", "NCLMetaForm.PageTriggers",
                "BC's page-trigger enum declares no members besides Unknown");

        _pageTriggerNames = names.ToArray();
    }

    /// <summary>
    /// The <c>ObjectType</c> ("Page", "Table", …) and object number an NCLMeta* instance carries
    /// on its base <c>objectId</c> field. Non-public fields are not discovered through
    /// inheritance by <c>GetField</c>, so the type chain is walked by hand.
    /// </summary>
    internal static bool TryGetMetaObjectNumber(object? self, out string? objectType, out int id)
    {
        objectType = null;
        id = 0;
        if (self == null) return false;

        FieldInfo? objIdField = null;
        for (var t = self.GetType(); t != null && objIdField == null; t = t.BaseType)
            objIdField = t.GetField("objectId",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var objId = objIdField?.GetValue(self);
        if (objId == null) return false;

        var numProp = objId.GetType().GetProperty("ObjectNumber", BindingFlags.Public | BindingFlags.Instance);
        if (numProp == null) return false;
        id = (int)numProp.GetValue(objId)!;
        objectType = objId.GetType()
            .GetProperty("ObjectType", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(objId)?.ToString();
        return true;
    }

}

/// <summary>
/// Opt-in AUDIT of what <see cref="RecordPatches.NCLMetaForm_get_DefinedTriggers"/> answered,
/// one line per page, written when the process exits — an inspection channel like
/// <c>AL_RUNNER_HOOK_AUDIT</c>, not a diagnosis, so it is not <c>[warn]</c>-tagged and reports
/// no problem. Off unless <c>AL_RUNNER_PAGE_TRIGGER_AUDIT=1</c>.
///
/// <para>Written straight to the process's stdout HANDLE: the test phase redirects
/// <see cref="Console"/>, so a <c>Console.Out</c> write from here is swallowed.</para>
/// </summary>
internal static class PageTriggerAudit
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("AL_RUNNER_PAGE_TRIGGER_AUDIT") == "1";

    private static readonly object Gate = new();
    private static readonly SortedDictionary<int, string> Lines = new();
    private static bool _flushRegistered;

    internal static void Record(
        object metaForm, Type pageClrType, List<Type> extensionTypes,
        KeyValuePair<string, int>[] triggers, int mask)
    {
        if (!Enabled) return;
        if (!RecordPatches.TryGetMetaObjectNumber(metaForm, out _, out var pageId)) return;

        var defined = triggers.Where(t => (mask & t.Value) != 0).Select(t => t.Key).ToArray();
        var line = $"[page-trigger-audit] page={pageId} clr={pageClrType.Name}"
                 + $" ext={(extensionTypes.Count == 0 ? "-" : string.Join(",", extensionTypes.Select(t => t.Name)))}"
                 + $" triggers={(defined.Length == 0 ? "-" : string.Join(",", defined))}";

        lock (Gate)
        {
            Lines[pageId] = line;
            if (_flushRegistered) return;
            _flushRegistered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    private static void Flush()
    {
        lock (Gate)
        {
            try
            {
                using var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                foreach (var line in Lines.Values) stdout.WriteLine(line);
            }
            catch { }
        }
    }
}
