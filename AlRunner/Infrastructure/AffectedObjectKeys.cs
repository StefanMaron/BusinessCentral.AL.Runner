// AffectedObjectKeys — the object keys affectedOnly's coverage and change sides must build identically,
// and the attribution of one executed statement to its object (#5003).
// Rules: docs/server-mode.md#affectedonly-and-files-declaring-several-objects.
namespace AlRunner.Infrastructure;

internal static class AffectedObjectKeys
{
    internal static string Of(AffectedObjectId id)
        => $"{id.Kind}|{(id.Id.HasValue ? "id:" + id.Id.Value : "name:" + id.Name)}";

    /// <summary>The key of an AL object named by its class label (<c>AlCallStackCapture.ParseObjectTypeAndId</c>) and id.</summary>
    internal static string OfObjectClass(string label, int id)
        => Of(new AffectedObjectId(label == "CodeUnit" ? "Codeunit" : label, id, ""));

    /// <summary>
    /// The object key one executed statement belongs to. A file the change model tracks as one object
    /// keys as that object. A file it records as declaring several objects keys by the object whose
    /// scope ran the statement, which is safe because changing such a file falls back to a full run.
    /// Null for a file in neither set, which the caller treats as unattributable.
    /// </summary>
    internal static string? OfStatement(
        AlCoverageTracker.AlStatementRecord statement,
        IReadOnlyDictionary<string, AffectedObjectId> trackedByPath,
        IReadOnlySet<string> multiObjectPaths)
    {
        if (trackedByPath.TryGetValue(statement.FilePath, out var identity)) return Of(identity);
        if (multiObjectPaths.Contains(statement.FilePath) && statement.ObjectId != 0)
            return OfObjectClass(statement.ObjectLabel, statement.ObjectId);
        return null;
    }
}
