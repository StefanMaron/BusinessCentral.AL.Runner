// AppPackageIdentity — the runner's stand-in for the two package GUIDs a real BC service
// tier assigns an app when it is PUBLISHED, and the single definition of them (#2963).
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
// WHAT THESE ARE
//   A deterministic function of the app id, so:
//     * two different apps always get two different package ids;
//     * the same app gets the same ids in every process, so an on-disk install baseline
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
    // Two different salts so "Package ID" and "Runtime Package ID" are never the same GUID
    // for the same app. Real BC assigns them independently; making them equal here would let
    // a comparison of one column against the other silently succeed.
    private const string RuntimePackageSalt = "al-runner/runtime-package-id/v1";
    private const string PackageSalt = "al-runner/package-id/v1";

    /// <summary>The value the runner uses for this app's <c>Runtime Package ID</c>, on BOTH
    /// the Published Application row and every AllObj row for an object the app owns.</summary>
    internal static Guid RuntimePackageIdFor(Guid appId) => Derive(appId, RuntimePackageSalt);

    /// <summary>The value the runner uses for this app's <c>Package ID</c>, on both sides in
    /// the same way. Distinct from the runtime package id by construction.</summary>
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
