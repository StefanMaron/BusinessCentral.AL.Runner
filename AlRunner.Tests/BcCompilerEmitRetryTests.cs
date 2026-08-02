// BcCompilerEmitRetryTests — atomic-per-module Emit resilience.
//
// Root cause being guarded
// -------------------------
// BC's Compilation.Emit(...) is atomic PER MODULE: if even one object in a large
// dependency package throws while emitting (an internal BC-emitter crash — e.g. a
// DotNet variable whose type never resolved, "Unexpected value 'None' of type
// NavTypeKind", or a bound-tree shape BC's codegen doesn't expect), the WHOLE
// module's Sources come back empty — EVERY object is lost, including perfectly
// valid, unrelated codeunits sitting next to the broken one.
//
// This produced the reported Pageworks Copilot-flow failure: "System Application
// Test Library" (177 objects) had ~18-20 broken mock/test-page objects unrelated to
// Copilot. Compilation.Emit threw once for the whole package, so even the healthy
// "Copilot Test Library" codeunit (132932) never got a real .NET type. At runtime,
// codeunit dispatch fell back to a NoOp stub for the unresolved id, and the NoOp's
// inherited OnInvoke unconditionally throws NavNCLMissingMethodException for ANY
// procedure call. That exception's message hardcodes literal "0" as the object id
// (Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLMissingMethodException.CreateMessage
// does `string.Format(culture, Lang.WrongReference, methodId, 0)` — unconditionally),
// which is why the failure read "the object with ID 0" even though ObjectId stamping
// (the unrelated "212-wall" ctor-replacement fix) was working correctly the whole
// time — confirmed by diagnostics before this fix was written; see PR description.
//
// Fix: BcCompiler.Emit now parses BC's own per-object failure info out of a crashed
// Emit() (either the AggregateException's "Object:'<Type> <Ns>."<Name>"'" text, or —
// for a crash that surfaces as a second-wave plain compile error instead of a thrown
// exception — the EmitResult.Diagnostics' real Location.SourceTree), excludes just
// those broken source files, and retries — iteratively, since excluding one crashing
// object can unmask another previously-hidden one — up to a bounded number of rounds.
//
// Test strategy
// -------------
// Model the general pattern with two minimal, synthetic AL objects compiled as one
// module: one object designed to crash BC's emitter (an unresolvable DotNet variable
// type — the same NavTypeKind.None crash class documented above), and one ordinary,
// healthy codeunit. Assert the healthy codeunit's method is still emitted (a concrete
// value assertion — RED before the retry-loop fix throws away BOTH objects; GREEN
// after it recovers the healthy one).
//
// Env-guarded like VersionAgnosticClosureTests: only runs when BC service-tier
// artifacts are actually provisioned on this machine (a bare CI leg without artifacts
// is a no-op, not a failure).

using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerEmitRetryTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerEmitRetryTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-emit-retry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // (The artifacts probe moved into BcEngineFixture, which now owns the whole
    // in-process engine bootstrap and exposes the result as BcEngineFixture.Ready.)

    [Fact]
    public void Emit_RecoversHealthyCodeunit_WhenAnUnrelatedObjectCrashesTheModuleEmit()
    {
        // The Ncl Cecil rewrite + runtime-patch bootstrap now happens ONCE in BcEngineFixture,
        // before any test in the bc-engine-serial collection runs. Doing it here instead meant
        // overwriting bin/…Ncl.dll while a parallel test class was loading types out of it —
        // see BcEngineCollection.cs for the torn-image failures that caused.
        if (!_engine.Ready) return; // no BC artifacts provisioned — skip, don't fail

        // One healthy codeunit whose real body must survive the retry...
        File.WriteAllText(Path.Combine(_root, "Good.al"), """
            codeunit 90100 "EmitRetryTest Good"
            {
                procedure GetAnswer(): Integer
                begin
                    exit(42);
                end;
            }
            """);

        // ...alongside one object engineered to crash BC's own emitter: a DotNet
        // variable referencing a type name that can never resolve. Even with a real
        // DotNet resolver factory attached (BcCompiler always attaches one), a type
        // that fails to resolve leaves NavTypeKind == None at codegen time, which is
        // exactly the crash class documented in BcCompiler.Emit's DotNet-resolver
        // comment (UnexpectedValue(NavTypeKind.None)).
        File.WriteAllText(Path.Combine(_root, "Bad.al"), """
            codeunit 90101 "EmitRetryTest Bad"
            {
                procedure Crash()
                var
                    x: DotNet "EmitRetryTest.Nonexistent.BogusType";
                begin
                    x := x.DoSomething();
                end;
            }
            """);

        var output = new BcCompiler().Emit(new[] { _root }, "EmitRetryTestModule");

        Assert.True(
            output.Sources.Count > 0,
            "Expected at least the healthy codeunit's source to survive — got 0 sources " +
            $"(diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}). This means the " +
            "unrelated crashing object still took down the WHOLE module's emit — the exact " +
            "atomic-emit gap this test guards against.");

        var good = output.Sources.FirstOrDefault(s => s.Code.Contains("EmitRetryTest Good"));
        Assert.True(
            good != null,
            "Expected the healthy 'EmitRetryTest Good' codeunit's C# to be present in the " +
            $"emitted sources; got: [{string.Join(", ", output.Sources.Select(s => s.Name))}]");
        Assert.Contains("GetAnswer", good!.Code);
    }
}
