using System.Text;
using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Thread-safety regression for <see cref="TableFieldRegistry.GetSourceTableId"/>.
///
/// The method used to mutate two non-concurrent dictionaries on its deferred-resolution
/// branch. Those writes ran on the parallel rewrite path (Parallel.For in Pipeline.cs),
/// so two threads resolving still-pending pages at once threw
/// "Operations that change non-concurrent collections must have exclusive access...".
/// Resolution now happens serially in ParseAndRegister, leaving GetSourceTableId a pure reader.
///
/// Placed in the Pipeline collection because TableFieldRegistry static state is process-global
/// and shared with every other in-process pipeline test; the collection serializes them so no
/// sibling test mutates the registry while this test's own Parallel.For runs.
/// </summary>
[Collection("Pipeline")]
public class TableFieldRegistryConcurrencyTests
{
    [Fact]
    public void GetSourceTableId_ParallelReadsOnPendingPages_NoRaceAndResolvesCorrectly()
    {
        const int count = 1000;
        const int firstPageId = 50000;
        const int firstTableId = 60000;

        TableFieldRegistry.Clear();
        try
        {
            // Parse the pages FIRST, before any table is registered, so every page's
            // SourceTable is unresolved and gets parked in the pending map.
            var pages = new StringBuilder();
            for (int i = 0; i < count; i++)
                pages.Append($"page {firstPageId + i} \"Page {i}\" {{ SourceTable = \"Tbl {i}\"; }}\n");
            TableFieldRegistry.ParseAndRegister(pages.ToString());

            // Now register the matching tables.
            var tables = new StringBuilder();
            for (int i = 0; i < count; i++)
                tables.Append($"table {firstTableId + i} \"Tbl {i}\" {{ fields {{ field(1; PK; Integer) {{ }} }} }}\n");
            TableFieldRegistry.ParseAndRegister(tables.ToString());

            // Hammer GetSourceTableId concurrently for every page id. Each index is written by
            // exactly one iteration, so the result array itself is race-free.
            var results = new int?[count];
            var ex = Record.Exception(() =>
                Parallel.For(0, count, i => results[i] = TableFieldRegistry.GetSourceTableId(firstPageId + i)));

            // No concurrent-collection exception ...
            Assert.Null(ex);
            // ... and every page resolves to its declared source table.
            for (int i = 0; i < count; i++)
                Assert.Equal(firstTableId + i, results[i]);

            // Negative direction: an unregistered page id returns null, not a bogus id.
            Assert.Null(TableFieldRegistry.GetSourceTableId(firstPageId + count + 1));
        }
        finally
        {
            // Leave the shared static registry clean for sibling tests in this collection.
            TableFieldRegistry.Clear();
        }
    }
}
