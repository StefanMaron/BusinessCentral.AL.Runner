// AppPackageIdentity — the runner's stand-in for the package GUIDs a real BC service tier
// assigns an app when it is PUBLISHED, and the single definition of them (#2963).
//
// WHY THEY HAVE TO EXIST AT ALL
//   System Application code decides whether a module may act on a table by comparing the two
//   sides of a publish record. `Reten. Pol. Allowed Tbl. Impl.ModuleOwnsTable`:
//
//       PublishedApplication.SetRange("ID", CallerModuleInfo.Id);            // + version parts
//       if not PublishedApplication.FindFirst() then exit(false);
//       if AllObj."App Runtime Package ID" <> PublishedApplication."Runtime Package ID" then
//           exit(false);
//       exit(true);
//
//   Both sides of that comparison were unanswered in the runner: the Published Application
//   table (2000000206) had no rows, and every AllObj row carried the type default for its
//   package columns. So the FindFirst failed and every ownership check declined.
//
// WHY NOT JUST LEAVE THEM BOTH EMPTY
//   Seeding Published Application with Guid.Empty package ids would make the comparison above
//   succeed for EVERY app/table pair, because AllObj's columns are Guid.Empty too — so any
//   app would "own" every table in the system. That is a silent wrong answer of exactly the
//   kind .claude/rules/loud-failures.md exists to prevent: the check would pass, and pass for
//   the wrong reason, on a question about ownership. The ids therefore have to DISCRIMINATE
//   between apps even though the runner has no publish step to get real ones from.
//
// ONE GUID PER APP, NOT TWO — MEASURED, NOT REASONED (#3066)
//   This file used to derive "Package ID" and "Runtime Package ID" from two different salts,
//   so they were never equal for one app. The stated reason was that real BC assigns them
//   independently and that equality would let a comparison of one column against the other
//   silently succeed.
//
//   A real service tier says otherwise. BusinessCentral.AL.Language.Tests#187 put the question
//   to all eight OnPrem legs, BC 27.0 through 28.4:
//
//       Test Published App Sys Table.PublishedApplication_ThisApp_PackageIdIsItsRuntimePackageId
//
//   Every leg reported the two columns EQUAL for a freshly published app. The first revision of
//   that test asserted they differ — the runner's belief — and it was the one assertion of
//   eighteen that failed upstream.
//
//   So the two-salt design was not a safety measure, it was a divergence that answered "no" to
//   a cross-column comparison a real tier answers "yes" to. What actually discriminates is the
//   value differing BETWEEN apps, which is unchanged and is pinned upstream by
//   PublishedApplication_TwoApps_DoNotShareEitherPackageId. One derived GUID per app therefore
//   keeps every property the ownership check needs AND matches what BC reports.
//
//   What this deliberately does NOT claim: that a real tier keeps them equal forever. An app
//   republished over an earlier version can carry a runtime package id minted by the later
//   publish while its package id still comes from the compile. The runner has no republish, so
//   "freshly published" is the only state it can be in, and that is the state upstream measured.
//
// WHAT THESE ARE
//   A deterministic function of the app id, so:
//     * two different apps always get two different package ids;
//     * the same app gets the same id in every process, so an on-disk install baseline
//       captured by one run still matches AllObj rows rebuilt by the next;
//     * the value is stable across runs, which a random GUID per process would not be.
//   They are NOT the ids a service tier would assign — nothing in-process can know those —
//   and no AL code may treat them as such. Their only contract is the one above: equal for
//   an app and its own objects, different for anything else.
using System.Security.Cryptography;
using System.Text;

namespace AlRunner.Infrastructure;

internal static class AppPackageIdentity
{
    // ONE salt. Both accessors derive from it, so an app's two package columns carry the same
    // GUID — see the "#3066" section of the header for the measurement that settled that.
    // Keeping the historical runtime-package salt string means runtime package ids, which are
    // the primary key of the Published Application rows the runner seeds, do not move.
    private const string PackageSalt = "al-runner/runtime-package-id/v1";

    /// <summary>The value the runner uses for this app's <c>Runtime Package ID</c>, on BOTH
    /// the Published Application row and every AllObj row for an object the app owns.</summary>
    internal static Guid RuntimePackageIdFor(Guid appId) => Derive(appId, PackageSalt);

    /// <summary>The value the runner uses for this app's <c>Package ID</c>. The SAME GUID as
    /// <see cref="RuntimePackageIdFor"/>, which is what a real tier reports for a freshly
    /// published app. Kept as its own accessor so each call site still names the column it is
    /// filling, and so a future BC that separates them again is a one-line change here.</summary>
    internal static Guid PackageIdFor(Guid appId) => Derive(appId, PackageSalt);

    private static Guid Derive(Guid appId, string salt)
    {
        // Guid.Empty in means "no owning app known"; answering with a derived id would claim
        // ownership on behalf of an app that was never identified.
        if (appId == Guid.Empty) return Guid.Empty;

        var input = new byte[16 + Encoding.UTF8.GetByteCount(salt)];
        appId.TryWriteBytes(input);
        Encoding.UTF8.GetBytes(salt, 0, salt.Length, input, 16);
        var hash = SHA256.HashData(input);
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}
