// Test helpers — classes placed in BC-like namespaces so their stack frames
// contain the namespace strings that Executor.IsLikelyRunnerLimitation() looks for.
// These are ONLY used by RunnerErrorClassificationTests.

namespace Microsoft.Dynamics.Nav.TestHelper
{
    internal static class BcRuntimeSimulator
    {
        // NoInlining is required: the JIT must NOT inline this method into its caller,
        // otherwise the stack frame for this method disappears and
        // IsLikelyRunnerLimitation() cannot detect the Microsoft.Dynamics.Nav namespace
        // at frame 0.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void ThrowNull() =>
            throw new NullReferenceException("NavSession is null — service-tier context not available");
    }
}

namespace Microsoft.BusinessCentral.TestHelper
{
    internal static class BusinessCentralSimulator
    {
        // NoInlining is required: the JIT must NOT inline this method into its caller,
        // otherwise the stack frame for this method disappears and
        // IsLikelyRunnerLimitation() cannot detect the Microsoft.BusinessCentral namespace
        // at frame 0.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void ThrowNull() =>
            throw new NullReferenceException("BC service-tier context not available");
    }
}
