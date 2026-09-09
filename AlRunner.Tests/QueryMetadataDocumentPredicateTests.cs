using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The one IN-PROCESS half of <see cref="QueryMetadataFromBcDocumentTests"/>, split out so it
/// can join <see cref="ObjectMetadataRegistrySerialCollection"/> without dragging that class's
/// subprocess-spawning <c>[SkippableFact]</c> into a serial collection with it (#3613). The
/// registry is process-global, and this test both writes it and reads it back across a window.
/// </summary>
[Collection(ObjectMetadataRegistrySerialCollection.Name)]
public class QueryMetadataDocumentPredicateTests : IDisposable
{
    private const int DivergentQueryId = 70720;
    private const int PlainQueryId = 70721;

    public QueryMetadataDocumentPredicateTests() => AlObjectMetadataRegistry.Clear();
    public void Dispose() => AlObjectMetadataRegistry.Clear();

    /// <summary>
    /// The route is chosen on AVAILABILITY, so a query with no document keeps the derivation
    /// rather than failing. Nothing in the fixture can produce that state — every query there
    /// is compiled — so the claim is made against the predicate itself, which is what both
    /// call sites consult.
    /// </summary>
    [Fact]
    public void QueryWithNoRegisteredDocument_KeepsTheDerivation()
    {
        Assert.False(AlRunner.Patches.RecordPatches.HasBcQueryMetadataDocument(DivergentQueryId));

        AlObjectMetadataRegistry.Register(
            AlRunner.Patches.RecordPatches.BcQueryMetadataKind, DivergentQueryId,
            "QMD Divergent", "<Query><ID>70720</ID></Query>");
        Assert.True(AlRunner.Patches.RecordPatches.HasBcQueryMetadataDocument(DivergentQueryId));

        // Keyed by (kind, id), not by id alone: a TABLE numbered 70720 is a different
        // object and must not satisfy the query's predicate.
        Assert.False(AlRunner.Patches.RecordPatches.HasBcQueryMetadataDocument(PlainQueryId));
    }
}
