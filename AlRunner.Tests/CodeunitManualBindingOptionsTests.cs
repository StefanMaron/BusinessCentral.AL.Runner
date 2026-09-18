// CodeunitManualBindingOptionsTests — issue #4289.
//
// WHAT #4289 REPORTED, AND WHAT MEASURING IT FOUND
//   PR #4287's body argued that EnsureCodeunitMetaById must repeat
//   NavCodeunit_get_MetaCodeunit's `_metaToClrType.AddOrUpdate(meta, clrType)`, because
//   "a meta built first by the new path and later read through the old one would answer
//   IsEventManualBinding = false". Deleting that line reds nothing, and the stated
//   consequence does not hold: see docs/codeunit-manual-binding.md#who-reads-iseventmanualbinding for the
//   Cecil census of BC 28.1 behind it. In one line — NavCodeunit_get_MetaCodeunit
//   re-stashes on its cache-HIT branch too, so reading through the old path repairs the
//   side table whatever the new path did.
//
//   What the stash really buys is EnsureCodeunitMetaById's own postcondition: the meta it
//   hands back is COMPLETE — every consumer of that meta answers from the AL-emitted
//   attribute — with no NavCodeunit instance anywhere in the run. That is a runner-internal
//   contract, not something BC can compose today, and this file says so rather than
//   implying an AL-observable claim. It is worth pinning because it is what makes the
//   id-only path substitutable for the instance path: the moment a BC caller reads
//   IsEventManualBinding off a meta it got by id, or get_MetaCodeunit's instance-field fast
//   path is reached before that helper ever stashed, the absence is a wrong `false`.
//
// WHY A SECOND PAIR OF ARMS
//   Both readers of [NavCodeunitOptionsAttribute] fall back to masking the Options enum
//   when the attribute carries no derived IsEventManualBinding property, and both masked
//   with 1. On BC 28.1.49838.53910 the enum is SingleInstance = 1, EventManualBinding = 2,
//   so that fallback answered the question exactly backwards. It is unreachable while BC's
//   attribute keeps the derived property, which is why nothing caught it; the stand-in
//   attribute below reaches it, and reaches it through the same public entry points BC
//   uses rather than by calling anything the fix introduced.
//
// FIXTURE SHAPE
//   Codeunit types are compiled and loaded here rather than declared, because
//   FindCodeunitType matches on the exact CLR name Codeunit{id} and the decision under test
//   is which attribute hangs off that type. Abstract, for the reason
//   CodeunitPublisherMetadataResolutionTests gives: nothing on this path instantiates one,
//   and an abstract subclass owes NavCodeunit's ~9 abstract members no override.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests.BcAttributeShapeWithoutDerivedProperty
{
    /// <summary>
    /// Enum mirroring <c>Microsoft.Dynamics.Nav.Runtime.NavCodeunitOptions</c>'s member
    /// names and values as measured on BC 28.1.49838.53910. Deliberately a SEPARATE type:
    /// the point of these arms is the branch that runs when BC's own attribute shape is not
    /// the one the runner knows, and reusing BC's enum could not express that.
    /// </summary>
    public enum NavCodeunitOptions
    {
        None = 0,
        SingleInstance = 1,
        EventManualBinding = 2,
    }

    /// <summary>
    /// Named exactly <c>NavCodeunitOptionsAttribute</c> because both readers key on
    /// <c>attr.GetType().Name</c>, and declaring <c>Options</c> but NOT the derived
    /// <c>IsEventManualBinding</c> property is what routes them to the fallback.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class NavCodeunitOptionsAttribute : Attribute
    {
        public NavCodeunitOptionsAttribute(NavCodeunitOptions options) => Options = options;

        public NavCodeunitOptions Options { get; }
    }
}

namespace AlRunner.Tests.BcAttributeShapeWithoutTheFlagMember
{
    /// <summary>
    /// An <c>Options</c> enum that declares no <c>EventManualBinding</c> member at all — the
    /// shape a future BC rename would produce. Value 1 on purpose: that is the bit the old
    /// hardcoded mask tested, so this is the arm that separates "read the member" from
    /// "test bit 0".
    /// </summary>
    public enum NavCodeunitOptions
    {
        None = 0,
        SomethingElse = 1,
    }

