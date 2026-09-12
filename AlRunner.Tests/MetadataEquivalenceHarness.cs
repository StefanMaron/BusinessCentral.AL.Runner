// MetadataEquivalenceHarness — drives the total comparison of BC's own metadata emitter
// against the runner's SymbolReference-derived metadata, for one ground-truth bundle.
//
// Issue #3533. The standard: the derivation is acceptable only if it builds the SAME objects
// BC's emitter builds, member for member, with every difference declared.
//
// The two sides, and why they are the same TYPE
// ---------------------------------------------
//   BC's:  MetaTable.CreateMetaTableFromXml(<the emitter's own document>)
//   ours:  RecordPatches.GetOrBuildNCLMetaTable(id).GetMetaTableOriginal()
//
// The runner builds a Types.Metadata.MetaTable and hands it to NCLMetaTable.CreateFromMetaTable;
// GetMetaTableOriginal() hands that same MetaTable back. So both sides are
// Microsoft.Dynamics.Nav.Types.Metadata.MetaTable and the comparison is member-for-member on
// one type, not a translation between two shapes.
//
// Why the bundle is not checked in: see docs/metadata-equivalence.md § "Where the ground
// truth lives".

using System.Reflection;
using Xunit;
using System.Text.Json;
using System.Xml;
using AlRunner.Metadata;
using AlRunner.Patches;

namespace AlRunner.Tests;

/// <summary>One object in a ground-truth bundle, as its manifest describes it.</summary>
internal sealed record GroundTruthObject(string Kind, int Id, string Name, string File, string Sha256);

internal sealed record GroundTruthBundle(
    string Directory,
    int Schema,
    Guid AppId,
    string AppName,
    string AppPublisher,
    string AppVersion,
    string BcBuild,
    IReadOnlyDictionary<string, int> Census,
    IReadOnlyList<GroundTruthObject> Objects)
{
    public string Label => $"{AppPublisher}_{AppName} {AppVersion}";
}

/// <summary>What one bundle's comparison found, including what it did NOT cover.</summary>
internal sealed record MetadataEquivalenceReport(
    GroundTruthBundle Bundle,
    IReadOnlyList<string> KindsCompared,
    IReadOnlyList<string> KindsNotCompared,
    int ObjectsCompared,
    IReadOnlyList<string> Unbuildable,
    IReadOnlyList<MetadataDifference> Differences,
    // Enum-rooted documents that are an enumEXTENSION's rather than a base enum's, and so have
    // no runner object addressable by their own id. Reported rather than dropped: an object
    // silently missing from the denominator is the one way a shrinking comparison stays green
    // (#3782 step 7; the derivation gap is #3807).
    IReadOnlyList<string> EnumExtensionDocuments)
{
    public string Summary =>
        $"{Bundle.Label} (BC {Bundle.BcBuild}): compared {ObjectsCompared} object(s) of kind(s) " +
        $"[{string.Join(", ", KindsCompared)}]; NOT compared: [{string.Join(", ", KindsNotCompared)}]; " +
        $"{EnumExtensionDocuments.Count} enum-extension document(s) skipped; " +
        $"{Differences.Count} difference(s) across {Differences.Select(d => d.Signature).Distinct().Count()} member(s)";
}

internal static class MetadataEquivalencePaths
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    public static string ExpectationsDir()
        => Path.Combine(RepoRoot, "tests", "expectations", "metadata-equivalence");

    public static string AllowlistFile() => Path.Combine(ExpectationsDir(), "allowlist.json");

    public static string AppsFile() => Path.Combine(ExpectationsDir(), "apps.json");

    /// <summary>
    /// Where the generator writes bundles. A sibling of the artifacts root, because a bundle
    /// belongs to the BC build it was generated from and is regenerated when that moves.
    /// </summary>
    public static string GroundTruthRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("AL_RUNNER_METADATA_GROUND_TRUTH");
        if (!string.IsNullOrEmpty(explicitRoot)) return explicitRoot;
        var artifacts = AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir;
        return Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(artifacts))
                            ?? artifacts, "metadata-ground-truth");
    }

    /// <summary>
    /// Bundles for the BC build this process actually loaded — never the whole root.
    ///
    /// A dev box accumulates one directory per build it has generated, and every one of them
    /// holds a System Application bundle with the same table ids. Registering two of those
    /// packages into RecordPatches' process-global state makes the second table id lose to the
    /// first, and the comparison then measures one app's metadata against another app's
    /// symbols — a wrong answer, arrived at silently, of exactly the kind this harness exists
    /// to catch. CI has one build so it would never have shown up there.
    /// </summary>
    public static string GroundTruthDirForThisBuild()
        => Path.Combine(GroundTruthRoot(),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(
                AlRunner.Infrastructure.BcArtifacts.ServiceTierDir)));
}

