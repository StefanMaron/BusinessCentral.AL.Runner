// Events declared in a tableextension / pageextension / reportextension (#5004): AL publishes them
// under the object the extension extends, so a subscriber binds ObjectType::Table / Page / Report
// with the BASE object's id. Corpus codeunit 68508 "Test Extension Declared Events".
using System.Reflection;
using AlRunner.Patches;

namespace AlRunner;

public static partial class BcRuntime
{
    internal const string ExtensionKindTable = "TableExtension";
    internal const string ExtensionKindPage = "PageExtension";
    internal const string ExtensionKindReport = "ReportExtension";

    /// <summary>
    /// Decode an extension object's own class name — <c>TableExtension&lt;N&gt;</c>,
    /// <c>PageExtension&lt;N&gt;</c>, <c>ReportExtension&lt;N&gt;</c> — into its extension kind, the
    /// publisher kind its events are dispatched as (<see cref="PublisherKindTable"/>,
    /// <see cref="PublisherKindPage"/>, <see cref="PublisherKindReport"/>) and the extension's id.
    /// </summary>
    internal static bool TryDecodeExtensionEventPublisherDeclType(
        string declTypeName, out string extensionKind, out string basePublisherKind, out int extensionId)
    {
        foreach (var (prefix, baseKind) in _extensionPublisherPrefixes)
        {
            if (declTypeName.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(declTypeName.AsSpan(prefix.Length), out extensionId))
            {
                extensionKind = prefix;
                basePublisherKind = baseKind;
                return true;
            }
        }
        extensionKind = basePublisherKind = "";
        extensionId = 0;
        return false;
    }

    private static readonly (string Prefix, string BaseKind)[] _extensionPublisherPrefixes =
    {
        (ExtensionKindTable, PublisherKindTable),
        (ExtensionKindPage, PublisherKindPage),
        (ExtensionKindReport, PublisherKindReport),
    };

    /// <summary>The object kind used for the base in registries and keys (<c>Table</c> for a table).</summary>
    internal static string BaseObjectKindOf(string basePublisherKind)
        => basePublisherKind == PublisherKindTable ? "Table" : basePublisherKind;

    /// <summary>
    /// The base object an extension's event was raised on: the extension instance's own
    /// <c>ParentObject</c> (BC's <c>NavRecordExtension</c> / <c>NavFormExtension</c> /
    /// <c>NavReportExtension</c> set it in their constructors) when <paramref name="extensionInstance"/>
    /// has one, else the source registry when it names exactly one base. Null when neither does.
    /// </summary>
    internal static (object? BaseInstance, int BaseId)? ResolveExtensionPublisher(
        string extensionKind, int extensionId, object? extensionInstance)
    {
        if (extensionInstance != null
            && ReadParentObject(extensionInstance) is Microsoft.Dynamics.Nav.Runtime.NavApplicationObjectBase parent)
            return (parent, parent.ObjectId.ObjectNumber);
        var ids = RecordPatches.ExtensionBaseObjectIds(extensionKind, extensionId);
        return ids.Count == 1 ? (null, ids[0]) : null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo?> _parentObjectProperty = new();

    private static object? ReadParentObject(object extensionInstance)
    {
        var pi = _parentObjectProperty.GetOrAdd(extensionInstance.GetType(), static t =>
        {
            // Declared protected internal on each Nav*Extension base; a derived class may hide it
            // with a `new` of the same name (the emitted page extension does), so walk to the base.
            for (var b = t; b != null; b = b.BaseType)
            {
                var p = b.GetProperty("ParentObject",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null && b.Namespace == "Microsoft.Dynamics.Nav.Runtime.Extensions") return p;
            }
            return null;
        });
        try { return pi?.GetValue(extensionInstance); }
        catch (TargetInvocationException) { return null; }
    }
}
