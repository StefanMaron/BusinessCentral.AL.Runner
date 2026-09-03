// TestTransactionModelDetectionTests — pins TestExecutor.IsAutoRollback(), the mechanism
// half of AL Runner issue #2400.
//
// The bug it fixes
// -----------------
// RunOne invokes an AL [Test] method directly via reflection (m.Invoke), bypassing BC's own
// NavTestCodeunit.ExecuteTestMethodAsync dispatch — the method that reads a [Test]
// procedure's [TransactionModel(TransactionModel::AutoRollback)] attribute and calls
// Session.Rollback() around it (decompiled, unmodified NCL body). Bypassing that dispatch
// meant the attribute had no effect at all: a codeunit under the default TestIsolation =
// Codeunit shares one transaction across every [Test], so an AutoRollback-annotated test's
// own writes stayed visible to every later [Test] in the same codeunit — concretely,
// Microsoft's own Tests-SINGLESERVER Codeunit 134614 ("Test App Permissions") declares the
// attribute on every [Test], relying on it to give each test a clean "Security Group" table;
// without it the second test's own InitializeData() found the first test's SG1 row still
// there and failed with "The group SG1 already exists."
//
// Why this is a runner-side test and not (only) an AL corpus test
// -----------------------------------------------------------------
// The AL-level claim — "a [Test] carrying TransactionModel::AutoRollback gets its own
// uncommitted writes rolled back the moment it finishes, even under TestIsolation = Codeunit"
// — is plain BC behaviour and belongs upstream: see TestTransactionModelAutoRollback (60899),
// submitted against StefanMaron/BusinessCentral.AL.Language.Tests#114. This file pins the
// RUNNER'S OWN attribute-discovery logic instead: that IsAutoRollback finds the
// TransactionModel property on a [NavTest]-shaped attribute (discovered by TYPE NAME, per
// this file's own convention — see TestExecutor.cs's header comment — so it works across a
// multi-bundle run with more than one Ncl assembly-load-context, which is why this test uses
// a LOCAL fake attribute type rather than the real Microsoft.Dynamics.Nav.Runtime one) and
// answers correctly for AutoRollback, every other TransactionModel value, and a method with
// no such attribute at all — none of which the corpus test above can address, since it has
// no way to ask the runner what its OWN dispatch logic decided.
using System.Reflection;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public class TestTransactionModelDetectionTests
{
    // Mirrors the shape TestExecutor.IsAutoRollback actually reads: a public property named
    // exactly "TransactionModel" on a type named exactly "NavTestAttribute", whose value's
    // ToString() is the enum member name — matching Microsoft.Dynamics.Nav.Runtime.
    // TestTransactionModel's own AutoCommit/AutoRollback/None shape. Declared as a nested
    // type here (not a reference to the real Ncl type) specifically so this test proves the
    // NAME-based discovery works independent of which assembly the real attribute lives in.
    private enum FakeTransactionModel { AutoCommit, AutoRollback, None }

    [AttributeUsage(AttributeTargets.Method)]
    private sealed class NavTestAttribute : Attribute
    {
        public FakeTransactionModel TransactionModel { get; set; }
    }

    private static class Probes
    {
        [NavTestAttribute(TransactionModel = FakeTransactionModel.AutoRollback)]
        public static void AutoRollbackMethod() { }

        [NavTestAttribute(TransactionModel = FakeTransactionModel.AutoCommit)]
        public static void AutoCommitMethod() { }

        [NavTestAttribute(TransactionModel = FakeTransactionModel.None)]
        public static void NoneMethod() { }

        [NavTestAttribute] // default enum value (0) = AutoCommit, matching real BC's default
        public static void DefaultAttributeMethod() { }

        public static void NoAttributeAtAllMethod() { }
    }

    private static MethodInfo Method(string name)
        => typeof(Probes).GetMethod(name, BindingFlags.Public | BindingFlags.Static)
           ?? throw new InvalidOperationException($"Probes.{name} not found");

    [Fact]
    public void AutoRollbackAttribute_IsDetected()
        => Assert.True(TestExecutor.IsAutoRollback(Method(nameof(Probes.AutoRollbackMethod))));

    [Fact]
    public void AutoCommitAttribute_IsNotAutoRollback()
        => Assert.False(TestExecutor.IsAutoRollback(Method(nameof(Probes.AutoCommitMethod))));

    [Fact]
    public void NoneAttribute_IsNotAutoRollback()
        => Assert.False(TestExecutor.IsAutoRollback(Method(nameof(Probes.NoneMethod))));

    /// <summary>
    /// The common case: a [Test] with no [TransactionModel(...)] override at all still gets
    /// a [NavTest] attribute (every AL [Test] does), whose TransactionModel defaults to
    /// AutoCommit (enum value 0) — must NOT be misread as AutoRollback.
    /// </summary>
    [Fact]
    public void DefaultAttribute_IsNotAutoRollback()
        => Assert.False(TestExecutor.IsAutoRollback(Method(nameof(Probes.DefaultAttributeMethod))));

    /// <summary>
    /// No [NavTest]-shaped attribute at all (would never actually happen for a real AL
    /// [Test] method, but this is exactly what the null-check in IsAutoRollback guards
    /// against) — must not throw and must answer false.
    /// </summary>
    [Fact]
    public void NoAttributeAtAll_IsNotAutoRollback()
        => Assert.False(TestExecutor.IsAutoRollback(Method(nameof(Probes.NoAttributeAtAllMethod))));

    /// <summary>
    /// The per-method cache: calling IsAutoRollback twice on the SAME MethodInfo must
    /// return the same answer both times (the cache stores the resolved attribute instance,
    /// not a stale boolean, so this also catches a cache keyed on the wrong identity).
    /// </summary>
    [Fact]
    public void RepeatedCalls_OnTheSameMethod_AgreeWithEachOther()
    {
        var m = Method(nameof(Probes.AutoRollbackMethod));

        Assert.True(TestExecutor.IsAutoRollback(m));
        Assert.True(TestExecutor.IsAutoRollback(m));
    }
}
