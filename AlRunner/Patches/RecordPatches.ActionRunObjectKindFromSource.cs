// RecordPatches.ActionRunObjectKindFromSource — the object KIND a precompiled page's action
// states after RunObject, read from the page's own AL source inside its .app (#4622).
//
// SymbolReference.json states RunObject as a bare name ("Purchase Statistics"), and a name two
// objects answer (page 161 and report 312) cannot be resolved from it. The compiled page DLL
// does not help: a RunObject action compiles to no code at all, only OnAction triggers do, so
// the running process holds nothing that names the kind. The .app's src/ tree does — the
// compiler's own input, `RunObject = Page "Purchase Statistics";` — and it is read with BC's
// own AL parser, the same one every other source-derived answer in this class uses.
using System.Collections.Concurrent;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using MetaTypes = Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // (app path, source file) -> file text, or null when the .app ships no such file. A
    // Base-Application-sized .app is tens of MB and nests its package one level in, so one
    // read per file per process, not one per action invocation.
    private static readonly ConcurrentDictionary<(string AppPath, string File), string?> _actionSourceText = new();

    /// <summary>
    /// The object kind member <paramref name="memberId"/> of the precompiled page or
    /// pageextension <paramref name="declaringObjectId"/> names after <c>RunObject</c>, as its AL
    /// source in the .app states it. Null when that cannot be established, with
    /// <paramref name="whyNot"/> saying which step failed: no symbol entry, no source file stated,
    /// a symbols-only .app, no such action in the file, or a RunObject whose kind is not one of
    /// the five AL allows.
    /// </summary>
    internal static MetaTypes.RunObjectType? TryReadActionRunObjectKindFromSource(
        int declaringObjectId, int memberId, bool isExtension, out string whyNot)
    {
        string? appPath = null, sourceFile = null;
        Dictionary<int, string>? memberNames = null;
        if (isExtension)
        {
            foreach (var (path, symbols) in EnumerateRegisteredBcAppSymbols(
                         "pages and pageextensions (dependency page metadata)"))
            {
                var ext = symbols.PageExtensions?.FirstOrDefault(e => e.Id == declaringObjectId);
                if (ext == null) continue;
                (appPath, sourceFile, memberNames) = (path, ext.ReferenceSourceFileName, ext.MemberIdToName);
                break;
            }
        }
        else if (TryGetDependencyPageSymbol(declaringObjectId) is { } page)
        {
            (appPath, sourceFile, memberNames) =
                (TryGetDependencyPageAppPath(declaringObjectId), page.ReferenceSourceFileName, page.MemberIdToName);
        }

        var objectLabel = $"{(isExtension ? "pageextension" : "page")} {declaringObjectId}";
        if (appPath == null || memberNames == null || !memberNames.TryGetValue(memberId, out var actionName))
        {
            whyNot = $"{objectLabel} has no symbol entry for member {memberId}";
            return null;
        }
        if (string.IsNullOrEmpty(sourceFile))
        {
            whyNot = $"the symbol file states no source file for {objectLabel}";
            return null;
        }

        var text = _actionSourceText.GetOrAdd((appPath, sourceFile!),
            key => BcAppSymbolCache.TryReadSourceFile(key.AppPath, key.File));
        if (string.IsNullOrEmpty(text))
        {
            whyNot = $"{Path.GetFileName(appPath)} ships no AL source for {objectLabel} ({sourceFile})";
            return null;
        }

        return ActionRunObjectKindInSource(text!, declaringObjectId, isExtension, actionName, out whyNot);
    }

    /// <summary>
    /// The kind word after <c>RunObject =</c> on action <paramref name="actionName"/> of the page
    /// or pageextension <paramref name="objectId"/> declared in <paramref name="source"/>. The
    /// object is matched by id so a file declaring several objects cannot answer for the wrong
    /// one; the action by its declared name, case-insensitively as AL resolves names.
    /// </summary>
    internal static MetaTypes.RunObjectType? ActionRunObjectKindInSource(
        string source, int objectId, bool isExtension, string actionName, out string whyNot)
    {
        var objectLabel = $"{(isExtension ? "pageextension" : "page")} {objectId}";
        NavCA.SyntaxNode? declaring = null;
        foreach (var obj in ParseAlObjects(source))
        {
            var id = obj switch
            {
                NavSyntax.PageSyntax p when !isExtension => ObjectIdOf(p),
                NavSyntax.PageExtensionSyntax pe when isExtension => ObjectIdOf(pe),
                _ => null,
            };
            if (id == objectId)
            {
                declaring = obj;
                break;
            }
        }
        if (declaring == null)
        {
            whyNot = $"the AL source stated for {objectLabel} does not declare it";
            return null;
        }

        var kinds = new HashSet<MetaTypes.RunObjectType>();
        var unreadable = new List<string>();
        foreach (var action in declaring.DescendantNodes().OfType<NavSyntax.PageActionSyntax>())
        {
            if (!string.Equals(IdentText(action.Name), actionName, StringComparison.OrdinalIgnoreCase)) continue;
            var value = PropValue(action.PropertyList, "RunObject")?.ToString()?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            var kindWord = value!.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
            switch (kindWord.ToLowerInvariant())
            {
                case "page": kinds.Add(MetaTypes.RunObjectType.Page); break;
                case "codeunit": kinds.Add(MetaTypes.RunObjectType.Codeunit); break;
                case "report": kinds.Add(MetaTypes.RunObjectType.Report); break;
                case "xmlport": kinds.Add(MetaTypes.RunObjectType.XMLport); break;
                case "query": kinds.Add(MetaTypes.RunObjectType.Query); break;
                default: unreadable.Add(value); break;
            }
        }

        if (unreadable.Count > 0 || kinds.Count != 1)
        {
            whyNot = unreadable.Count > 0
                ? $"action '{actionName}' of {objectLabel} states RunObject = {string.Join(" / ", unreadable)}, "
                  + "whose object kind could not be read"
                : kinds.Count == 0
                    ? $"the AL source for {objectLabel} declares no action '{actionName}' with a RunObject"
                    : $"action '{actionName}' of {objectLabel} states more than one RunObject kind "
                      + $"({string.Join(", ", kinds)})";
            return null;
        }
        whyNot = "";
        return kinds.First();
    }
}
