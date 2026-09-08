// RecordPatches.PageTriggerMetadata — NCLMetaForm's twelve Is<Trigger>Defined flags (#3447).
//
// BC computes them in NCLMetaForm.DefinedTriggers by reflecting over the page's compiled class
// (ApplicationObjectClrType) and then over each NCLPageExtension in orderedExtensionObjects.
// The runner builds its NCLMetaForm through CreateEmptyNCLMetaForm and never populates that
// extension list, so a trigger a PAGEEXTENSION declares read false. This replacement keeps BC's
// algorithm — including its own IsTriggerImplemented, so the "…Async" fallback and the
// DeclaringType comparison stay BC's — and supplies the extensions from the runner's own
// pageextension registry instead. See docs/page-rowset-triggers.md#page-trigger-metadata.
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

    private static MethodInfo? _mIsTriggerImplementedOnForm;
    private static MethodInfo? _mIsTriggerImplementedOnFormExtension;
    private static KeyValuePair<string, int>[]? _pageTriggerNames;

    /// <summary>
    /// Replacement for the private <c>NCLMetaForm.get_DefinedTriggers</c>, the single source of
    /// all twelve <c>Is&lt;Trigger&gt;Defined</c> flags. Returns the <c>PageTriggers</c> bitmask
    /// as its underlying <see cref="int"/>.
    /// </summary>
    /// <remarks>
    /// Observably equivalent to BC's body for a page whose extensions BC would have loaded, and
    /// strictly closer to it than the empty-extension-list answer it replaces. Two deliberate
    /// differences, both in the direction of answering later rather than wrongly: the result is
    /// cached only once the page's CLR type resolves (BC caches unconditionally, and the runner
    /// resolves that type lazily from the loaded assemblies), and an unresolvable type answers
    /// "no triggers" without being remembered.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NCLMetaForm_get_DefinedTriggers(object self)
    {
        if (self == null) return 0;
        if (_definedTriggersCache.TryGetValue(self, out var cached)) return cached.Value;

        var pageClrType = NCLMetaApplicationObject_get_ApplicationObjectClrType(self);
        if (pageClrType == null) return 0;

        if (!EnsurePageTriggerReflection(self.GetType())) return 0;

        int mask = 0;
        var extensionTypes = PageExtensionTypesFor(self);
        foreach (var trigger in _pageTriggerNames!)
        {
            if (IsTriggerImplemented(_mIsTriggerImplementedOnForm, pageClrType, trigger.Key, isPublic: false))
            {
                mask |= trigger.Value;
                continue;
            }
            if (_pageOnlyTriggers.Contains(trigger.Key)) continue;
            foreach (var extType in extensionTypes)
                if (IsTriggerImplemented(_mIsTriggerImplementedOnFormExtension, extType, trigger.Key, isPublic: true))
                {
                    mask |= trigger.Value;
                    break;
                }
        }

        PageTriggerAudit.Record(self, pageClrType, extensionTypes, _pageTriggerNames!, mask);
        _definedTriggersCache.AddOrUpdate(self, new StrongBox<int>(mask));
        return mask;
    }

    private static bool IsTriggerImplemented(MethodInfo? bcCheck, Type clrType, string triggerName, bool isPublic)
    {
        if (bcCheck == null) return false;
        try
        {
            return (bool)bcCheck.Invoke(null, new object?[] { clrType, triggerName, isPublic })!;
        }
        catch (TargetInvocationException)
        {
            // BC's CheckTrigger throws when the name is absent from the type ENTIRELY — i.e.
            // NavForm/NavFormExtension no longer declares this trigger, or an extension class
            // does not derive from NavFormExtension. Neither is a page that "declares the
            // trigger", so the flag stays false and the surrounding scan continues.
            return false;
        }
    }

    /// <summary>The compiled <c>PageExtension{id}</c> classes extending this page, in id order.</summary>
    private static List<Type> PageExtensionTypesFor(object metaForm)
    {
        var types = new List<Type>();
        if (!TryGetMetaObjectNumber(metaForm, out _, out var pageId)) return types;
        foreach (var extensionId in GetPageExtensionIdsForPage(pageId))
        {
            var t = FindClrTypeByName("PageExtension" + extensionId);
            if (t != null) types.Add(t);
        }
        return types;
    }

    private static bool EnsurePageTriggerReflection(Type metaFormType)
    {
        if (_pageTriggerNames != null) return true;

        var pageTriggers = metaFormType.GetNestedType("PageTriggers", BindingFlags.NonPublic | BindingFlags.Public);
        var baseType = metaFormType.BaseType;
        while (baseType != null && baseType.Name != "NCLMetaApplicationObject") baseType = baseType.BaseType;
        if (pageTriggers == null || baseType == null) return false;

        var generic = BcShape.FindMethod(baseType, "IsTriggerImplemented",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
            surface: "page trigger metadata", member: "NCLMetaApplicationObject.IsTriggerImplemented",
            detail: "the runner recomputes NCLMetaForm's Is<Trigger>Defined flags with BC's own "
                  + "check — see docs/page-rowset-triggers.md#page-trigger-metadata",
            types: new[] { typeof(Type), typeof(string), typeof(bool) });
        if (generic == null || !generic.IsGenericMethodDefinition) return false;

        _mIsTriggerImplementedOnForm = generic.MakeGenericMethod(
            typeof(Microsoft.Dynamics.Nav.Runtime.NavForm));
        _mIsTriggerImplementedOnFormExtension = generic.MakeGenericMethod(
            typeof(Microsoft.Dynamics.Nav.Runtime.Extensions.NavFormExtension));

        var names = new List<KeyValuePair<string, int>>();
        foreach (var v in Enum.GetValues(pageTriggers))
        {
            var name = v.ToString();
            if (string.IsNullOrEmpty(name) || name == "Unknown") continue;
            names.Add(new KeyValuePair<string, int>(name!, Convert.ToInt32(v)));
        }
        _pageTriggerNames = names.ToArray();
        return _pageTriggerNames.Length > 0;
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
/// Opt-in record of what <see cref="RecordPatches.NCLMetaForm_get_DefinedTriggers"/> answered,
/// one line per page, written when the process exits.
///
/// <para>The flags have no AL-observable of their own — BC's own consumers of them sit on
/// dispatch paths the runner serves itself — so this is what a test can assert against. Off
/// unless <c>AL_RUNNER_PAGE_TRIGGER_AUDIT=1</c>, and written straight to the process's stdout
/// handle, because the test phase redirects <see cref="Console"/>.</para>
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