    /// <inheritdoc cref="AlRunner.Tests.BcAttributeShapeWithoutDerivedProperty.NavCodeunitOptionsAttribute"/>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class NavCodeunitOptionsAttribute : Attribute
    {
        public NavCodeunitOptionsAttribute(NavCodeunitOptions options) => Options = options;

        public NavCodeunitOptions Options { get; }
    }
}

namespace AlRunner.Tests.BcAttributeShapeWithoutOptions
{
    /// <summary>
    /// A <c>NavCodeunitOptionsAttribute</c> carrying NEITHER the derived
    /// <c>IsEventManualBinding</c> property NOR <c>Options</c> — the shape a BC rename of the
    /// options member would produce. Distinct from
    /// <c>BcAttributeShapeWithoutTheFlagMember</c>, where <c>Options</c> IS present and merely
    /// declares no <c>EventManualBinding</c> member: there the read SUCCEEDS and false is the
    /// right answer, here the read cannot be performed at all and false would be invented.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class NavCodeunitOptionsAttribute : Attribute
    {
        public NavCodeunitOptionsAttribute() { }
    }
}

namespace AlRunner.Tests.BcAttributeShapeWithNonEnumOptions
{
    /// <summary>
    /// <c>Options</c> is present and holds a <see cref="string"/> — a change to the member's
    /// TYPE rather than a rename, which is what separates this from
    /// <c>BcAttributeShapeWithoutOptions</c>. The lookup succeeds and hands back something no
    /// option flag can be decoded from, so <c>false</c> here would be invented rather than read.
    ///
    /// <para>The value deliberately SPELLS the flag: a decoder that fell back to matching text
    /// would answer <c>true</c>, and one that shrugged would answer <c>false</c>. Both are
    /// answers to a question that was never asked.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class NavCodeunitOptionsAttribute : Attribute
    {
        public NavCodeunitOptionsAttribute(string options) => Options = options;

        public string Options { get; }
    }
}

namespace AlRunner.Tests.BcAttributeShapeWithNullOptions
{
    /// <summary>
    /// <c>Options</c> is present, reference-typed, and reads as <c>null</c>. Kept apart from
    /// <c>BcAttributeShapeWithNonEnumOptions</c> because the two undecodable reads send a reader
    /// to different places — one says BC re-typed the member, the other that it stopped
    /// populating it — and a message that could not tell them apart would name neither.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class NavCodeunitOptionsAttribute : Attribute
    {
        public NavCodeunitOptionsAttribute() { }

        public string? Options => null;
    }
}

namespace AlRunner.Tests.BcAttributeShapeWithWideOptions
{
    /// <summary>
    /// An <c>Options</c> enum backed by <see cref="ulong"/> whose <c>EventManualBinding</c>
    /// member exceeds <see cref="long.MaxValue"/>. This is the ONLY reachable route into the
    /// decoder's former <c>catch { return false; }</c>: measured, <c>Convert.ToInt64</c> raises
    /// <see cref="OverflowException"/> on this value, while <c>Enum.Parse</c> cannot fail at all
    /// for a name that came from <c>Enum.GetNames</c>. The value decodes perfectly — the old
    /// instrument simply could not carry it — so the answer here is <c>true</c>, not a refusal.
    /// </summary>
    [Flags]
    public enum NavCodeunitOptions : ulong
    {
        None = 0,
        SingleInstance = 1,
        EventManualBinding = 0x8000_0000_0000_0000,
    }

    /// <inheritdoc cref="AlRunner.Tests.BcAttributeShapeWithoutDerivedProperty.NavCodeunitOptionsAttribute"/>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class NavCodeunitOptionsAttribute : Attribute
    {
        public NavCodeunitOptionsAttribute(NavCodeunitOptions options) => Options = options;

        public NavCodeunitOptions Options { get; }
    }
}

