// RecordPatches.TddReparse — issue #2001: --tdd generates a field directly into a
// SOURCE-COMPILED table's SyntaxTree entirely IN-MEMORY, inside BcCompiler.Emit. That happens
// strictly AFTER RecordPatches.AddSourceDirs has already parsed the on-disk file and — via
// PopulateNclMetadataCache — already built and cached a NCLMetaTable for that table id WITHOUT
// the generated field (Program.cs's "register-source-dirs" stage runs before the compile loop
// that eventually calls BcCompiler.Emit). Without this file, the generated field compiles fine
// but the runtime record engine still throws NavNCLFieldNotFoundException on first access,
// because it reads a frozen NCLMetaTable built before the field existed.
//
// This mirrors EvictCachedMetaTableForBaseTable (RecordPatches.AlSourceParser.cs) — a
// tableextension arriving after its base table was cached already evicts _metaTableCache so
// the next lookup rebuilds. The tableextension case never needs to touch the skeleton
// NCLMetadata's OWN cache dictionary because PopulateNclMetadataCache for the whole batch
// hasn't run yet at that point in Register()'s flow. --tdd's generation runs LATER — after
// that populate pass already completed — so a stale entry sits in BOTH places, and both need
// evicting before a repopulate pass will pick up the fresh table shape.
using System.Reflection;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Re-parses <paramref name="tableObjectText"/> (a complete <c>table N "Name" { ... }</c>
    /// declaration — the exact text of the mutated ObjectSyntax TddGeneration just built, not
    /// the original on-disk file) into <c>_parsedTables</c>, then evicts every cache layer
    /// that could otherwise still serve the pre-generation NCLMetaTable: the
    /// <c>_metaTableCache</c> build cache AND the skeleton NCLMetadata's own
    /// <c>metadataCacheEntries[Table]</c> dictionary (already populated for this id by the
    /// time --tdd generation runs — see this file's header). <see cref="PopulateNclMetadataCache"/>
    /// then rebuilds and reinserts a fresh entry for exactly this id; every other id's cache
    /// entry is untouched (<c>PopulateOneObjectType</c>'s own "skip if already present" check).
    /// </summary>
    internal static void TddReparseAndRefreshTable(int tableId, string tableObjectText)
    {
        TryParseTableFile(tableObjectText);
        _metaTableCache.TryRemove(tableId, out _);

        var skeleton = BcRuntime.SkeletonNCLMetadata;
        if (skeleton != null)
        {
            EnsureCachePopulatorReflection();
            if (_fNCLMetadataCacheEntries != null)
            {
                try
                {
                    var arr = _fNCLMetadataCacheEntries.GetValue(skeleton) as Array;
                    const int objectTypeTable = 1;
                    if (arr != null && arr.Length > objectTypeTable
                        && arr.GetValue(objectTypeTable) is System.Collections.IDictionary dict)
                    {
                        dict.Remove(tableId);
                    }
                }
                catch
                {
                    // Best-effort eviction: worst case the field stays FieldNotFoundException
                    // (loud, per loud-failures.md) rather than silently serving stale metadata.
                }
            }
        }

        PopulateNclMetadataCache();
    }
}
