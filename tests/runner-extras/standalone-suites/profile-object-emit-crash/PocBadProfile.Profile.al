/// <summary>
/// Issue #2238: a `profile` object referencing a RoleCenter page that never resolves
/// crashes BC's own ProfileMetadataEmitter (a NullReferenceException deep in
/// Microsoft.Dynamics.Nav.CodeAnalysis.SymbolExtensions.ShouldBeEmitted, reached from
/// ObjectMetadataEmitHelper.WriteAttributeProperties / ProfileMetadataEmitter.
/// WriteProfileHeader) instead of surfacing a clean AL0185 diagnostic and stopping there.
/// Compilation.Emit is atomic PER MODULE, so — before the runner-side fix in BcCompiler.cs
/// / Program.cs — this ONE broken profile took the WHOLE app's Emit down (EMIT-ZERO),
/// including the perfectly healthy "POC Tests" codeunit declared alongside it in
/// PocTests.Codeunit.al. See PocTests.Codeunit.al for the proving assertions.
///
/// The page reference is deliberately never declared anywhere in this suite — the crash
/// is reached via ANY unresolved RoleCenter reference, not a runner-specific broken page.
/// </summary>
profile "POC Bad Profile"
{
    Caption = 'POC Bad Profile';
    RoleCenter = "POC Nonexistent Role Center Page";
}