namespace AlRunner.Tests
{
    [Collection(BcEngineCollection.Name)]
    public sealed class CodeunitManualBindingOptionsTests
    {
        private readonly BcEngineFixture _engine;

        public CodeunitManualBindingOptionsTests(BcEngineFixture engine) => _engine = engine;

        /// <summary>
        /// Compile and load <c>Codeunit{id}</c> as an abstract <c>NavCodeunit</c> subclass
        /// carrying <paramref name="attributeSource"/>. References the live assemblies so the
        /// emitted type binds the same <c>NavCodeunit</c> the engine already mapped, and the
        /// same stand-in attribute type this test assembly declares.
        /// </summary>
        private static Type CompileAndLoadCodeunit(int id, string attributeSource)
        {
            var source = $@"
{attributeSource}
public abstract class Codeunit{id} : Microsoft.Dynamics.Nav.Runtime.NavCodeunit
{{
    protected Codeunit{id}(Microsoft.Dynamics.Nav.Runtime.ITreeObject owner, int objectId)
        : base(owner, objectId) {{ }}
}}";
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var compilation = CSharpCompilation.Create(
                $"al-runner-test-manualbinding-{id}-{Guid.NewGuid():N}",
                new[] { CSharpSyntaxTree.ParseText(source) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);
            Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
            var type = Assembly.Load(ms.ToArray()).GetType($"Codeunit{id}");
            Assert.NotNull(type);
            return type!;
        }

        private const string BcAttr = "Microsoft.Dynamics.Nav.Runtime.NavCodeunitOptionsAttribute";
        private const string BcEnum = "Microsoft.Dynamics.Nav.Runtime.NavCodeunitOptions";
        private const string StandInAttr =
            "AlRunner.Tests.BcAttributeShapeWithoutDerivedProperty.NavCodeunitOptionsAttribute";
        private const string StandInEnum =
            "AlRunner.Tests.BcAttributeShapeWithoutDerivedProperty.NavCodeunitOptions";

        private void RequireEngine() => TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        /// <summary>
        /// Reads the binding hook through the meta that <c>EnsureCodeunitMetaById</c> built,
        /// which is the composition the stash sits between. Never touches
        /// <c>_metaToClrType</c> — asserting the dictionary holds an entry would be the
        /// defect #4289 reports wearing a test's clothes.
        /// </summary>
        private static bool BindingAnswerForMetaBuiltById(int codeunitId)
        {
            var meta = AlRunner.BcRuntime.EnsureCodeunitMetaById(codeunitId);
            Assert.NotNull(meta);
            return AlRunner.BcRuntime.NCLMetaCodeunit_get_IsEventManualBinding(meta!);
        }

        [SkippableFact]
        public void MetaBuiltById_ForAManualBindingCodeunit_TheBindingHookAnswersTrue()
        {
            RequireEngine();

            // Fresh id: CodeunitPatches._codeunitTypeCache memoizes a MISS, so the type must
            // be loaded before anything asks about the id (the trap
            // CodeunitPublisherMetadataResolutionTests records).
            const int codeunitId = 61741;
            CompileAndLoadCodeunit(codeunitId,
                $"[{BcAttr}({BcEnum}.EventManualBinding)]");

            Assert.True(BindingAnswerForMetaBuiltById(codeunitId),
                "a meta built from the id alone must answer IsEventManualBinding from the "
                + "AL-emitted [NavCodeunitOptions], with no NavCodeunit instance in the run");
        }

        [SkippableFact]
        public void MetaBuiltById_ForASingleInstanceOnlyCodeunit_TheBindingHookAnswersFalse()
        {
            RequireEngine();

            // SingleInstance rather than "no attribute at all" on purpose: an absent attribute
            // answers false down a different branch, so it could not tell "read the flag" from
            // "found nothing". This arm stays GREEN when the stash is deleted, which is what
            // makes the arm above a discriminating red rather than a coverage red.
            const int codeunitId = 61742;
            CompileAndLoadCodeunit(codeunitId,
                $"[{BcAttr}({BcEnum}.SingleInstance)]");

            Assert.False(BindingAnswerForMetaBuiltById(codeunitId),
                "SingleInstance is not manual binding; on BC 28.1.49838.53910 the enum is "
                + "SingleInstance = 1, EventManualBinding = 2");
        }

        [SkippableFact]
        public void AttributeWithoutTheDerivedProperty_TheBindingHookReadsTheEventManualBindingMember()
        {
            RequireEngine();

            const int manualId = 61743;
            const int singleId = 61744;
            CompileAndLoadCodeunit(manualId, $"[{StandInAttr}({StandInEnum}.EventManualBinding)]");
            CompileAndLoadCodeunit(singleId, $"[{StandInAttr}({StandInEnum}.SingleInstance)]");

            Assert.True(BindingAnswerForMetaBuiltById(manualId),
                "EventManualBinding = 2, so a fallback masking with 1 answers this backwards");
            Assert.False(BindingAnswerForMetaBuiltById(singleId),
                "SingleInstance = 1, so a fallback masking with 1 answers this backwards");
        }

        [SkippableFact]
        public void AttributeWithoutTheDerivedProperty_TheDispatcherReadsTheEventManualBindingMember()
        {
            RequireEngine();

            // IsManualBindingCodeunitType is the OTHER reader — the codeunit-event dispatcher
            // and AlEventSubscriberAdapter go through it, never through the meta — and it
            // carried the same fallback. It takes a Type, so this arm is independent of the
            // stash and stays GREEN when the stash is deleted.
            var manual = CompileAndLoadCodeunit(61745, $"[{StandInAttr}({StandInEnum}.EventManualBinding)]");
            var single = CompileAndLoadCodeunit(61746, $"[{StandInAttr}({StandInEnum}.SingleInstance)]");

            Assert.True(AlRunner.BcRuntime.IsManualBindingCodeunitType(manual));
            Assert.False(AlRunner.BcRuntime.IsManualBindingCodeunitType(single));
        }

        [SkippableFact]
        public void OptionsEnumWithNoEventManualBindingMember_AnswersFalseRatherThanTestingBitZero()
        {
            RequireEngine();

            // A genuinely absent flag stays a false — the same answer a codeunit carrying no
            // attribute gets, and what BC's own derived property returns for one that declares
            // nothing (guards-need-a-third-state.md's "keep the genuinely-absent case a pass").
            // The old mask read this enum's unrelated member 1 as manual binding.
            const string attr =
                "AlRunner.Tests.BcAttributeShapeWithoutTheFlagMember.NavCodeunitOptionsAttribute";
            const string en =
                "AlRunner.Tests.BcAttributeShapeWithoutTheFlagMember.NavCodeunitOptions";

            var type = CompileAndLoadCodeunit(61747, $"[{attr}({en}.SomethingElse)]");

            Assert.False(BindingAnswerForMetaBuiltById(61747));
            Assert.False(AlRunner.BcRuntime.IsManualBindingCodeunitType(type));
        }

        /// <summary>
        /// The third state. An attribute with no <c>Options</c> member at all is a read the
        /// runner CANNOT PERFORM, not an absent flag — so it refuses rather than answering
        /// <c>false</c>, which would report every manual-binding codeunit as automatic and
        /// silently unbind its subscribers.
        ///
        /// <para>The arm above is the one this is paired with, and the pair is the whole
        /// claim: there <c>Options</c> is present and merely lacks
        /// <c>EventManualBinding</c>, the read succeeds, and <c>false</c> is correct
        /// (<c>guards-need-a-third-state.md</c>'s "keep the genuinely-absent case a pass").
        /// A change that made both refuse, or both answer false, would leave exactly one of
        /// these two red.</para>
        /// </summary>
        [SkippableFact]
        public void AttributeWithoutAnOptionsMember_RefusesRatherThanAnsweringFalse()
        {
            RequireEngine();

            const string attr =
                "AlRunner.Tests.BcAttributeShapeWithoutOptions.NavCodeunitOptionsAttribute";

            // Two ids so each reader sees a type the other has not touched. One id would work
            // today — the dispatcher's _manualBindingTypeCache is a ConcurrentDictionary and
            // GetOrAdd stores nothing for a factory that threw, and the meta path memoizes no
            // answer at all — but that is a property of the caches, not of the claim under
            // test, and separate ids keep this arm independent of it.
            var type = CompileAndLoadCodeunit(61748, $"[{attr}()]");
            CompileAndLoadCodeunit(61749, $"[{attr}()]");

            var viaDispatcher = Assert.Throws<AlRunner.Infrastructure.BcShapeGapException>(
                () => AlRunner.BcRuntime.IsManualBindingCodeunitType(type));
            Assert.Equal("NavCodeunitOptionsAttribute.Options", viaDispatcher.Member);
            Assert.Contains("silently unbind every manual subscriber", viaDispatcher.Detail);

            // The meta path is the OTHER reader and reaches the same decoder by its own route,
            // so a refusal added to one of them only would leave this arm green.
            var meta = AlRunner.BcRuntime.EnsureCodeunitMetaById(61749);
            Assert.NotNull(meta);
            var viaMeta = Assert.Throws<AlRunner.Infrastructure.BcShapeGapException>(
                () => AlRunner.BcRuntime.NCLMetaCodeunit_get_IsEventManualBinding(meta!));
            Assert.Equal("NavCodeunitOptionsAttribute.Options", viaMeta.Member);
        }

        /// <summary>
        /// <c>Options</c> present but holding a non-enum. The read SUCCEEDED, which is why this
        /// looks like the <c>false</c> arm and is not one: what came back is a value the decoder
        /// cannot get a bit out of, and <c>BcShapeGapException</c>'s own header puts "present and
        /// holding something of a shape the runner cannot use" on the refusing side of the line,
        /// next to its worked <c>RequiredEnumerable</c> case.
        ///
        /// <para>Paired against
        /// <see cref="OptionsEnumWithNoEventManualBindingMember_AnswersFalseRatherThanTestingBitZero"/>:
        /// there the value IS an enum and merely lacks the member, the decode runs to completion,
        /// and <c>false</c> is the read answer. A change collapsing the two reds exactly one of
        /// them (#4319).</para>
        /// </summary>
        [SkippableFact]
        public void OptionsHoldingANonEnum_RefusesRatherThanAnsweringFalse()
        {
            RequireEngine();

            const string attr =
                "AlRunner.Tests.BcAttributeShapeWithNonEnumOptions.NavCodeunitOptionsAttribute";

            // Two ids for the reason the sibling refusal arm gives: one per reader, so neither
            // arm depends on how the other reader's cache treats a factory that threw.
            var type = CompileAndLoadCodeunit(61750, $"[{attr}(\"EventManualBinding\")]");
            CompileAndLoadCodeunit(61751, $"[{attr}(\"EventManualBinding\")]");

            var viaDispatcher = Assert.Throws<AlRunner.Infrastructure.BcShapeGapException>(
                () => AlRunner.BcRuntime.IsManualBindingCodeunitType(type));
            Assert.Equal("NavCodeunitOptionsAttribute.Options", viaDispatcher.Member);
            Assert.Contains("holds a String, which is not an enum", viaDispatcher.Detail);
            Assert.Contains("silently unbind every manual subscriber", viaDispatcher.Detail);

            var meta = AlRunner.BcRuntime.EnsureCodeunitMetaById(61751);
            Assert.NotNull(meta);
            var viaMeta = Assert.Throws<AlRunner.Infrastructure.BcShapeGapException>(
                () => AlRunner.BcRuntime.NCLMetaCodeunit_get_IsEventManualBinding(meta!));
            Assert.Equal("NavCodeunitOptionsAttribute.Options", viaMeta.Member);
            Assert.Contains("holds a String, which is not an enum", viaMeta.Detail);
        }

        /// <summary>
        /// <c>Options</c> present, reference-typed, reading as <c>null</c>. Refuses like the
        /// wrong-type arm and must say so DIFFERENTLY — the two causes have different remedies,
        /// and a message that folded them together would send a reader to the wrong one.
        /// </summary>
        [SkippableFact]
        public void OptionsReadingAsNull_RefusesAndNamesTheNullRatherThanAWrongType()
        {
            RequireEngine();

            const string attr =
                "AlRunner.Tests.BcAttributeShapeWithNullOptions.NavCodeunitOptionsAttribute";

            var type = CompileAndLoadCodeunit(61752, $"[{attr}()]");
            CompileAndLoadCodeunit(61753, $"[{attr}()]");

            var viaDispatcher = Assert.Throws<AlRunner.Infrastructure.BcShapeGapException>(
                () => AlRunner.BcRuntime.IsManualBindingCodeunitType(type));
            Assert.Equal("NavCodeunitOptionsAttribute.Options", viaDispatcher.Member);
            Assert.Contains("read as null", viaDispatcher.Detail);
            // The discrimination, not decoration: a refusal that reported every undecodable
            // Options the same way would pass every other assertion in this arm.
            Assert.DoesNotContain("is not an enum", viaDispatcher.Detail);

            var meta = AlRunner.BcRuntime.EnsureCodeunitMetaById(61753);
            Assert.NotNull(meta);
            var viaMeta = Assert.Throws<AlRunner.Infrastructure.BcShapeGapException>(
                () => AlRunner.BcRuntime.NCLMetaCodeunit_get_IsEventManualBinding(meta!));
            Assert.Equal("NavCodeunitOptionsAttribute.Options", viaMeta.Member);
            Assert.Contains("read as null", viaMeta.Detail);
        }

        /// <summary>
        /// An <c>Options</c> flag whose value does not fit an <see cref="long"/>. The decoder used
        /// to mask through <c>Convert.ToInt64</c> inside a <c>try</c> whose <c>catch</c> answered
        /// <c>false</c>; on a ulong-backed enum that conversion overflows, so a codeunit that
        /// DOES declare manual binding was reported as automatic.
        ///
        /// <para>Not a refusal: the value decodes exactly, the old instrument just could not
        /// carry it. Both directions are asserted, because a decoder that answered <c>true</c>
        /// unconditionally would satisfy the positive arm alone.</para>
        /// </summary>
        [SkippableFact]
        public void WideOptionsEnum_WhoseFlagExceedsInt64_DecodesRatherThanAnsweringFalse()
        {
            RequireEngine();

            const string attr =
                "AlRunner.Tests.BcAttributeShapeWithWideOptions.NavCodeunitOptionsAttribute";
            const string en =
                "AlRunner.Tests.BcAttributeShapeWithWideOptions.NavCodeunitOptions";

            var manual = CompileAndLoadCodeunit(61754, $"[{attr}({en}.EventManualBinding)]");
            var single = CompileAndLoadCodeunit(61755, $"[{attr}({en}.SingleInstance)]");
            CompileAndLoadCodeunit(61756, $"[{attr}({en}.EventManualBinding)]");
            CompileAndLoadCodeunit(61757, $"[{attr}({en}.SingleInstance)]");

            Assert.True(AlRunner.BcRuntime.IsManualBindingCodeunitType(manual),
                "EventManualBinding = 0x8000000000000000 overflows Convert.ToInt64, which the "
                + "old catch turned into 'this codeunit does not declare manual binding'");
            Assert.False(AlRunner.BcRuntime.IsManualBindingCodeunitType(single),
                "SingleInstance = 1 carries none of the EventManualBinding bit; this arm stays "
                + "GREEN under the old code too, so it is the control rather than the proof");

            Assert.True(BindingAnswerForMetaBuiltById(61756),
                "the meta reader reaches the same decoder by its own route");
            Assert.False(BindingAnswerForMetaBuiltById(61757));
        }
    }
}
