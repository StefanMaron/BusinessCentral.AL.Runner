// CompanySystemTableSeedLatchTests — AlRunner#3187.
//
// RUNNER-MECHANISM tests. Nothing here is a claim about Business Central: that the Company
// system table holds a row for the current company is plain BC behaviour and is adjudicated
// upstream (corpus codeunit 60700, #2329). What these pin is the runner's own once-per-bundle
// latch on the seed that writes it.
//
// THE DEFECT
//   EnsureCompanySystemTableRowSeeded set its flag on its FIRST line, before doing any work:
//
//       if (_companyRowSeededForThisBundle) return;
//       _companyRowSeededForThisBundle = true;   // <-- before the work
//
//   So the flag meant "someone started", not "the row is there". A throw out of the seed left
//   it latched, and a later call returned early having done nothing, against a table the
//   method reports as seeded. The sibling seeder next door carries the opposite in a comment,
//   because #2941's review rejected exactly this pattern there.
//
// WHY IT IS NOT A ONE-LINE MOVE, AND WHAT THESE TESTS ENCODE
//   Two of the exits mean "there is nothing to seed", and moving one assignment to the end of
//   the method would turn them from "do not retry" into "retry on every call". So the fix
//   decides PER EXIT, and each of these tests pins one row of that decision:
//
//     NoCompanyTable        settled  -> latches, not retried  (NoCompanyMetatable_...)
//     NoDataAccessSource    not-yet  -> reported, retried     (NoDataAccessSourceYet_...)
//     NoCompanyIdentity     not-yet  -> reported, retried     (NoCompanyIdentityYet_...)
//     Inserted              settled  -> latches               (...RetriesAndInserts, 2nd call)
//     AlreadyPresent        settled  -> latches, not retried  (AnAlreadyPresentRow_...)
//     threw                 not-yet  -> reported, retried     (AThrownSeed_...)
//
// WHY A DELEGATE SEAM AND NOT A FIXTURE RUN
//   The three BC-typed steps — NCLMetaTable, the DataAccessSource, BC's own provider Insert —
//   cannot be constructed in a unit test, and there is no supported way to make the real
//   Insert refuse from outside the process. EnsureCompanySystemTableRowSeededCore takes them
//   as parameters; production passes the real ones. No environment variable is involved and
//   nothing behaves differently in production, which is the same shape SeededRowColumnsTests
//   uses to drive the production resolution logic over a plain tuple.
//
// WHY THE RED IS REAL
//   These tests were run against the refactor with the OLD policy restored (the settled
//   predicate answering true for every outcome, which is what latching on line 2 amounts to):
//   the three retry tests failed with the second call returning AlreadySeededThisBundle and no
//   second attempt made. Restoring the per-exit predicate turned them green.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

