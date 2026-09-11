// MissingCodeunitMessageTests — #3399.
//
// BuildMissingCodeunitMessage is the diagnostic a user sees when a codeunit reference
// resolved at compile time against a Microsoft .app but no runtime DLL for that app is
// loaded. It told them two things that are false on v2: that "AL Runner does not yet load
// Microsoft R2R packages at runtime" (loading them IS the architecture — see
// tests/runner-extras/microsoft-test-library) and to "provide an AL implementation/stub for
// this codeunit at the bucket level" (there is no bucket layout; that was v1). Both halves
// direct the reader away from the only fix that works — pointing --package-cache at the
// directory holding the app — which is why the message is the defect and not just prose.
//
// The id table the message reads from was measured against the shipped packages rather than
// trusted; see MissingCodeunitDependencyTableTests for the ids and the instrument.

using System;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class MissingCodeunitMessageTests
{
    // 130002 is in the table (verified: Microsoft_Library Assert.app); 47111 is not, so the
    // two arms of BuildMissingCodeunitMessage are both covered.
    public static TheoryData<int> BothArms => new() { 130002, 47111 };

    [Theory]
    [MemberData(nameof(BothArms))]
    public void Message_NamesPackageCacheAsTheRemedy(int id)
    {
        var msg = BcRuntime.BuildMissingCodeunitMessageForTests(id);

        // The actionable remedy: the flag a user actually passes, and the fact that it
        // takes a directory of .app packages. Asserting the flag alone would pass on a
        // message that merely mentioned it in passing.
        Assert.Contains("--package-cache", msg);
        Assert.Contains(".app", msg);

        // The one-command alternative, which ProvisioningCheck already advertises for the
        // sibling gap. A user with no cache at all cannot act on --package-cache alone.
        Assert.Contains("al-runner provision", msg);
    }

    [Theory]
    [MemberData(nameof(BothArms))]
    public void Message_DoesNotRepeatTheV1Falsehoods(int id)
    {
        var msg = BcRuntime.BuildMissingCodeunitMessageForTests(id);

        // "does not yet load Microsoft R2R packages at runtime" — false; that is the
        // architecture. Matching on the claim rather than on the word "R2R", so the message
        // stays free to say truthfully which package failed to load.
        Assert.DoesNotContain("does not yet load", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wait until runtime dependency loading lands", msg, StringComparison.OrdinalIgnoreCase);

        // "at the bucket level" — there is no bucket layout on v2.
        Assert.DoesNotContain("bucket", msg, StringComparison.OrdinalIgnoreCase);

        // Telling the user to hand-write the codeunit is the remedy loud-failures.md and
        // docs/limitations.md § "Why no real SA implementations" both rule out for a
        // Microsoft dependency app.
        Assert.DoesNotContain("implementation/stub", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KnownId_NamesTheCodeunitAndItsRealPackage()
    {
        // 130002 "Library Assert" ships in Microsoft_Library Assert.app — measured, not
        // assumed. The message must carry both so the reader knows which .app to find.
        var msg = BcRuntime.BuildMissingCodeunitMessageForTests(130002);

        Assert.Contains("130002", msg);
        Assert.Contains("Library Assert", msg);
    }

    [Fact]
    public void UnknownId_StillNamesTheIdAndTheRemedy()
    {
        var msg = BcRuntime.BuildMissingCodeunitMessageForTests(47111);

        Assert.Contains("47111", msg);
        // No package name is knowable for an unlisted id, so the remedy has to carry the
        // whole message's weight.
        Assert.Contains("--package-cache", msg);
    }
}
