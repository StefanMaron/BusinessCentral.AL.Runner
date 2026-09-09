// SystemAppPackageRegistrationFailureTests — issue #3581.
//
// THE DEFECT
//   RegisterSystemAppPackage() is the engine-bootstrap step that puts the platform SystemApp
//   package into _bcAppPaths and eagerly parses the NCL-internal system tables out of it —
//   RecordLink 2000000068, Field 2000000041, Object 2000000038 and the rest. BC's own NCL code
//   constructs records of those tables directly (`new NavRecord(parent, id)`), bypassing the
//   NavRecordHandle patch, so their NCLMetaTable must be in NCLMetadata's cache dict before any
//   test runs. If this step does not happen, the metadata is simply absent for the whole life of
//   the process.
//
//   It had three exits that all resolved toward "initialization succeeded":
//
//     1. `if (asm == null) { Console.Error.WriteLine(...); return; }`
//     2. `if (mGetStream == null) { Console.Error.WriteLine(...); return; }`
//     3. `catch (Exception ex) { Console.Error.WriteLine(...); }`   // then falls off the end
//
//   Register() carries on, PopulateNclMetadataCache() runs against a _parsedTables that never
//   got the system tables, and the run proceeds. This is the shape
//   .claude/rules/guards-need-a-third-state.md names: "registration failed" spelled as its own
//   success state.
//
//   The stderr line is not a mitigation. Log's default-verbosity filter drops lines that START
//   with a bracketed component tag, and every one of these does. Measured on this branch: a full
//   corpus run's captured output contains ZERO occurrences of "registered SystemPackage", while
//   the same bundle under --verbose shows the line at log position 33. At the verbosity anyone
//   actually runs at, all three exits are silent.
//
// THE PER-EXIT JUDGEMENT — which of the three states each one is
//   Exit 3 is the one with a live wrong answer behind it rather than a latent one.
//   AddBcAppPath already REFUSES: it wraps any symbol-read failure in a
//   BcAppSymbolReadException specifically so "a failed .app is never left in _bcAppPaths" and
//   Program.cs can exit 1 (#2712, the OutOfMemory case that turned into 47 plausible-looking
//   test failures and exit 0). The outer catch here caught that deliberate refusal and
//   converted it straight back into a silent success — one frame above where it was raised.
//   Arm 6 is what proves it now escapes.
//
//   Exits 1 and 2 are failed LOOKUPS, not legitimate absences, and that distinction is the
//   whole judgement. Measured across every complete BC artifact set on this box —
//   27.0.38460.53934, 27.3.44313.53909, 27.5.46862.48827, 27.5.46862.53931, 28.0.46665.54338,
//   28.1.49838.53910, 28.1.49838.54308, 28.2.50931.54319, 28.3.52162.54347, 28.4.53241.54346,
//   28.4.53241.54407 — Microsoft.BusinessCentral.SystemApp.dll is present in 11 of 11. The one
//   directory without it holds a single entry, `platform-apps`, and is not an engine directory
//   at all: ForceLoadBcDlls would already have thrown on Microsoft.Dynamics.Nav.Common before
//   reaching here. So there is no state in which the engine bootstraps and the SystemApp
//   assembly is legitimately absent, and refusing cannot break a bundle that genuinely has
//   nothing to register.
//
//   That is the constraint the rule states as "a genuinely absent thing must stay a pass".
//   Over-refusing here would break startup on every BC version rather than on a future one, so
//   the negative controls in section 2 are load-bearing: they pin that a HEALTHY assembly,
//   type and method still register silently, and that a package which extracts and registers
//   cleanly still returns normally.
//
// WHY THE HELPER TAKES ITS INPUTS AS PARAMETERS
//   Same idiom as QueryJoinFindRequestShapeGapTests (#3656) and PermissionMetadataShapeGapTests:
//   the real production decision logic is driven with fakes standing in for the BC assembly,
//   type and method, so each failure can be injected SEPARATELY with no BC install and no
//   poking of the RecordPatches process-global statics — which would leave them poisoned for
//   every other test in this assembly.
//
// No Base Application floor (.claude/rules/no-base-app-in-csharp-tests.md).
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class SystemAppPackageRegistrationFailureTests
{
    private const string Surface = "NCL-internal system tables (RecordLink, Field, Object, …)";

    // ══ 1. THE EXITS THAT MUST REFUSE ════════════════════════════════════════════════════
    //
    // Each arm injects exactly ONE failure and leaves the others healthy. An arm asserting
    // only "it throws" would pass on an implementation that threw from a DIFFERENT branch, so
    // each asserts the distinguishing substring PRESENT and its sibling's ABSENT.

    [Fact]
    public void AnUnloadableSystemAppAssembly_Refuses_NamingTheAssembly()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => Register(asm: null));

        Assert.Contains("Microsoft.BusinessCentral.SystemApp", ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Surface, ex.Surface);
        Assert.EndsWith(" — see docs/limitations.md#bc-shape-gaps", ex.Message, StringComparison.Ordinal);

        // The DISCRIMINATOR. Without this the arm passes on a build that refuses at the
        // GetPackageStream branch instead, which is a different defect with the same symptom.
        Assert.DoesNotContain("GetPackageStream", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentSystemPackageType_Refuses_NamingTheType_NotTheAssembly()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Register(asm: new FakeAssembly(typeof(SomethingElse))));

        Assert.Contains("SystemPackage", ex.Member, StringComparison.Ordinal);
        Assert.Equal(Surface, ex.Surface);

        // The type went missing, NOT the assembly and NOT the method: both siblings absent.
        Assert.DoesNotContain("assembly could not be loaded", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPackageStream", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentGetPackageStreamMethod_Refuses_NamingTheMethod_NotTheAssemblyOrType()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Register(asm: new FakeAssembly(typeof(SystemAppFakes.NoGetStream.SystemPackage))));

        Assert.Contains("GetPackageStream", ex.Member, StringComparison.Ordinal);
        Assert.Equal(Surface, ex.Surface);

        // Siblings absent: the type WAS found, so nothing may claim it was not, and the
        // assembly WAS loaded.
        Assert.DoesNotContain("assembly could not be loaded", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("type not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGetPackageStreamThatReturnsNoStream_Refuses_RatherThanRegisteringNothing()
    {
        // `(Stream)mGetStream.Invoke(null, null)!` — the null-forgiving `!` is an annotation
        // that throws nothing. A method that is PRESENT but answers null used to NRE on the
        // CopyTo one frame down, inside the outer catch, and be reported as
        // "registration failed: NullReferenceException:" with no member name at all.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Register(asm: new FakeAssembly(typeof(SystemAppFakes.NullStream.SystemPackage))));

        Assert.Contains("GetPackageStream", ex.Member, StringComparison.Ordinal);
        Assert.Contains("no package stream", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("method not found", ex.Message, StringComparison.Ordinal);
    }

    // ══ 2. NEGATIVE CONTROLS — the exits that must STAY silent ═══════════════════════════
    //
    // Without these, "a failed lookup refuses" is satisfiable by a change that refuses on
    // everything — which would break engine startup on EVERY BC version rather than on a
    // future one. That is the over-refusing direction the issue calls worse than the defect.

    [Fact]
    public void AHealthyAssemblyTypeAndMethod_RegisterTheExtractedPackage_AndDoNotThrow()
    {
        var registered = Register(asm: new FakeAssembly(typeof(SystemAppFakes.Healthy.SystemPackage)));

        Assert.NotNull(registered);
        Assert.True(File.Exists(registered));
        Assert.Equal(SystemAppFakes.Healthy.SystemPackage.Payload.Length, new FileInfo(registered!).Length);
    }

    [Fact]
    public void TheAbsentMemberAndTheHealthyRead_AreDistinguishedByWhetherAnythingIsThrownAtAll()
    {
        // The pairing that makes section 1 ARMED rather than merely quiet, stated in one place
        // so neither half can be weakened alone: the two outcomes are separated by the
        // strongest discriminator available — one throws and the other returns a path — so a
        // fix that collapsed them cannot pass both lines.
        var absent = Record.Exception(() => Register(asm: null));
        var healthy = Record.Exception(() => Register(asm: new FakeAssembly(typeof(SystemAppFakes.Healthy.SystemPackage))));

        var gap = Assert.IsType<BcShapeGapException>(absent);
        Assert.Contains("Microsoft.BusinessCentral.SystemApp", gap.Member, StringComparison.Ordinal);
        Assert.Null(healthy);
    }

    // ══ 3. THE OUTER CATCH — the live wrong answer, not the latent one ═══════════════════

    [Fact]
    public void ARefusalFromTheRegistrationStep_ReachesTheCaller_RatherThanBeingSwallowed()
    {
        // AddBcAppPath raises BcAppSymbolReadException precisely so a .app whose symbols could
        // not be read is never left registered and Program.cs can exit 1 (#2712). The outer
        // `catch (Exception)` ate exactly that, one frame above where it was raised, and
        // reported it as a stderr line the default verbosity filter drops.
        var boom = new BcAppSymbolReadException("/some/system.app", "table symbols",
                                                new InvalidOperationException("symbols unreadable"));

        var ex = Assert.Throws<BcAppSymbolReadException>(
            () => Register(asm: new FakeAssembly(typeof(SystemAppFakes.Healthy.SystemPackage)),
                           register: _ => throw boom));

        Assert.Same(boom, ex);
    }

    [Fact]
    public void AnExtractionFailure_ReachesTheCaller_RatherThanReadingAsInitializationSucceeded()
    {
        // The other half of exit 3: the failure comes from extraction rather than from
        // registration. A stream whose read throws stands in for a full disk, a permission
        // fault, or a truncated embedded resource.
        var ex = Assert.Throws<IOException>(
            () => Register(asm: new FakeAssembly(typeof(SystemAppFakes.ThrowingRead.SystemPackage))));

        Assert.Equal("the package stream refused", ex.Message);
    }

    [Fact]
    public void TheEagerParseStep_IsNotSwallowedEither()
    {
        // EagerParseAllBcAppTables is what actually materialises the system tables into
        // _parsedTables. A failure there leaves the .app registered but the metadata absent —
        // the exact end state the issue describes — and it was inside the same catch.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Register(asm: new FakeAssembly(typeof(SystemAppFakes.Healthy.SystemPackage)),
                           eagerParse: () => throw new InvalidOperationException("eager parse refused")));

        Assert.Equal("eager parse refused", ex.Message);
    }

    // ══ 4. THE REFLECTION PIN: name, arity, uniqueness ═══════════════════════════════════
    //
    // ReflectionDrivenHelperLivenessTests measures LIVENESS from IL for every RecordPatches
    // member any test names as a literal, and this file names RegisterSystemAppPackageCore —
    // so a change that leaves the method declared but stops production reaching it fails
    // there. What is pinned HERE is the half that guard cannot see.

    [Fact]
    public void TheHelperIsUniqueByName_AndTakesTheParametersTheseArmsDriveItWith()
    {
        var candidates = typeof(RecordPatches)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "RegisterSystemAppPackageCore")
            .ToList();

        Assert.Single(candidates);
        Assert.Equal(
            new[] { typeof(Assembly), typeof(Action<string>), typeof(Action) },
            candidates[0].GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(string), candidates[0].ReturnType);
    }

    // ══ Plumbing ═════════════════════════════════════════════════════════════════════════

    private static string? Register(
        Assembly? asm, Action<string>? register = null, Action? eagerParse = null)
    {
        var m = typeof(RecordPatches).GetMethod(
                    "RegisterSystemAppPackageCore", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "test setup: RecordPatches.RegisterSystemAppPackageCore not found");
        try
        {
            return (string?)m.Invoke(null, new object?[]
            {
                asm,
                register ?? (Action<string>)(_ => { }),
                eagerParse ?? (Action)(() => { }),
            });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    // ── Fakes standing in for BC's SystemApp assembly and its SystemPackage type. ──
    //
    // Each names the member it is missing, so a refusal that names the wrong one is visible in
    // the assertion rather than inferable from an absence.

    /// <summary>
    /// An Assembly whose GetTypes() answers exactly the type under test. Only GetTypes() and
    /// Location are reached by the production helper; everything else stays at the base
    /// class's behaviour so an unexpected read fails loudly rather than answering a default.
    /// </summary>
    private sealed class FakeAssembly : Assembly
    {
        private readonly Type[] _types;
        public FakeAssembly(params Type[] types) => _types = types;
        public override Type[] GetTypes() => _types;
        public override string Location => string.Empty;   // drives the no-asmInfo GUID suffix
    }

    /// <summary>A type that is NOT SystemPackage, so the type lookup finds nothing. Its own name
    /// is what makes the arm about an absent SystemPackage rather than an empty assembly.</summary>
    private sealed class SomethingElse
    {
    }

}