// Serial: drives process-wide static seed state and swaps Console.Error to read the [warn]
// lines the production call sites emit. See ConsoleFilterSerialCollection.
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class CompanySystemTableSeedLatchTests : IDisposable
{
    private readonly TextWriter _savedErr = Console.Error;

    public CompanySystemTableSeedLatchTests()
        => AlRunner.Patches.RecordPatches.ResetCompanySystemTableForNewBundle();

    public void Dispose()
    {
        Console.SetError(_savedErr);
        AlRunner.Patches.RecordPatches.ResetCompanySystemTableForNewBundle();
    }

    /// <summary>
    /// BC's own already-exists refusal is matched by TYPE NAME in the seed, so a local type of
    /// the same name drives that branch exactly as the real one does.
    /// </summary>
    private sealed class NavRecordAlreadyExistsException : Exception
    {
        internal NavRecordAlreadyExistsException(string message) : base(message) { }
    }

    private sealed class ProviderRefusedException : Exception
    {
        internal ProviderRefusedException(string message) : base(message) { }
    }

    /// <summary>One seed's steps, with a record of what the seed actually did.</summary>
    private sealed class Seed
    {
        internal object? Meta = new object();
        internal object? Source = new object();
        internal (string? Name, object? Id) Identity = ("CRONUS", Guid.Empty);
        internal Exception? InsertThrows;

        internal int MetaLookups;
        internal int InsertAttempts;
        internal readonly List<string> RowsWritten = new();

        internal AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome Run()
            => AlRunner.Patches.RecordPatches.EnsureCompanySystemTableRowSeededCore(
                resolveMeta: () => { MetaLookups++; return Meta; },
                resolveSource: () => Source,
                readIdentity: () => Identity,
                insertRow: (_, _, name, _) =>
                {
                    InsertAttempts++;
                    if (InsertThrows != null) throw InsertThrows;
                    RowsWritten.Add(name);
                });
    }

    private static (T Result, string Stderr) CapturingStderr<T>(Func<T> body)
    {
        var saved = Console.Error;
        var sink = new StringWriter();
        try
        {
            Console.SetError(sink);
            return (body(), sink.ToString());
        }
        finally
        {
            Console.SetError(saved);
        }
    }

    // ---------------------------------------------------------------- the reported defect

    [Fact]
    public void AThrownSeed_DoesNotLatch_AndTheNextCallRetriesAndWritesTheRow()
    {
        var seed = new Seed { InsertThrows = new ProviderRefusedException("the provider refused") };

        var (first, _) = CapturingStderr(seed.Run);

        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.Failed, first);
        Assert.Empty(seed.RowsWritten);

        // The second call is the whole point of #3187: pre-fix it returned early on a latched
        // flag and never attempted the insert again.
        seed.InsertThrows = null;
        var second = seed.Run();

        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.Inserted, second);
        Assert.Equal(2, seed.InsertAttempts);
        Assert.Equal(new[] { "CRONUS" }, seed.RowsWritten);
        Assert.False(
            AlRunner.Patches.RecordPatches.CompanySeedIsSettled(first),
            "a seed that threw wrote no row, so it must not settle the question for the bundle");
        Assert.Equal(
            AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.Inserted,
            AlRunner.Patches.RecordPatches.CompanySeedOutcomeForThisBundle);

        // ...and having written it, it now settles: a third call does no work.
        Assert.Equal(
            AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.AlreadySeededThisBundle,
            seed.Run());
        Assert.Equal(2, seed.InsertAttempts);
    }

    [Fact]
    public void AThrownSeed_IsLoud_NamingTheExceptionTypeAndMessageAtWarn()
    {
        var seed = new Seed
        {
            // Wrapped, because the real Insert is invoked by reflection and every genuine
            // failure arrives inside a TargetInvocationException. The unwrap is what puts the
            // real type name and message on the line rather than "TargetInvocationException:
            // Exception has been thrown by the target of an invocation."
            InsertThrows = new TargetInvocationException(
                new ProviderRefusedException("Company: primary key is not writable")),
        };

        var (outcome, stderr) = CapturingStderr(seed.Run);

        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.Failed, outcome);
        Assert.Contains("[warn] CompanySystemTable: could not seed the Company row (2000000006)", stderr);
        Assert.Contains("ProviderRefusedException: Company: primary key is not writable", stderr);
        Assert.Contains("Company.Get(CompanyName()) will fail", stderr);
        Assert.DoesNotContain("TargetInvocationException", stderr);
    }

    // ---------------------------------------------------------------- the settled exits

    [Fact]
    public void NoCompanyMetatableInTheBundle_Settles_AndIsNotRetried()
    {
        var seed = new Seed { Meta = null };

        var (first, stderr) = CapturingStderr(seed.Run);

        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.NoCompanyTable, first);
        Assert.True(AlRunner.Patches.RecordPatches.CompanySeedIsSettled(first));
        // Nothing to seed is not a failure, so nothing is reported.
        Assert.Equal("", stderr);
        Assert.Equal(1, seed.MetaLookups);

        Assert.Equal(
            AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.AlreadySeededThisBundle,
            seed.Run());
        // The call count is the claim: a bundle whose closure has no Company metatable must not
        // re-ask per app group.
        Assert.Equal(1, seed.MetaLookups);
        Assert.Equal(0, seed.InsertAttempts);
    }

    [Fact]
    public void AnAlreadyPresentRow_Settles_AndIsNotRetried_AndIsNotReported()
    {
        var seed = new Seed
        {
            InsertThrows = new NavRecordAlreadyExistsException("record already exists"),
        };

        var (first, stderr) = CapturingStderr(seed.Run);

        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.AlreadyPresent, first);
        Assert.True(AlRunner.Patches.RecordPatches.CompanySeedIsSettled(first));
        Assert.Equal("", stderr);

        Assert.Equal(
            AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.AlreadySeededThisBundle,
            seed.Run());
        Assert.Equal(1, seed.InsertAttempts);
    }

    // ---------------------------------------------------------------- the not-yet exits

    [Fact]
    public void NoDataAccessSourceYet_DoesNotLatch_AndTheNextCallRetriesAndWritesTheRow()
    {
        var seed = new Seed { Source = null };

        var (first, stderr) = CapturingStderr(seed.Run);

        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.NoDataAccessSource, first);
        Assert.Contains("[warn] CompanySystemTable: the skeleton session has no DataAccessSource yet", stderr);
        Assert.Equal(0, seed.InsertAttempts);

        seed.Source = new object();
        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.Inserted, seed.Run());
        Assert.Equal(new[] { "CRONUS" }, seed.RowsWritten);
        Assert.False(AlRunner.Patches.RecordPatches.CompanySeedIsSettled(first));
    }

    [Fact]
    public void NoCompanyIdentityYet_DoesNotLatch_AndTheNextCallRetriesAndWritesTheRow()
    {
        var seed = new Seed { Identity = (null, null) };

        var (first, stderr) = CapturingStderr(seed.Run);

        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.NoCompanyIdentity, first);
        Assert.Contains("[warn] CompanySystemTable: the skeleton NavCompany exposes no company name", stderr);
        Assert.Equal(0, seed.InsertAttempts);

        seed.Identity = ("CRONUS", Guid.Empty);
        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.Inserted, seed.Run());
        Assert.Equal(new[] { "CRONUS" }, seed.RowsWritten);
        Assert.False(AlRunner.Patches.RecordPatches.CompanySeedIsSettled(first));
    }

    // ---------------------------------------------------------------- the per-bundle reset

    [Fact]
    public void ResetForANewBundle_ClearsASettledOutcome_SoTheNextBundleSeedsItsOwnRow()
    {
        var seed = new Seed();
        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.Inserted, seed.Run());
        Assert.Equal(
            AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.AlreadySeededThisBundle,
            seed.Run());

        AlRunner.Patches.RecordPatches.ResetCompanySystemTableForNewBundle();

        Assert.Null(AlRunner.Patches.RecordPatches.CompanySeedOutcomeForThisBundle);
        Assert.Equal(AlRunner.Patches.RecordPatches.CompanyRowSeedOutcome.Inserted, seed.Run());
        Assert.Equal(new[] { "CRONUS", "CRONUS" }, seed.RowsWritten);
    }
}
