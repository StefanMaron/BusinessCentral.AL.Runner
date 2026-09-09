// ReflectiveParserInvoke — a shared, arity-tolerant call for the RecordPatches parser
// statics this suite drives by reflection (TryParseTableFile, TryParseTableExtensionFile,
// MergeExtensionFields, ...).
//
// #3600/#3615: a C# optional parameter is a call-site convenience the compiler fills in;
// MethodInfo.Invoke matches the argument array length exactly and does not, so adding one
// to TryParseTableFile/TryParseTableExtensionFile threw TargetParameterCountException at
// every reflective call site still passing the old count — see PR #3615 for the count and
// the classes. InvokeStatic pads any parameter a call site doesn't know about with its
// declared default (or null), so a future optional parameter does not repeat that failure.
using System;
using System.Reflection;

namespace AlRunner.Tests;

internal static class ReflectiveParserInvoke
{
    internal static object? InvokeStatic(this MethodInfo method, params object?[] knownArgs)
    {
        var parameters = method.GetParameters();
        if (knownArgs.Length > parameters.Length)
            throw new ArgumentException(
                $"{method.Name} takes {parameters.Length} parameter(s); {knownArgs.Length} supplied.");
        if (knownArgs.Length == parameters.Length)
            return method.Invoke(null, knownArgs);

        var args = new object?[parameters.Length];
        knownArgs.CopyTo(args, 0);
        for (var i = knownArgs.Length; i < parameters.Length; i++)
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        return method.Invoke(null, args);
    }
}
