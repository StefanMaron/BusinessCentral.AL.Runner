// EnumCaptionCaptureTests — issue #1775 (Format(enum) returned the AL member name
// instead of the declared Caption).
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that
// two of OUR OWN components behave correctly —
//   1. BcCompiler.CaptureOutputter.AddApplicationObject reads each enum value's
//      Caption property (via IEnumValueSymbol.GetProperty(PropertyKind.Caption)) at
//      emit time and stores it in AlEnumMetadataRegistry, alongside enumextension
//      values registered against the base enum's id; and
//   2. AlEnumOptionMetadata.GetCaptionFromIndex serves that captured text back,
//      falling back to the member name only when a value declares no Caption at all.
//
// The BEHAVIORAL claim ("Format(<enum value>) returns the Caption in real BC") is
// proven upstream against a live BC service tier — see
// StefanMaron/BusinessCentral.AL.Language.Tests PR for "Test Enum Caption" (60947) /
// "ALT Caption Kind" (60910), per docs/rules/bc-behavior-tests-go-upstream.md. This
// test exists so a regression in OUR OWN capture/serve pipeline fails loudly here,
// in milliseconds, without needing the BC engine loaded a second time.

using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class EnumCaptionCaptureTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public EnumCaptionCaptureTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-enum-caption-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        AlEnumMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlEnumMetadataRegistry.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void CaptureOutputter_ReadsDeclaredCaption_AndFallsBackToNameWhenAbsent()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        File.WriteAllText(Path.Combine(_root, "CaptionKind.al"), """
            enum 90200 "EnumCaptionTest Kind"
            {
                value(0; None) { Caption = 'None'; }
                value(1; ArchivedRecord) { Caption = 'Archived Item'; }
                value(2; NoCaptionDeclared) { }
            }
            """);

        var output = new BcCompiler().Emit(new[] { _root }, "EnumCaptionTestModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the enum to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");

        Assert.True(AlEnumMetadataRegistry.TryGet(90200, out var entry),
            "Expected enum 90200 to be registered in AlEnumMetadataRegistry after emit");

        Assert.NotNull(entry.Captions);
        var idxArchived = Array.IndexOf(entry.Options, "ArchivedRecord");
        var idxNoCaption = Array.IndexOf(entry.Options, "NoCaptionDeclared");
        Assert.True(idxArchived >= 0 && idxNoCaption >= 0,
            $"Expected both members present; got [{string.Join(", ", entry.Options)}]");

        // Positive: the declared Caption text is captured verbatim, not collapsed to
        // the member name.
        Assert.Equal("Archived Item", entry.Captions![idxArchived]);

        // Negative: a value with NO Caption property at all captures null — "declares
        // none" — rather than silently defaulting to the member name at capture time
        // (that default is the CONSUMER's job, asserted below).
        Assert.Null(entry.Captions![idxNoCaption]);

        // Consumer side: AlEnumOptionMetadata.GetCaptionFromIndex must return the
        // captured Caption for the explicit value, and fall back to the member name
        // for the value with none — this is the exact override issue #1775 reported
        // as forwarding straight to GetOptionFromIndex (i.e. always the member name).
        var meta = new AlEnumOptionMetadata(entry.Name, entry.Id, entry.Options, entry.Indexes,
            entry.Implementations, entry.Captions);
        Assert.Equal("Archived Item", meta.GetCaptionFromIndex(entry.Indexes[idxArchived]));
        Assert.Equal("NoCaptionDeclared", meta.GetCaptionFromIndex(entry.Indexes[idxNoCaption]));
    }

    [SkippableFact]
    public void CaptureOutputter_EnumExtensionValueCaption_RegisteredAgainstBaseId()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        File.WriteAllText(Path.Combine(_root, "BaseKind.al"), """
            enum 90201 "EnumCaptionTest Base Kind"
            {
                Extensible = true;
                value(0; A) { Caption = 'Alpha'; }
            }
            """);
        File.WriteAllText(Path.Combine(_root, "BaseKindExt.al"), """
            enumextension 90202 "EnumCaptionTest Base Kind Ext" extends "EnumCaptionTest Base Kind"
            {
                value(1; B) { Caption = 'Beta Caption'; }
            }
            """);

        var output = new BcCompiler().Emit(new[] { _root }, "EnumCaptionTestExtModule");
        Assert.True(output.Sources.Count > 0,
            $"Expected the enum+extension to emit; diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}");

        Assert.True(AlEnumMetadataRegistry.TryGet(90201, out var entry),
            "Expected base enum 90201 (merged with its extension's values) to be registered");

        var idxB = Array.IndexOf(entry.Options, "B");
        Assert.True(idxB >= 0, $"Expected extension value 'B' merged in; got [{string.Join(", ", entry.Options)}]");
        Assert.Equal("Beta Caption", entry.Captions?[idxB]);

        var meta = new AlEnumOptionMetadata(entry.Name, entry.Id, entry.Options, entry.Indexes,
            entry.Implementations, entry.Captions);
        Assert.Equal("Beta Caption", meta.GetCaptionFromIndex(entry.Indexes[idxB]));
    }
}
