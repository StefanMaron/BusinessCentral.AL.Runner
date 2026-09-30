// EventSubscriberPatches.ExtensionPublishers — the classes of the extensions of a publisher (#5004).
namespace AlRunner.Patches;

public static partial class EventSubscriberPatches
{
    /// <summary>
    /// The compiled <c>TableExtension&lt;N&gt;</c> / <c>PageExtension&lt;N&gt;</c> /
    /// <c>ReportExtension&lt;N&gt;</c> classes of every extension of <paramref name="baseId"/>, so
    /// the event scopes an extension declares are seeded for the base object's subscribers.
    /// </summary>
    /// <param name="basePublisherKind">The dispatcher's kind: <c>Table</c> or <c>Record</c>, <c>Page</c>, <c>Report</c>.</param>
    private static IReadOnlyList<Type> ExtensionClrTypesOf(string basePublisherKind, int baseId)
    {
        var baseKind = basePublisherKind == BcRuntime.PublisherKindTable ? "Table" : basePublisherKind;
        var prefix = baseKind switch
        {
            "Table" => BcRuntime.ExtensionKindTable,
            "Page" => BcRuntime.ExtensionKindPage,
            "Report" => BcRuntime.ExtensionKindReport,
            _ => null,
        };
        if (prefix == null) return Array.Empty<Type>();
        var types = new List<Type>();
        foreach (var extensionId in RecordPatches.ExtensionIdsOfBaseObject(baseKind, baseId))
            types.AddRange(ResolveAllBusinessApplicationTypes(prefix + extensionId));
        return types;
    }
}
