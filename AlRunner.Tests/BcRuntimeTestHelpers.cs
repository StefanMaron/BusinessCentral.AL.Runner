// Test helpers — classes placed in BC-like namespaces so their stack frames
// contain the namespace strings that Executor.IsLikelyRunnerLimitation() looks for.
// These are ONLY used by RunnerErrorClassificationTests.

namespace Microsoft.Dynamics.Nav.TestHelper
{
    internal static class BcRuntimeSimulator
    {
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
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void ThrowNull() =>
            throw new NullReferenceException("BC service-tier context not available");
    }
}