/// <summary>
/// The one place "there is no ground-truth bundle" is turned into a verdict (#3789).
///
/// <para>Three answers, not two, per <c>.claude/rules/guards-need-a-third-state.md</c>: bundles
/// exist, no bundle exists on a dev box (a legitimate skip — the generator is a provisioning
/// step nobody has run), and no bundle exists ON CI, where the generator runs before
/// <c>dotnet test</c> by construction. The third is a workflow regression and must FAIL.</para>
///
/// <para>Why a shared helper rather than the branch copied per class. The classes that read a
/// bundle exist to stop the harness reporting green over an unrun measurement, and a skip reads
/// green in the summary line — so a class missing this branch is the anti-green-over-nothing
/// guard without the guard. That is what #3789 found in
/// <c>MetadataEquivalencePageOracleTests</c>, and #3782 has five more object kinds to go, each
/// of which would want the same discrimination and could reintroduce the gap by writing a bare
/// <c>Skip.If</c>. <c>MetadataEquivalenceBundleGateTests</c> holds every reader to this
/// helper.</para>
/// </summary>
internal static class MetadataEquivalenceBundleGate
{
    /// <summary>
    /// The bundles for this BC build, or the right verdict when there are none. Callers must
    /// use this rather than calling <see cref="MetadataEquivalenceHarness.LoadBundles"/> and
    /// writing their own <c>Skip.If</c>.
    /// </summary>
    public static IReadOnlyList<GroundTruthBundle> RequireBundles()
    {
        var root = MetadataEquivalencePaths.GroundTruthDirForThisBuild();
        var bundles = MetadataEquivalenceHarness.LoadBundles(root);
        if (bundles.Count > 0) return bundles;

        // Names the exact --artifacts to pass. A dev box holds several BC builds and the
        // generator's own default is the NEWEST one, while this process loaded whichever build
        // the runner was compiled against — so "just run the generator" is not actionable on its
        // own and has already cost one round trip.
        var reason =
            $"no metadata ground-truth bundle under '{root}'. This test process loaded BC " +
            $"from '{AlRunner.Infrastructure.BcArtifacts.ServiceTierDir}', so generate for " +
            $"that build:{Environment.NewLine}" +
            $"  tools/gen-metadata-ground-truth.sh --artifacts " +
            $"\"{AlRunner.Infrastructure.BcArtifacts.ServiceTierDir}\"{Environment.NewLine}" +
            "Business Foundation is ~3s and System Application ~14s. " +
            "AL_RUNNER_METADATA_GROUND_TRUTH overrides where bundles are read from.";

        if (TestArtifacts.RunningOnCi)
            throw new MetadataGroundTruthMissingOnCiException(
                "On a CI leg the ground truth is generated before `dotnet test` (see the " +
                "'Generate BC metadata ground truth' step in .github/workflows/bc-tests.yml), " +
                "so its absence is a workflow regression, not a legitimate skip. Skipping here " +
                "would leave the metadata-equivalence gate reporting green while measuring " +
                "nothing. " + reason);

        throw new SkipException(reason);
    }
}

/// <summary>
/// Thrown instead of skipping when CI has no ground-truth bundle. A distinct type so
/// <c>MetadataEquivalenceBundleGateTests</c> can assert the CI branch fires without needing the
/// bundle directory to be absent for real.
/// </summary>
internal sealed class MetadataGroundTruthMissingOnCiException : Exception
{
    public MetadataGroundTruthMissingOnCiException(string message) : base(message) { }
}

internal static class MetadataEquivalenceHarness
{
    /// <summary>Bundle schema this harness understands. See tools/metadata-ground-truth.</summary>
    private const int SupportedSchema = 1;

    /// <summary>
    /// The object kinds this harness compares today. Everything else in a bundle is reported
    /// as NOT compared rather than dropped — covering less has to be visible, or the harness
    /// becomes the silent-partial-answer it was built to prevent.
    ///
    /// #3782 is the programme that empties the NOT-compared list, one kind per pull request.
    /// The order and remaining kinds are on that issue.
    /// </summary>
    internal static readonly string[] ComparedKinds =
        { "MetaTable", "PageDefinition", "CodeUnit", "Query", "XmlPort", "Report",
          "PermissionSet", "Enum", "MetadataRuntimeDeltas" };

    public static IReadOnlyList<GroundTruthBundle> LoadBundles(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<GroundTruthBundle>();
        var bundles = new List<GroundTruthBundle>();
        foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
            bundles.Add(ReadManifest(manifestPath));
        return bundles;
    }

    private static GroundTruthBundle ReadManifest(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        var schema = root.GetProperty("schema").GetInt32();
        if (schema != SupportedSchema)
            throw new InvalidDataException(
                $"{manifestPath}: bundle schema {schema}, this harness understands {SupportedSchema}. " +
                "Regenerate with tools/metadata-ground-truth rather than reading fields that may have moved.");

        var app = root.GetProperty("app");
        var census = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in root.GetProperty("census").EnumerateObject())
            census[kv.Name] = kv.Value.GetInt32();

        var objects = root.GetProperty("objects").EnumerateArray()
            .Select(o => new GroundTruthObject(
                o.GetProperty("Kind").GetString()!,
                o.GetProperty("Id").GetInt32(),
                o.GetProperty("Name").GetString()!,
                o.GetProperty("File").GetString()!,
                o.GetProperty("Sha256").GetString()!))
            .ToArray();

