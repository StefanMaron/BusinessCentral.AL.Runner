using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class NumberSequenceEntryPointResolutionTests
{
    private static TypeDefinition AddNumberSequenceType(ModuleDefinition module)
    {
        var type = new TypeDefinition(
            "Microsoft.Dynamics.Nav.Runtime",
            "ALNumberSequence",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(type);
        return type;
    }

    private static MethodDefinition AddMethod(
        ModuleDefinition module,
        TypeDefinition type,
        string name,
        MethodAttributes attributes,
        TypeReference returnType,
        params TypeReference[] parameterTypes)
    {
        var method = new MethodDefinition(name, attributes, returnType);
        foreach (var parameterType in parameterTypes)
            method.Parameters.Add(new ParameterDefinition(parameterType));
        type.Methods.Add(method);
        method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        return method;
    }

    [Fact]
    public void ExactPublicStaticShape_IsSelectedAmongSameNameSiblings()
    {
        using var module = ModuleDefinition.CreateModule("SyntheticNumberSequence", ModuleKind.Dll);
        var type = AddNumberSequenceType(module);
        var expected = AddMethod(module, type, "ALRange",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Int64,
            module.TypeSystem.String, module.TypeSystem.Int32, module.TypeSystem.Boolean);
        AddMethod(module, type, "ALRange",
            MethodAttributes.Private | MethodAttributes.Static,
            module.TypeSystem.Int64,
            module.TypeSystem.String, module.TypeSystem.Int32, module.TypeSystem.Boolean);
        AddMethod(module, type, "ALRange",
            MethodAttributes.Public,
            module.TypeSystem.Int64,
            module.TypeSystem.String, module.TypeSystem.Int32, module.TypeSystem.Boolean);
        AddMethod(module, type, "ALRange",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Int64,
            module.TypeSystem.String, module.TypeSystem.Int64, module.TypeSystem.Boolean);

        var resolved = NclCecilRewrite.ResolveNumberSequenceEntryPoint(
            type, "ALRange", "System.Int64",
            "System.String", "System.Int32", "System.Boolean");

        Assert.Same(expected, resolved);
    }

    [Fact]
    public void ByRefShape_IsMatchedExactly()
    {
        using var module = ModuleDefinition.CreateModule("SyntheticNumberSequenceByRef", ModuleKind.Dll);
        var type = AddNumberSequenceType(module);
        var byRefLong = module.ImportReference(typeof(ByRef<long>));
        var expected = AddMethod(module, type, "ALRange",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Int64,
            module.TypeSystem.String, module.TypeSystem.Int32, byRefLong, module.TypeSystem.Boolean);

        var resolved = NclCecilRewrite.ResolveNumberSequenceEntryPoint(
            type, "ALRange", "System.Int64",
            "System.String", "System.Int32",
            "Microsoft.Dynamics.Nav.Runtime.ByRef`1<System.Int64>", "System.Boolean");

        Assert.Same(expected, resolved);
    }

    [Fact]
    public void MissingExactShape_HardErrorsAndListsAvailableOverloads()
    {
        using var module = ModuleDefinition.CreateModule("SyntheticNumberSequenceMissing", ModuleKind.Dll);
        var type = AddNumberSequenceType(module);
        AddMethod(module, type, "ALRange",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Int64,
            module.TypeSystem.String, module.TypeSystem.Int64, module.TypeSystem.Boolean);

        var error = Assert.Throws<InvalidOperationException>(() =>
            NclCecilRewrite.ResolveNumberSequenceEntryPoint(
                type, "ALRange", "System.Int64",
                "System.String", "System.Int32", "System.Boolean"));

        Assert.Contains("found 0", error.Message);
        Assert.Contains("System.Int32", error.Message);
        Assert.Contains("System.Int64", error.Message);
        Assert.Contains("#2049", error.Message);
    }

    [Fact]
    public void DuplicateExactShape_HardErrors()
    {
        using var module = ModuleDefinition.CreateModule("SyntheticNumberSequenceDuplicate", ModuleKind.Dll);
        var type = AddNumberSequenceType(module);
        for (var index = 0; index < 2; index++)
            AddMethod(module, type, "ALNext",
                MethodAttributes.Public | MethodAttributes.Static,
                module.TypeSystem.Int64,
                module.TypeSystem.String, module.TypeSystem.Boolean);

        var error = Assert.Throws<InvalidOperationException>(() =>
            NclCecilRewrite.ResolveNumberSequenceEntryPoint(
                type, "ALNext", "System.Int64", "System.String", "System.Boolean"));

        Assert.Contains("found 2", error.Message);
        Assert.Contains("#2049", error.Message);
    }
}
