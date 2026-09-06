// RecordPatches.DependencyAppSymbolWalk — the ONE walk over the registered dependency
// .app paths, and the one place the "vanished vs unreadable" split is spelled.
//
// THE DEFECT THIS CLOSES (#3143)
//   Ten call sites each wrote their own copy of
//
//       foreach (var appPath in _bcAppPaths.ToArray())
//       {
//           try { ... = BcAppSymbolCache.Get(appPath). ...; }
//           catch (Exception ex) { Console.Error.WriteLine($"[RecordPatches] ..."); continue; }
//           ...
//       }
//
//   and every one of them turned "the runner could not find out what this .app declares"
//   into "this .app declares nothing" — a WRONG answer rather than a missing one, which is
//   exactly what .claude/rules/loud-failures.md forbids. The `continue` dropped the app's
//   objects out of AllObj, its codeunits out of CodeUnit Metadata, its tables out of Table
//   Metadata, its pages out of Page Metadata AND out of every dependency-page property
//   lookup, its reports out of Report Metadata and out of the synthesized report dataset,
//   its profiles out of All Profile, and its queries out of the query symbol index. AL then
//   read a table that was short some rows, or got `false` / `0` / `null` from a property
//   lookup, and a test could go green having quietly done without.
//
//   The only trace was a `[RecordPatches]`-tagged stderr line, and Log's default-verbosity
//   filter drops lines that START with a bracketed component tag — measured in #3031 by
//   driving the real filter, not by reading its regex. At the verbosity users actually run
//   at, the loss was completely silent.
//
// THE SPLIT (#2712, applied to the permission slice by #3031, and to every remaining
// dependency-symbol read here)
//
//   * VANISHED (`!File.Exists`) — legitimate and expected. A --watch iteration removed a
//     dependency between runs, a --server process outlived a rebuild, a fixture's temp dir
//     was deleted. Skip the .app as a WHOLE and say so on `[warn]`, a tag Log's
//     default-verbosity filter exempts, so the user is actually told.
//
//   * PRESENT BUT UNREADABLE — never legitimate. Every path in _bcAppPaths already passed
//     AddBcAppPath's eager read (#2712), so a failure here means the bytes changed into
//     something unparseable since, or the parser is defective. Both are runner defects, and
//     both would otherwise be reported as ordinary-looking test results. Raise
//     BcAppSymbolReadException naming the .app and the surface; Program.cs catches it and
//     exits 1.
//
//   The check is a File.Exists precondition rather than an exception filter on purpose: a
//   deleted file surfaces as FileNotFoundException, DirectoryNotFoundException or
//   IOException depending on platform and timing, so filtering on type would classify the
//   expected state by accident. The narrow TOCTOU window (deleted between the check and the
//   read) fails loudly, which is the conservative direction.
//
// WHY BcAppSymbolReadException AND NOT RunnerOutOfScopeException
//   RunnerOutOfScopeException means "this BC surface is one the runner does not support",
//   and an expectations entry can legitimately classify it (`expect-oos`). An unreadable
//   dependency package is neither: it is a defect that must stop the run outright, and
//   Program.cs already has the exit-1 arm for it. Classifying it as out-of-scope would let a
//   corrupt dependency be declared expected. BuildObjectOwnerIndex (#3117/#3133) reaches for
//   AllObjShapeGap instead — that one is deliberate, because the owner is a STORED COLUMN
//   VALUE on rows AllObj has already begun writing, so its refusal has to name the AllObj
//   shape the caller was asking about. Both are loud; neither returns a default.
//
// WHY ONE HELPER AND NOT TEN EDITS
//   The ten sites are one shape repeated ten times, not ten bugs. A helper makes the split
//   impossible to get subtly different per table (#3031 and #2712 had already written it
//   twice, with two different warning texts), and DependencyAppSymbolWalkSourceGuardTests
//   holds the property that no NEW copy of the old shape can be added without failing.

using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Every registered dependency .app's parsed symbols, in registration order, paired with
    /// the path they came from.
    ///
    /// <para>A .app that has VANISHED from disk since registration is skipped with a
    /// <c>[warn]</c>; a .app that is present but whose SymbolReference.json cannot be read
    /// raises <see cref="BcAppSymbolReadException"/> naming the .app and
    /// <paramref name="surface"/>. See this file's header for why those two are not the same
    /// condition.</para>
    ///
    /// <para>This is a lazy iterator, so the refusal surfaces where the caller ENUMERATES,
    /// not where it calls. That matters for a caller that assigns the sequence and drains it
    /// later: wrapping the call in a try/catch catches nothing. Every caller in this
    /// repository drains inline.</para>
    /// </summary>
    /// <param name="surface">
    /// What was being read, phrased to complete both "could not read its {surface} from
    /// SymbolReference.json" and "its {surface} are not available to this run" — e.g.
    /// <c>"objects (AllObj)"</c>.
    /// </param>
    internal static IEnumerable<(string AppPath, BcAppSymbolCache.AppSymbols Symbols)>
        EnumerateRegisteredBcAppSymbols(string surface)
    {
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            if (!File.Exists(appPath))
            {
                // No leading `[Component]` tag: Log's default-verbosity filter drops those,
                // and a skip the user is never told about is the same silent loss the
                // refusal below exists to prevent.
                Console.Error.WriteLine(
                    "[warn] registered dependency .app is no longer on disk; its "
                    + $"{surface} are not available to this run: {appPath}");
                continue;
            }

            BcAppSymbolCache.AppSymbols symbols;
            try
            {
                symbols = BcAppSymbolCache.Get(appPath);
            }
            catch (Exception ex) when (ex is not BcAppSymbolReadException)
            {
                throw new BcAppSymbolReadException(appPath, surface, ex);
            }

            yield return (appPath, symbols);
        }
    }
}
