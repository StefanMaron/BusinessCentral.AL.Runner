// EnumCompilerSymbolPropertyCaptureTests — issue #3912.
//
// THE GAP THIS CLOSES
// Two compiler-side readers in BcCompiler.CaptureOutputter had no test that drives them:
//
//   * ReadEnumExtensible            (#3807) — the enum's declared `Extensible` property
//   * ReadEnumImplementationFallback (#2306) — the enum-level DefaultImplementation and
//                                              UnknownValueImplementation codeunit lists
//
// Demonstrated on the issue rather than inferred: mutating ReadEnumExtensible to
// `return null;` unconditionally, rebuilt in Release with 0 `error CS`, left
// EnumExtensibleAndImplementationRenderTests at Failed: 0, Passed: 6, Total: 6.
//
// WHY THE EXISTING TESTS DO NOT COVER IT, AND ARE STILL RIGHT
// EnumExtensibleAndImplementationRenderTests constructs an EnumSymbol directly and asserts
// what the metadata-equivalence RENDER states; EnumDefaultImplementationTests constructs an
// AlEnumOptionMetadata directly and asserts the three-step resolution BC's
// NCLEnumMetadata.GetImplementationCodeunitId performs. Both are correctly scoped to their own
// observable. Neither reaches BcCompiler, so the path FROM BC's compiler symbol TO the
// registry record — which is what these two readers are — was unmeasured at both ends.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: every assertion is
// about what OUR capture pipeline stores in AlEnumMetadataRegistry after BC's own compiler has
// parsed the AL. The BC-side claims these values feed (an enum's DefaultImplementation
// resolving an interface cast, an extensible enum accepting an enumextension) are Base App /
// AL-language behaviour and are the corpus's to adjudicate, per
// .claude/rules/bc-behavior-tests-go-upstream.md.
//
// THE THREE-WAY ANSWER IS THE POINT
// Entry.Extensible is `bool?`, not `bool`, because BC's own emitter keeps "declared false" and
// "declared nothing" apart (12 of 144 emitted enum documents state no Extensible attribute).
// IApplicationObjectTypeSymbol.IsExtensible is `GetBooleanPropertyValue(Extensible) ??
// ExtensibleByDefault` and CANNOT tell those two apart, which is exactly why ReadEnumExtensible
// reads the PROPERTY. A test asserting only true/false would pass against a reader that had
// silently regressed to IsExtensible — so the absent case is asserted for both readers.
//
// ONE FIXTURE, BOTH READERS: a single AL enum can declare Extensible, DefaultImplementation and
// UnknownValueImplementation at once, so the "declared" half of both readers is one compile.
// The "absent" half needs sibling enums that declare nothing, which live in the same compile.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class EnumCompilerSymbolPropertyCaptureTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    // 90500-90509. Enum, codeunit and interface ids live in separate AL object-type
    // namespaces, so the block is read as one range here only for legibility.
    private const int ImplAId = 90501;
    private const int ImplBId = 90502;
    private const int DeclaresEverythingId = 90503;
    private const int DeclaresExtensibleFalseId = 90504;
    private const int DeclaresNothingId = 90505;

    public EnumCompilerSymbolPropertyCaptureTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-enum-compiler-symbol-capture-tests");
        Directory.CreateDirectory(_root);
        AlEnumMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlEnumMetadataRegistry.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// One compile carrying all three states of both readers. Enum 90503 declares
    /// <c>Extensible = true</c> plus both implementation fallbacks; 90504 declares
    /// <c>Extensible = false</c> and no fallbacks; 90505 declares neither.
    /// </summary>
    private AlEnumMetadataRegistry.Entry Compile(int id)
    {
        File.WriteAllText(Path.Combine(_root, "EnumPropertyCapture.al"), $$"""
            interface "EnumPropCapture Thing"
            {
                procedure Hello(): Integer;
            }

            codeunit {{ImplAId}} "EnumPropCapture Impl A" implements "EnumPropCapture Thing"
            {
                procedure Hello(): Integer begin exit(1); end;
            }

            codeunit {{ImplBId}} "EnumPropCapture Impl B" implements "EnumPropCapture Thing"
            {
                procedure Hello(): Integer begin exit(2); end;
            }

            enum {{DeclaresEverythingId}} "EnumPropCapture Declares All" implements "EnumPropCapture Thing"
            {
                Extensible = true;
                DefaultImplementation = "EnumPropCapture Thing" = "EnumPropCapture Impl A";
                UnknownValueImplementation = "EnumPropCapture Thing" = "EnumPropCapture Impl B";
                value(0; Alpha) { }
            }

            enum {{DeclaresExtensibleFalseId}} "EnumPropCapture Extensible False"
            {
                Extensible = false;
                value(0; Alpha) { }
            }

            enum {{DeclaresNothingId}} "EnumPropCapture Declares Nothing"
            {
                value(0; Alpha) { }
            }
            """);

        var output = new BcCompiler().Emit(new[] { _root }, "EnumPropCaptureModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the fixture to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");

        Assert.True(AlEnumMetadataRegistry.TryGet(id, out var entry),
            $"Expected enum {id} to be registered in AlEnumMetadataRegistry after emit");
        return entry;
    }

    [SkippableFact]
    public void ReadEnumExtensible_CapturesDeclaredTrue_FromTheCompilerSymbol()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        Assert.Equal(true, Compile(DeclaresEverythingId).Extensible);
    }

    [SkippableFact]
    public void ReadEnumExtensible_CapturesDeclaredFalse_NotNull()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // `false` and `null` are different answers. A reader that folded a declared false into
        // "declares nothing" would make the render omit an attribute BC's own emitter states
        // for 31 of 142 base enums.
        Assert.Equal(false, Compile(DeclaresExtensibleFalseId).Extensible);
    }

    [SkippableFact]
    public void ReadEnumExtensible_AnEnumDeclaringNone_CapturesNull_NotAlsOwnDefault()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // The state IApplicationObjectTypeSymbol.IsExtensible cannot express: AL's own default
        // for a non-implementing enum is false, so a reader that used IsExtensible would answer
        // `false` here and this assertion is what separates the two implementations.
        Assert.Null(Compile(DeclaresNothingId).Extensible);
    }

    [SkippableFact]
    public void ReadEnumImplementationFallback_CapturesBothDeclaredCodeunitIds()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var entry = Compile(DeclaresEverythingId);

        // The reader resolves the AL `Interface = Codeunit` form to the implementing codeunit's
        // OBJECT ID, one per interface-declaration index. Distinct ids for the two properties,
        // so a reader that read one property and answered for both would fail here.
        Assert.Equal(new[] { ImplAId }, entry.DefaultImplementations);
        Assert.Equal(new[] { ImplBId }, entry.UnknownImplementations);
    }

    [SkippableFact]
    public void ReadEnumImplementationFallback_AnEnumDeclaringNone_CapturesNullForBoth()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var entry = Compile(DeclaresNothingId);

        // Null, not an empty array: "declares none" is what AlEnumOptionMetadata's three-step
        // fallback reads as "no enum-level implementer", and what the render omits the
        // attribute for (#2306, #3807).
        Assert.Null(entry.DefaultImplementations);
        Assert.Null(entry.UnknownImplementations);
    }

    [SkippableFact]
    public void TheCapturedFallbacks_ResolveThroughAlEnumOptionMetadata()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var entry = Compile(DeclaresEverythingId);

        // The consumer end, driven from the COMPILED entry rather than a hand-built one: a
        // known ordinal with no per-value Implementation takes DefaultImplementation, an
        // unknown ordinal takes UnknownValueImplementation. That split is the whole reason the
        // two are read as separate properties, and EnumDefaultImplementationTests proves it
        // only against constructed inputs.
        var meta = new AlEnumOptionMetadata(entry.Name, entry.Id, entry.Options, entry.Indexes,
            entry.Implementations, entry.Captions,
            entry.DefaultImplementations, entry.UnknownImplementations);

        Assert.Equal(ImplAId, meta.GetImplementationCodeunitIdPublic(0, 0));
        Assert.Equal(ImplBId, meta.GetImplementationCodeunitIdPublic(7, 0));
    }
}