        return new GroundTruthBundle(
            Path.GetDirectoryName(manifestPath)!, schema,
            app.GetProperty("Id").GetGuid(), app.GetProperty("Name").GetString()!,
            app.GetProperty("Publisher").GetString()!, app.GetProperty("Version").GetString()!,
            root.GetProperty("bcBuild").GetString()!, census, objects);
    }

    /// <summary>
    /// Compare one bundle. <paramref name="appPackagePath"/> is the very .app the bundle was
    /// generated from — the runner reads its SymbolReference.json, so a different build of the
    /// same app would compare two things that were never meant to agree.
    /// </summary>
    public static MetadataEquivalenceReport Compare(GroundTruthBundle bundle, string appPackagePath)
    {
        RecordPatches.AddBcAppPath(appPackagePath);

        var getOriginal = Type.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable, Microsoft.Dynamics.Nav.Ncl")
            ?.GetMethod("GetMetaTableOriginal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "NCLMetaTable.GetMetaTableOriginal is not reachable. Without it there is no way to " +
                "read back the MetaTable the runner built, and the comparison would silently " +
                "become NCLMetaTable-vs-MetaTable — two different types.");

        var fromXml = Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaTable, Microsoft.Dynamics.Nav.Types")
            ?.GetMethod("CreateMetaTableFromXml", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("MetaTable.CreateMetaTableFromXml is not reachable.");

        // BC's own reader for a PageDefinition document: a CONSTRUCTOR, not a factory — pages
        // have no CreatePageDefinitionFromXml the way MetaTable has CreateMetaTableFromXml.
        //
        // PageDefinition, NOT MetaPageDefinition, and the difference is not cosmetic. Both
        // types expose a public (XmlNode) constructor and both accept the document without
        // throwing, but MetaPageDefinition's IGNORES it: measured on BC 28.1.49838.53910
        // against the emitter's own Page 257 "Source Codes", it answers ID=0, Name=null,
        // Properties=null, Content=null, while PageDefinition answers ID=257,
        // Name="Source Codes" with both Properties and Content populated. Passing that empty
        // object in as BOTH sides is a comparison of nothing against nothing: 235 pages
        // compared, 0 differences, every other test in this file still green. See
        // MetadataEquivalencePageOracleTests, which pins exactly that asymmetry so this cannot
        // be silently switched back.
        var pageType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.PageDefinition, Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("PageDefinition is not reachable.");
        var pageFromXml = pageType.GetConstructor(new[] { typeof(XmlNode) })
            ?? throw new InvalidOperationException(
                "PageDefinition has no (XmlNode) constructor. Without it there is no way to " +
                "read BC's emitted PageDefinition document back into BC's own object model, and " +
                "the comparison would become a hand-written XML walk against the runner — two " +
                "derivations, no oracle.");

        // BC's own reader for a CodeUnit document: a CONSTRUCTOR on a STRUCT, not a factory —
        // codeunits have no CreateMetaCodeunitFromXml the way MetaTable has
        // CreateMetaTableFromXml.
        //
        // Proven to parse rather than assumed to: measured on BC 28.1.49838.53910 against the
        // emitter's own CodeUnit 26 "Confirm Management Impl.", this constructor answers
        // Id=26, Name="Confirm Management Impl.", ALNamespace="System.Utilities",
        // InherentPermissions=Execute and SubType=Normal. Step 1 of #3782 lost a full cycle to
        // MetaPageDefinition, which also constructs without throwing and returns a DEFAULT
        // object, so the comparison ran green over 235 pages having compared nothing. The
        // discrimination is pinned in MetadataEquivalenceCodeunitOracleTests.
        var codeunitType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaCodeunit, Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("MetaCodeunit is not reachable.");
        var codeunitFromXml = codeunitType.GetConstructor(new[] { typeof(XmlNode) })
            ?? throw new InvalidOperationException(
                "MetaCodeunit has no (XmlNode) constructor. Without it there is no way to read " +
                "BC's emitted CodeUnit document back into BC's own object model, and the " +
                "comparison would become a hand-written XML walk against the runner — two " +
                "derivations, no oracle.");

        // BC's own reader for a Query document: a CONSTRUCTOR taking the two app-group ids,
        // MetaQuery(XmlNode, int metadataAppGroupId, int languageAppGroupId).
        //
        // Proven to parse rather than assumed to, per step 1's finding. Measured on BC
        // 28.1.49838.53910 against the emitter's own Query 774 "Users in Plans", it answers
        // Id=774, Name="Users in Plans", QueryType=Normal, InherentPermissions=Execute and
        // DataItems[2]. MetadataEquivalenceQueryXmlPortOracleTests pins that it reads the
        // document AND that it discriminates between two different ones.
        var queryType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaQuery, Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("MetaQuery is not reachable.");
        var queryFromXml = queryType.GetConstructor(new[] { typeof(XmlNode), typeof(int), typeof(int) })
            ?? throw new InvalidOperationException(
                "MetaQuery has no (XmlNode, int, int) constructor. Without it there is no way to " +
                "read BC's emitted Query document back into BC's own object model, and the " +
                "comparison would become a hand-written XML walk against the runner — two " +
                "derivations, no oracle.");

        // BC's own reader for an XmlPort document takes an XmlDocument, not an XmlNode, and two
        // trailing DELEGATES which are both optional: MetaXmlPort(XmlDocument, CreateRequestForm,
        // int, int, RemoveItemsOnPageBasedOnLicenseAndApplicationArea). Passing null for both is
        // what the ctor's own body expects — it guards the only use with
        // `if (createRequestForm != null && val != null)`, so a null simply leaves
        // RequestFormMetadata unbuilt on BOTH sides and cannot skew the comparison.
        //
        // This one cannot silently ignore a document the way MetaPageDefinition did: its body is
        // a switch over the uppercased child element name that THROWS ArgumentException on any
        // name it does not know. Measured on BC 28.1.49838.53910 against XmlPort 9001, it
        // answers Id=9001, Name="Export/Import Security Groups", Direction=Both, Encoding=UTF16
        // and Nodes[9].
        var xmlPortType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaXmlPort, Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("MetaXmlPort is not reachable.");
        var xmlPortFromXml = xmlPortType.GetConstructors()
                .FirstOrDefault(c => c.GetParameters() is { Length: 5 } ps
                                     && ps[0].ParameterType == typeof(XmlDocument))
            ?? throw new InvalidOperationException(
                "MetaXmlPort has no (XmlDocument, …) constructor. Without it there is no way to " +
                "read BC's emitted XmlPort document back into BC's own object model, and the " +
                "comparison would become a hand-written XML walk against the runner — two " +
                "derivations, no oracle.");

        // BC's own reader for a Report document: MetaReport(XmlElement, CreateRequestForm, int,
        // int, RemoveItemsOnPageBasedOnLicenseAndApplicationArea). Both delegates are optional
        // and null is what the ctor's own body expects.
        //
        // Proven to parse rather than assumed to, per step 1's finding: measured on BC
        // 28.1.49838.53910 against the emitter's own Report 9810 "Change Password", it answers
        // Id=9810, Name="Change Password", ProcessingOnly=True, DefaultLayout=RDLC,
        // TransactionType=UpdateNoLocks and ALNamespace="System.Security.AccessControl".
        // MetadataEquivalenceReportEnumPermissionSetOracleTests pins that it reads the document
        // AND that it discriminates between two different ones.
        var reportType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaReport, Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("MetaReport is not reachable.");
        var reportFromXml = reportType.GetConstructors()
                .FirstOrDefault(c => c.GetParameters() is { Length: 5 } ps
                                     && ps[0].ParameterType == typeof(XmlElement))
            ?? throw new InvalidOperationException(
                "MetaReport has no (XmlElement, …) constructor. Without it there is no way to " +
                "read BC's emitted Report document back into BC's own object model, and the " +
                "comparison would become a hand-written XML walk against the runner — two " +
                "derivations, no oracle.");

        // BC's own reader for a PermissionSet document: a STATIC FACTORY taking the two
        // app-group ids, not a constructor. Measured on the same build against PermissionSet 21
        // "System Application - Read": Id=21, Name="SYSTEM APPLICATION - READ" (BC uppercases —
        // see PermissionSetDiffOptions), Assignable=False, Access=Internal and
        // IncludedPermissionSets[33].
        var permissionSetType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaPermissionSet, Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("MetaPermissionSet is not reachable.");
        var permissionSetFromXml = permissionSetType.GetMethod(
                "Create", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "MetaPermissionSet has no static Create(XmlNode, int, int). Without it there is " +
                "no way to read BC's emitted PermissionSet document back into BC's own object " +
                "model, and the comparison would become a hand-written XML walk against the " +
                "runner — two derivations, no oracle.");

        // BC's own reader for an Enum document. Measured on the same build against Enum 59
        // "Auto Format": Id=59, Name="Auto Format", Extensible=True, Values[2] and
        // ALNamespace="System.Text" — so it parses, unlike MetaPageDefinition.
        var enumType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaEnum, Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("MetaEnum is not reachable.");
        var enumFromXml = enumType.GetConstructor(new[] { typeof(XmlNode) })
            ?? throw new InvalidOperationException(
                "MetaEnum has no (XmlNode) constructor. Without it there is no way to read BC's " +
                "emitted Enum document back into BC's own object model, and the comparison would " +
                "become a hand-written XML walk against the runner — two derivations, no oracle.");

        // BC's own reader for a MetadataRuntimeDeltas document — the emitted form of a
        // tableextension, pageextension or permissionsetextension.
        //
        // IT LIVES IN A THIRD ASSEMBLY, and that is the whole reason this kind was first
        // reported as having no reader at all. Microsoft.Dynamics.Nav.Apps.dll is neither of
        // the two assemblies every other oracle here comes from, and a census over just those
        // two answers zero — which reads exactly like "BC ships no reader" and is not. The
        // artifact directory holds 501 assemblies; a metadata-only scan of all of them finds
        // 308 types whose name contains "Delta", 54 of them in this one. Never conclude a
        // negative about BC's surface from a search narrower than the thing being claimed.
        //
        // Proven to parse rather than assumed to: measured on BC 28.1.49838.53910 over all 11
        // documents in the two bundles, FromXml(XDocument) returns a populated object for every
        // one — AllDeltas of 12 for page 774, 5 for 4318, 4 for 2515, 2 for 324, 1 for 9862 and
        // 0 for the five documents that are genuinely empty elements. Pinned in
        // MetadataEquivalenceRuntimeDeltasOracleTests.
        var appsAssembly = System.Reflection.Assembly.LoadFrom(Path.Combine(
            AlRunner.Infrastructure.BcArtifacts.ServiceTierDir, "Microsoft.Dynamics.Nav.Apps.dll"));
        var deltasType = appsAssembly.GetType(
                "Microsoft.Dynamics.Nav.Apps.MetadataDeltas.NavAppObjectMetadataRuntimeDeltas")
            ?? throw new InvalidOperationException("NavAppObjectMetadataRuntimeDeltas is not reachable.");
        // The XDocument overload, not the XContainer one: an emitted document is handed over
        // whole, and picking by parameter type rather than by position keeps this working if BC
        // reorders the two.
        var deltasFromXml = deltasType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "FromXml"
                                     && m.GetParameters() is { Length: 1 } ps
                                     && ps[0].ParameterType == typeof(System.Xml.Linq.XDocument))
            ?? throw new InvalidOperationException(
                "NavAppObjectMetadataRuntimeDeltas has no static FromXml(XDocument). Without it " +
                "there is no way to read BC's emitted MetadataRuntimeDeltas document back into " +
                "BC's own object model, and the comparison would become a hand-written XML walk " +
                "against the runner — two derivations, no oracle.");

        // Built once per bundle, not per object: RunnerPermissionSetDeclarations drives the
        // runner's own population, which is what makes IncludedPermissionSets resolvable at all.
        IReadOnlyDictionary<int, BcAppSymbolCache.PermissionSetSymbol>? permissionSetsById = null;

        var differences = new List<MetadataDifference>();
        var unbuildable = new List<string>();
        var enumExtensionDocuments = new List<string>();
        int compared = 0;

        foreach (var obj in bundle.Objects.Where(o => ComparedKinds.Contains(o.Kind, StringComparer.Ordinal))
                     .OrderBy(o => o.Kind, StringComparer.Ordinal).ThenBy(o => o.Id))
        {
            var xmlPath = Path.Combine(bundle.Directory, obj.File);
            var document = new XmlDocument();
            document.Load(xmlPath);

            object? expected;
            object? actual;
            string objectKey;

            if (obj.Kind == "CodeUnit")
            {
                objectKey = $"Codeunit {obj.Id}";
                try
                {
                    expected = codeunitFromXml.Invoke(new object?[] { document.DocumentElement });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own MetaCodeunit " +
                                    $"constructor threw — {Describe(ex)}");
                    continue;
                }

                try
                {
                    // The runner's own codeunit derivation, rendered as the document BC's
                    // constructor reads — NOT the document in AlObjectMetadataRegistry, which
                    // is BC's emitter output captured at compile and would compare BC against
                    // BC. See RecordPatches.CodeunitMetadataEquivalence.cs.
                    var runnerXml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(obj.Id);
                    if (runnerXml is null)
                    {
                        unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner knows no " +
                                        "codeunit with that id");
                        continue;
                    }
                    var runnerDoc = new XmlDocument();
                    runnerDoc.LoadXml(runnerXml);
                    actual = codeunitFromXml.Invoke(new object?[] { runnerDoc.DocumentElement });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }
            else if (obj.Kind == "Query")
            {
                objectKey = $"Query {obj.Id}";
                try
                {
                    expected = queryFromXml.Invoke(new object?[] { document.DocumentElement, 0, 0 });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own MetaQuery " +
                                    $"constructor threw — {Describe(ex)}");
                    continue;
                }
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own MetaQuery " +
                                    "constructor produced null");
                    continue;
                }

                try
                {
                    // The runner's OWN MetaQuery design object, built from SymbolReference.json
                    // — the same object NCLMetaQuery.CreateDynamicQuery consumes at runtime, so
                    // this measures what AL actually gets. Not BC's captured document, which
                    // TryBuildQueryMetadataEquivalenceDesign refuses outright: that route exists
                    // for source-compiled queries (#3608) and would compare BC against BC here.
                    //
                    // No rendering step, unlike every other kind in this harness: both sides are
                    // already Types.Metadata.MetaQuery.
                    actual = RecordPatches.TryBuildQueryMetadataEquivalenceDesign(obj.Id);
                    if (actual is null)
                    {
                        unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner built no " +
                                        "MetaQuery design at all — this is #3499's null, the one " +
                                        "AL reaches as a NullReferenceException inside BC's own " +
                                        "ALSetFilter");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }
            else if (obj.Kind == "XmlPort")
            {
                objectKey = $"XmlPort {obj.Id}";
                try
                {
                    expected = xmlPortFromXml.Invoke(new object?[] { document, null, 0, 0, null });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own MetaXmlPort " +
                                    $"constructor threw — {Describe(ex)}");
                    continue;
                }
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own MetaXmlPort " +
                                    "constructor produced null");
                    continue;
                }

                try
                {
                    // The runner's derivation rendered as BC's document shape, then read back by
                    // the SAME constructor — one type against itself. NOT AlXmlPortMetadataRegistry,
                    // which holds BC's emit-captured schema and would compare BC against BC.
                    var runnerXml = RecordPatches.TryBuildXmlPortMetadataEquivalenceXml(obj.Id);
                    if (runnerXml is null)
                    {
                        unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner knows no " +
                                        "xmlport with that id");
                        continue;
                    }
                    var runnerDoc = new XmlDocument();
                    runnerDoc.LoadXml(runnerXml);
                    actual = xmlPortFromXml.Invoke(new object?[] { runnerDoc, null, 0, 0, null });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }
            else if (obj.Kind == "Report")
            {
                objectKey = $"Report {obj.Id}";
                try
                {
                    expected = reportFromXml.Invoke(new object?[] { document.DocumentElement, null, 0, 0, null });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own MetaReport " +
                                    $"constructor threw — {Describe(ex)}");
                    continue;
                }
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own MetaReport " +
                                    "constructor produced null");
                    continue;
                }

                try
                {
                    // The runner's own report-metadata document — the one every runner consumer
                    // of report metadata reads (RunnerXmlMetadataLoader hands this XML to BC).
                    // NOT AlReportMetadataRegistry, which holds BC's emit-captured output and
                    // would compare BC against BC.
                    var runnerXml = RecordPatches.TryBuildReportMetadataEquivalenceXml(obj.Id);
                    if (runnerXml is null)
                    {
                        unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': no registered " +
                                        "dependency declares this report, so the runner built no " +
                                        "report metadata at all");
                        continue;
                    }
                    var runnerDoc = new XmlDocument();
                    runnerDoc.LoadXml(runnerXml);
                    actual = reportFromXml.Invoke(new object?[] { runnerDoc.DocumentElement, null, 0, 0, null });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }
            else if (obj.Kind == "MetadataRuntimeDeltas")
            {
                objectKey = $"MetadataRuntimeDeltas {obj.Id}";
                try
                {
                    expected = deltasFromXml.Invoke(
                        null, new object?[] { System.Xml.Linq.XDocument.Load(xmlPath) });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own " +
                                    $"NavAppObjectMetadataRuntimeDeltas.FromXml threw — {Describe(ex)}");
                    continue;
                }
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own " +
                                    "NavAppObjectMetadataRuntimeDeltas.FromXml produced null");
                    continue;
                }

                try
                {
                    // The runner's extension-delta answer for this object, rendered from its own
                    // extension declarations and read back through BC's own FromXml, so both
                    // sides of this comparison are one type (#3809).
                    //
                    // WHY BOTH OBJECT TYPES ARE TRIED. The runner keys an extension on (object
                    // type, id), as BC does — but a bundle entry records only the id and the
                    // name, and the emitted document states neither an object type nor anything
                    // that implies one. For 10 of the 11 documents in the 28.1 bundles exactly
                    // one type answers, so the pair is unambiguous. The eleventh is the 774
                    // collision — a tableextension and a pageextension, both "Plan User Details"
                    // — where this picks the PAGE extension, because that is the one carrying
                    // deltas; the tableextension's document is empty and its counterpart here is
                    // an empty render, so the two are interchangeable for the comparison.
                    //
                    // That ambiguity is a gap in the ground-truth bundle rather than in the
                    // runner: the generator knows each document's SymbolKind and does not record
                    // it. Recording it is tracked separately; until then this is a statement
                    // about what the bundle can express, not a guess about BC.
                    var deltasXml =
                        RecordPatches.TryGetRuntimeDeltasMetadataEquivalence("Page", obj.Id)
                        ?? RecordPatches.TryGetRuntimeDeltasMetadataEquivalence("Table", obj.Id);

                    if (deltasXml is null)
                    {
                        unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': no registered " +
                                        "dependency declares a table- or pageextension with that " +
                                        "id, so the runner built no deltas document at all");
                        continue;
                    }

                    actual = deltasFromXml.Invoke(
                        null, new object?[] { System.Xml.Linq.XDocument.Parse(deltasXml) });
                    if (actual is null)
                    {
                        unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own FromXml " +
                                        "produced null for the runner's rendered document");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }
            else if (obj.Kind == "PermissionSet")
            {
                objectKey = $"PermissionSet {obj.Id}";
                try
                {
                    expected = permissionSetFromXml.Invoke(null, new object?[] { document.DocumentElement, 0, 0 });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own " +
                                    $"MetaPermissionSet.Create threw — {Describe(ex)}");
                    continue;
                }
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own " +
                                    "MetaPermissionSet.Create produced null");
                    continue;
                }

                permissionSetsById ??= RecordPatches.RunnerPermissionSetDeclarations();
                if (!permissionSetsById.TryGetValue(obj.Id, out var declaration))
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner has no " +
                                    "permission-set declaration with that id");
                    continue;
                }

                try
                {
                    // The runner's own MetaPermissionSet — the very object BC's
                    // AssignFromMetaPermissionSet consumes at runtime, so both sides are
                    // Types.Metadata.MetaPermissionSet and no rendering step is involved.
                    actual = RecordPatches.BuildPermissionSetMetadataEquivalenceObject(declaration);
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }
            else if (obj.Kind == "Enum")
            {
                // One <Enum> root covers both Enum and EnumExtension — the generator's own
                // ClassifyDocument says so, and it is deliberate. The two are told apart by
                // SHAPE: a base enum wraps its values in <Values>, an extension states bare
                // <Value> children. Measured on BC 28.1.49838.53910, exactly 2 of 143 documents
                // are the extension shape (327 "No. Series Copilot Cap.", 2015 "Entity Text
                // Capability", both extending "Copilot Capability").
                //
                // They are SKIPPED rather than reported unbuildable, because the runner has
                // nothing addressable to compare against and that is a property of the
                // derivation rather than a per-object failure: AlEnumMetadataRegistry keys an
                // extension's values by the id of the enum it EXTENDS, never by the extension's
                // own id, and for a precompiled dependency the values are folded into the base
                // entry through Register (not RegisterExtension) at BcAppFallback's registration
                // — measured: SnapshotRaw reports 0 extension entries for both bundles. So there
                // is no runner object with id 327 to build. Counted and reported by
                // MetadataEquivalenceReport.EnumExtensionDocuments so it cannot be a silent drop;
                // tracked by #3807.
                if (IsEnumExtensionDocument(document))
                {
                    enumExtensionDocuments.Add($"Enum {obj.Id} '{obj.Name}'");
                    continue;
                }

                objectKey = $"Enum {obj.Id}";
                try
                {
                    expected = enumFromXml.Invoke(new object?[] { document.DocumentElement });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own MetaEnum " +
                                    $"constructor threw — {Describe(ex)}");
                    continue;
                }
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own MetaEnum " +
                                    "constructor produced null");
                    continue;
                }

                try
                {
                    var runnerXml = RecordPatches.TryBuildEnumMetadataEquivalenceXml(obj.Id);
                    if (runnerXml is null)
                    {
                        unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner's enum " +
                                        "registry knows no enum with that id");
                        continue;
                    }
                    var runnerDoc = new XmlDocument();
                    runnerDoc.LoadXml(runnerXml);
                    actual = enumFromXml.Invoke(new object?[] { runnerDoc.DocumentElement });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }
            else if (obj.Kind == "PageDefinition")
            {
                objectKey = $"Page {obj.Id}";
                try
                {
                    expected = pageFromXml.Invoke(new object?[] { document.DocumentElement });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own PageDefinition " +
                                    $"constructor threw — {Describe(ex)}");
                    continue;
                }
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own PageDefinition " +
                                    "constructor produced null");
                    continue;
                }

                try
                {
                    // The runner's page-metadata producer, and the exact document every runner
                    // consumer of page metadata reads: RunnerXmlMetadataLoader hands this XML to
                    // BC, which deserializes it with the same constructor used for BC's side
                    // above. So the comparison is one type against itself, and it measures the
                    // document the runner actually ships rather than a test-only rendering.
                    var runnerXml = RecordPatches.TryBuildDependencyPageMetadata(obj.Id);
                    if (runnerXml is null)
                    {
                        unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner built no page " +
                                        "metadata at all");
                        continue;
                    }
                    var runnerDoc = new XmlDocument();
                    runnerDoc.LoadXml(runnerXml);
                    actual = pageFromXml.Invoke(new object?[] { runnerDoc.DocumentElement });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }
            else
            {
                objectKey = $"Table {obj.Id}";
                expected = fromXml.Invoke(null, new object?[] { document.DocumentElement, 0 });
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own CreateMetaTableFromXml returned null");
                    continue;
                }

                actual = null;
                try
                {
                    var ncl = RecordPatches.GetOrBuildNCLMetaTable(obj.Id);
                    if (ncl is not null) actual = getOriginal.Invoke(ncl, null);
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }

            if (actual is null)
            {
                unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner built no metadata at all");
                continue;
            }

            compared++;
            differences.AddRange(obj.Kind switch
            {
                "PageDefinition" => MetadataObjectDiff.Compare(expected, actual, objectKey, PageDiffOptions),
                "Query" => MetadataObjectDiff.Compare(expected, actual, objectKey, QueryDiffOptions),
                "Enum" => MetadataObjectDiff.Compare(expected, actual, objectKey, EnumDiffOptions),
                _ => MetadataObjectDiff.Compare(expected, actual, objectKey),
            });
        }

        var kindsPresent = bundle.Census.Keys.ToArray();
        return new MetadataEquivalenceReport(
            bundle,
            kindsPresent.Where(k => ComparedKinds.Contains(k, StringComparer.Ordinal)).ToArray(),
            kindsPresent.Where(k => !ComparedKinds.Contains(k, StringComparer.Ordinal)).ToArray(),
            compared, unbuildable, differences, enumExtensionDocuments);
    }

    /// <summary>
    /// Query collections pair by the element's own Id, not by position, for the same
    /// structural reason page controls do: the two sides legitimately differ in LENGTH, so
    /// positional pairing turns one absent element into a cascade of fabricated
    /// "A differs from B" rows on every element after it.
    ///
    /// <para>Measured on BC 28.1.49838.53910 before this option was passed — see the PR for
    /// #3782 steps 3/4. The ids are BC-compiler-assigned and the runner uses them VERBATIM
    /// (BcAppSymbolCache.QueryColumnSymbol's own comment says why: precompiled callers pass
    /// them to NavQuery.ValidateExpectedType and GetColumnByNo), so pairing on them asserts an
    /// identity BC itself relies on rather than one this harness invented.</para>
    ///
    /// <para>#Ordinal still fires when both sides hold the same id set, so a genuine reordering
    /// is not hidden — MetadataObjectDiffTests.Id_paired_elements_still_report_a_reordering.</para>
    ///
    /// <para>Three spellings per member for the reason PageIdPairedMembers documents: BC backs
    /// each collection with a field and the differ walks both, so a signature listed only in
    /// its plain form leaves the field-backed path positionally paired.</para>
    /// </summary>
    private static readonly MetadataObjectDiffOptions QueryDiffOptions = new()
    {
        PairByIdMembers = QueryIdPairedMembers(),
    };

    private static IReadOnlySet<string> QueryIdPairedMembers()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { "MetaTable.Fields" };
        void Add(string declaringType, string member, string field)
        {
            set.Add($"{declaringType}.{member}");
            set.Add($"{declaringType}.{field}");
        }

        Add("MetaQuery", "DataItems", "#dataItemsField");
        Add("MetaQueryDataItem", "Columns", "#columnsField");
        Add("MetaQueryDataItem", "Filters", "#filtersField");
        return set;
    }

    /// <summary>
    /// Whether an <c>&lt;Enum&gt;</c> document is an ENUM EXTENSION's rather than a base enum's.
    ///
    /// <para>The discriminator is the value container, measured on BC 28.1.49838.53910 over all
    /// 143 Enum-rooted documents in the two bundles: a base enum wraps its values in a
    /// <c>&lt;Values&gt;</c> element, an extension states bare <c>&lt;Value&gt;</c> children of
    /// the root. Deliberately NOT keyed on the CaptionTranslationKey text, which also says
    /// <c>EnumExtension</c>: that is a computed string and reading a structural fact out of it
    /// would break the moment BC changed its key format.</para>
    /// </summary>
    private static bool IsEnumExtensionDocument(XmlDocument document)
    {
        foreach (XmlNode child in document.DocumentElement!.ChildNodes)
            if (child is XmlElement e && e.Name == "Value")
                return true;
        return false;
    }

    /// <summary>
    /// An enum's values pair by their AL-declared <c>Ordinal</c>, not by position.
    ///
    /// <para>Not a convenience: BC's emitter writes an enum's values in NAME order while the
    /// runner's registry holds them in declaration order, so the two lists are genuinely
    /// permutations of each other and positional pairing fabricates a difference on every value
    /// after the first divergence. Measured on BC 28.1.49838.53910, System Application enum 2616
    /// "Printer Paper Kind": BC's document opens <c>A2=66, A3=8, A4=9, A5=11, A6=70</c> — plainly
    /// alphabetical, plainly not ordinal order.</para>
    ///
    /// <para><b>And the one enum where pairing still falls back to position is the finding, not
    /// a gap in this option.</b> <c>TryPairById</c> refuses a duplicate key, and enum 2616 is the
    /// only one of the 142 whose runner-side ordinals contain a duplicate — because
    /// <c>TryParseEnumSymbol</c> reads an absent <c>Ordinal</c> as "previous + 1" where
    /// SymbolReference.json omits it to mean 0 (#3805). So the enum that cannot be paired is
    /// exactly the enum with the defect, and <c>Values[67].Ordinal expected 0, got 40</c> is a
    /// real disagreement reported through the positional path rather than an artifact of it.
    /// Fixing #3805 makes this enum pair by ordinal like the other 141.</para>
    ///
    /// <para><c>Ordinal</c> is in <see cref="MetadataObjectDiffOptions.IdPropertyNames"/> here
    /// rather than in the default set because it is an identity for THIS type only:
    /// <c>MetaEnumValue</c> has no <c>Id</c>/<c>ID</c> at all, and a type carrying both would
    /// otherwise pair on whichever reflection returned first.</para>
    ///
    /// <para>#Ordinal still fires when both sides hold the same ordinal set, so a genuine
    /// reordering is not hidden — MetadataObjectDiffTests.Id_paired_elements_still_report_a_reordering.</para>
    /// </summary>
    private static readonly MetadataObjectDiffOptions EnumDiffOptions = new()
    {
        PairByIdMembers = EnumIdPairedMembers(),
        IdPropertyNames = new[] { "Ordinal", "Id", "ID" },
    };

    private static IReadOnlySet<string> EnumIdPairedMembers()
        // Two spellings, for the reason PageIdPairedMembers documents: BC backs the collection
        // with a field and the differ walks both, so listing only the plain property leaves the
        // field-backed path positionally paired.
        //
        // The field is `values`, NOT `valuesField` — MetaEnum declares a plain private field
        // rather than using an auto-property, so the differ walks it as `#values` and the
        // `#<name>Field` convention every page collection follows does not apply. Measured:
        // with only the property listed, 3,014 of the enum value differences reported the paired
        // path `Values[id=N]` while 477 reported the positional `Values[N]` — including the one
        // genuine artifact this pairing exists to remove, `Enum 2616 Values[67].Ordinal`. A
        // guessed field name is indistinguishable from no entry at all.
        => new HashSet<string>(StringComparer.Ordinal)
        {
            "MetaTable.Fields",
            "MetaEnum.Values",
            "MetaEnum.#values",
        };

    /// <summary>
    /// Page control collections pair by the control's own ID, not by position.
    ///
    /// Position is the differ's default because order is meaningful in AL, and for a table's
    /// fields and keys that is the right call. It is wrong here for a structural reason: BC's
    /// Controls list holds the page's ORDINARY field controls as well as its part controls,
    /// and the runner reconstructs only the parts (DependencyPageMetadataXml's header says why
    /// — a field control's value binding lives in the .app's IL, not in this XML). So the two
    /// lists legitimately differ in length, and every element after the first divergence shifts.
    ///
    /// Measured on BC 28.1.49838.53910 before this option was passed: System Application
    /// page 4312 reported its part 'InputMessagePart' as differing from 'LogsPart', page 9855
    /// 'Permissions' from 'MetadataPermissions', and so on — 12 fabricated Name/ID/PagePartID
    /// triples across seven pages, none of which is a disagreement about any control. Pairing
    /// by id turns that cascade back into what it is: the runner does not build the ordinary
    /// controls, reported once per control as a presence difference.
    ///
    /// #Ordinal still fires when both sides hold the same id set, so a genuine reordering is
    /// not hidden — see MetadataObjectDiff.TryPairById and
    /// MetadataObjectDiffTests.Id_paired_elements_still_report_a_reordering.
    /// </summary>
    private static readonly MetadataObjectDiffOptions PageDiffOptions = new()
    {
        PairByIdMembers = PageIdPairedMembers(),
    };

    /// <summary>
    /// Every spelling of a page collection whose elements carry an <c>ID</c>.
    ///
    /// THREE spellings per member, and all three are needed. BC's page types implement their
    /// interfaces EXPLICITLY and back each collection with a field, so the differ walks one
    /// collection three times and reports it under three different signatures:
    ///
    ///     ControlContainerDefinition.Controls
    ///     ControlContainerDefinition.Microsoft.Dynamics.Nav.Types.Metadata.IMetaControlContainerDefinition.Controls
    ///     ControlContainerDefinition.#controlsField
    ///
    /// PairByIdMembers matches on the signature, so listing only the plain one leaves the other
    /// two positionally paired — which is exactly the state that produced the 12 fabricated
    /// 'InputMessagePart vs LogsPart' rows on BC 28.1.49838.53910, all of them on the
    /// interface-qualified path (#3782).
    ///
    /// Deliberately NOT here: ContentDefinition.Containers and the container types themselves.
    /// Measured on the same build — ControlContainerDefinition and ContentDefinition expose no
    /// ID property at all, so pairing would fall back to position anyway, and listing them
    /// would assert an identity BC does not give them.
    /// </summary>
    private static IReadOnlySet<string> PageIdPairedMembers()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { "MetaTable.Fields" };
        void Add(string declaringType, string iface, string member, string field)
        {
            set.Add($"{declaringType}.{member}");
            set.Add($"{declaringType}.Microsoft.Dynamics.Nav.Types.Metadata.{iface}.{member}");
            set.Add($"{declaringType}.{field}");
        }

        Add("ControlContainerDefinition", "IMetaControlContainerDefinition", "Controls", "#controlsField");
        Add("ControlGroupDefinition", "IMetaControlGroupDefinition", "Controls", "#controlsField");
        Add("RepeaterDefinition", "IMetaRepeaterDefinition", "Controls", "#controlsField");
        Add("GridLayoutDefinition", "IMetaGridLayoutDefinition", "Controls", "#controlsField");
        Add("ColumnLayoutDefinition", "IMetaColumnLayoutDefinition", "Controls", "#controlsField");
        Add("PageDefinition", "IMetaPageDefinition", "ActionContainers", "#actionContainersField");
        Add("ActionContainerDefinition", "IMetaActionContainerDefinition", "Actions", "#actionsField");
        Add("ActionGroupDefinition", "IMetaActionGroupDefinition", "Actions", "#actionsField");
        return set;
    }

    /// <summary>
    /// A reflected call reports the real fault as InnerException; the outer
    /// TargetInvocationException says only "an exception was thrown", which turns every
    /// unbuildable line into the same useless sentence.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var real = ex.InnerException ?? ex;
        return $"{real.GetType().Name}: {real.Message}";
    }

    /// <summary>
    /// The .app a bundle was generated from, located by identity rather than by a path baked
    /// into the manifest. Returns null when this box has no such package — which the caller
    /// must report, never absorb.
    /// </summary>
    public static string? FindAppPackage(GroundTruthBundle bundle)
    {
        // Scoped to the build's own artifact directory rather than the whole root, for the
        // same reason GroundTruthDirForThisBuild is: several builds of one app are on a dev
        // box, they carry the same object ids, and picking the wrong one is not detectable
        // downstream.
        var artifacts = Path.Combine(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, bundle.BcBuild);
        var wanted = $"{bundle.AppPublisher}_{bundle.AppName}_{bundle.AppVersion}.app";
        if (!Directory.Exists(artifacts)) return null;
        foreach (var candidate in Directory.EnumerateFiles(artifacts, "*.app", SearchOption.AllDirectories))
            if (string.Equals(Path.GetFileName(candidate), wanted, StringComparison.Ordinal))
                return candidate;
        return null;
    }
}
