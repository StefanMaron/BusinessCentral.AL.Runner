// PageEnumFieldMetadataPatchesTests — issue #1896 (Page.RunModal() on a page with a
// page-global control bound to an Enum-typed variable threw NavALException at form
// materialisation because NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions calls
// NCLMetadata.TryGetMetaApplicationObject(ObjectType.Enum, ...), which the runner never
// populated for Enum objects).
//
// This is a RUNNER-MECHANISM test: it pins the Cecil-rewritten replacement bodies
// (BcRuntime.NCLMetaForm_ApplyEnumMetadataToMetaPageExpressions /
// NCLMetaForm_ApplyEnumMetadataToPageExpressions, AlRunner/Patches/PageEnumFieldMetadataPatches.cs)
// directly against AlEnumMetadataRegistry, without needing the BC engine or a real page —
// exactly the boundary the Cecil rewrite crosses (real BC's own MetaPageDefinition/
// PageDefinition/MetaDataFieldDefinition/DataFieldDefinition types are still real BC types;
// only the enum-metadata LOOKUP source changed). The BEHAVIOURAL claim ("Page.RunModal on a
// page with an Enum-typed page-global control works, and the control's real value round-trips")
// is proven upstream against a live BC service tier — see
// StefanMaron/BusinessCentral.AL.Language.Tests handlers/TestPageEnumVariableControl_*.al, per
// docs/rules/bc-behavior-tests-go-upstream.md — and locally end-to-end in
// tests/runner-extras/page-enum-control-modal. This test exists so a regression in OUR OWN
// lookup-substitution logic fails loudly here, in milliseconds.
using System.Collections.Generic;
using System.Linq;
using AlRunner;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageEnumFieldMetadataPatchesTests : IDisposable
{
    public PageEnumFieldMetadataPatchesTests()
    {
        AlEnumMetadataRegistry.Clear();
    }

    public void Dispose()
    {
        AlEnumMetadataRegistry.Clear();
    }

    private const int EnumId = 90340;

    private static void RegisterTestEnum()
    {
        AlEnumMetadataRegistry.Register(
            EnumId, "PemTest Kind",
            options: new[] { "Field", "Block", "Image" },
            indexes: new[] { 0, 1, 2 },
            captions: new string?[] { "Fields", "Blocks", "Images" });
    }

    // Positive: MetaPageDefinition overload (the frozen/cached path NavForm.GetMasterPage
    // reaches) computes OptionString/OptionCaption/OptionValues from the registry, in BC's own
    // comma-joined shape — the exact three strings NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions's
    // real (unmodified) body would have produced from a genuine NCLEnumMetadata.
    [Fact]
    public void MetaPageDefinitionOverload_EnumField_ComputesOptionStringCaptionAndValuesFromRegistry()
    {
        RegisterTestEnum();

        var page = new MetaPageDefinition(expressions: new List<MetaDataFieldDefinition>
        {
            new MetaDataFieldDefinition(name: "Combi", enumId: EnumId),
        });

        var result = BcRuntime.NCLMetaForm_ApplyEnumMetadataToMetaPageExpressions(page);

        var expr = Assert.Single(result.Expressions);
        Assert.Equal("Field,Block,Image", expr.OptionString);
        Assert.Equal("Fields,Blocks,Images", expr.OptionCaption);
        Assert.Equal("0,1,2", expr.OptionValues);
    }

    // Positive: a field with NO enum id (EnumIdSpecified = false) passes through completely
    // untouched — proves the fix is selective, not a blanket rewrite of every field. If the
    // implementation always ran ResolveEnumOptionStrings regardless of EnumIdSpecified, this
    // would throw (no enum registered for a field that declares none).
    [Fact]
    public void MetaPageDefinitionOverload_NonEnumField_PassesThroughUnchanged()
    {
        var plainField = new MetaDataFieldDefinition(name: "PlainText");
        var page = new MetaPageDefinition(expressions: new List<MetaDataFieldDefinition> { plainField });

        var result = BcRuntime.NCLMetaForm_ApplyEnumMetadataToMetaPageExpressions(page);

        // No enum-typed expressions found -> real BC's body (and this rewrite) returns the
        // SAME instance, not a rebuilt clone.
        Assert.Same(page, result);
        Assert.False(result.Expressions.Single().EnumIdSpecified);
    }

    // Positive: PageDefinition overload (the mutable/thawed sibling) mutates the
    // DataFieldDefinition in place, matching real BC's body shape, with the same data.
    [Fact]
    public void PageDefinitionOverload_EnumField_MutatesOptionStringCaptionAndValuesInPlace()
    {
        RegisterTestEnum();

        var field = new DataFieldDefinition(name: "Combi", enumId: EnumId);
        var page = new PageDefinition(expressions: new List<DataFieldDefinition> { field });

        var result = BcRuntime.NCLMetaForm_ApplyEnumMetadataToPageExpressions(page);

        Assert.Same(page, result);
        Assert.Equal("Field,Block,Image", field.OptionString);
        Assert.Equal("Fields,Blocks,Images", field.OptionCaption);
        Assert.Equal("0,1,2", field.OptionValues);
    }

    // Negative — the load-bearing claim: a GENUINELY unknown enum id (never registered — not
    // source-compiled, not in a loaded dependency) must still fail LOUDLY with BC's own
    // NavMetadataNotFoundException naming the exact object type and id, not silently resolve to
    // an empty/default OptionString. A fix that swallowed the miss (e.g. defaulting to "") would
    // pass every test above and hide the same bug in reverse.
    [Fact]
    public void MetaPageDefinitionOverload_UnregisteredEnumId_ThrowsNavMetadataNotFoundException()
    {
        const int unregisteredId = 90341;
        Assert.False(AlEnumMetadataRegistry.TryGet(unregisteredId, out _),
            "test setup invariant: this id must not be registered");

        var page = new MetaPageDefinition(expressions: new List<MetaDataFieldDefinition>
        {
            new MetaDataFieldDefinition(name: "Combi", enumId: unregisteredId),
        });

        var ex = Assert.Throws<NavMetadataNotFoundException>(
            () => BcRuntime.NCLMetaForm_ApplyEnumMetadataToMetaPageExpressions(page));

        Assert.Equal(ObjectType.Enum, ex.ObjectType);
        Assert.Equal(unregisteredId, ex.ObjectId);
    }

    // Negative, PageDefinition overload — same claim, other overload.
    [Fact]
    public void PageDefinitionOverload_UnregisteredEnumId_ThrowsNavMetadataNotFoundException()
    {
        const int unregisteredId = 90342;
        Assert.False(AlEnumMetadataRegistry.TryGet(unregisteredId, out _),
            "test setup invariant: this id must not be registered");

        var page = new PageDefinition(expressions: new List<DataFieldDefinition>
        {
            new DataFieldDefinition(name: "Combi", enumId: unregisteredId),
        });

        var ex = Assert.Throws<NavMetadataNotFoundException>(
            () => BcRuntime.NCLMetaForm_ApplyEnumMetadataToPageExpressions(page));

        Assert.Equal(ObjectType.Enum, ex.ObjectType);
        Assert.Equal(unregisteredId, ex.ObjectId);
    }
}
