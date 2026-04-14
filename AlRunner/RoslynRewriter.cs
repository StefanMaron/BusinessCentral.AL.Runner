using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// No namespace - must be accessible from Program.cs top-level statements

/// <summary>
/// Roslyn CSharpSyntaxRewriter that transforms BC-generated C# into standalone code.
/// Replaces the fragile regex-based CSharpRewriter (now RegexRewriter) with a proper
/// syntax-tree-based approach.
/// </summary>
public class RoslynRewriter : CSharpSyntaxRewriter
{
    // Track the current top-level class name during visitation so that
    // VisitPropertyDeclaration can extract the table ID for Rec/xRec properties.
    private string? _currentClassName;

    private static readonly HashSet<string> BcAttributeNames = new(StringComparer.Ordinal)
    {
        "NavCodeunitOptions",
        "NavFunctionVisibility",
        "NavCaption",
        "NavName",
        // "NavTest" — kept for test method discovery by the executor
        // "SourceSpans" — kept for coverage line mapping
        "SignatureSpan",
        "ReturnValue",
        "NavObjectId",
        "NavByReferenceAttribute",
    };

    private static readonly HashSet<string> RemoveUsings = new(StringComparer.Ordinal)
    {
        "Microsoft.Dynamics.Nav.Runtime.Extensions",
        "Microsoft.Dynamics.Nav.Runtime.Report",
        "Microsoft.Dynamics.Nav.EventSubscription",
        "Microsoft.Dynamics.Nav.Common.Language",
    };

    /// <summary>
    /// Methods that take ITreeObject as first arg which we strip (e.g., value.ALByValue(this))
    /// </summary>
    // Methods on BC types that accept ITreeObject/NavRecord but should be no-ops in standalone mode.
    // NOTE: RunEvent is NOT stripped here anymore — it's intercepted specifically in the
    // statement-level rewriter so we can dispatch to registered event subscribers (#32).
    private static readonly HashSet<string> StripEntireCallMethods = new(StringComparer.Ordinal)
    {
        "ALCommit",   // ALDatabase.ALCommit() — SQL transaction commit, no-op standalone
        "ALSelectLatestVersion", // ALDatabase.ALSelectLatestVersion() — no-op standalone
        "ALBindSubscription",   // ALSession.ALBindSubscription() — event binding, no-op standalone
        "ALUnbindSubscription", // ALSession.ALUnbindSubscription() — event unbinding, no-op standalone
    };

    private static readonly HashSet<string> StripITreeObjectArgMethods = new(StringComparer.Ordinal)
    {
        "ALByValue", "ModifyLength", "ALRecord",
    };

    /// <summary>
    /// Methods where the first 'this' argument (ITreeObject) should be replaced with null!,
    /// keeping the rest of the call intact. These are BC runtime methods on real BC types
    /// that require ITreeObject but work with null in standalone mode.
    /// e.g. blob.ALCreateInStream(this, inStr) -> blob.ALCreateInStream(null!, inStr)
    /// </summary>
    private static readonly HashSet<string> NullifyFirstThisArgMethods = new(StringComparer.Ordinal)
    {
        "ALCreateInStream", "ALCreateOutStream",
    };

    /// <summary>
    /// Methods where the first 'this' argument (ITreeObject) should be removed entirely,
    /// keeping the rest of the call intact. These are methods on mock types (MockRecordRef)
    /// where the mock doesn't need the ITreeObject parameter.
    /// e.g. recRef.ALField(this, fieldNo) -> recRef.ALField(fieldNo)
    /// </summary>
    private static readonly HashSet<string> StripFirstThisArgMethods = new(StringComparer.Ordinal)
    {
        "ALField",
        "ALFieldIndex",
    };

    /// <summary>
    /// Names of .Target methods on record handles that should have .Target stripped.
    /// </summary>
    private static readonly HashSet<string> RecordTargetMethods = new(StringComparer.Ordinal)
    {
        "ALInit", "ALInsert", "ALModify", "ALGet", "ALFind", "ALNext", "ALDelete",
        "ALDeleteAll", "ALCount", "ALSetRange", "ALSetFilter", "ALFindSet",
        "ALFindFirst", "ALFindLast", "ALIsEmpty", "ALCalcFields", "ALSetCurrentKey",
        "ALReset", "ALCopy", "ALCopyFilter", "ALCopyFilters", "ALTestField", "ALTestFieldSafe", "ALValidate", "ALValidateSafe", "ALRename",
        "ALLockTable", "ALCalcSums", "ALSetLoadFields", "ALFieldCaption", "ALSetRecFilter",
        "ALTableCaption", "ALTableName", "ALTestFieldNavValueSafe",
        "ALFilterGroup", "ALSetRangeSafe", "ALReadIsolation",
        "ALTransferFields", "ALMark", "ALMarkedOnly",
        "ALGetFilters", "ALGetRangeMinSafe", "ALGetRangeMaxSafe",
        "SetFieldValueSafe", "GetFieldValueSafe", "GetFieldRefSafe",
    };

    public static string Rewrite(string csharp)
    {
        return RewriteToTree(csharp).GetRoot().ToFullString();
    }

    /// <summary>
    /// Rewrite BC-generated C# and return the result as a SyntaxTree.
    /// NormalizeWhitespace is applied to fix trivia left by node removal.
    /// The returned tree can be passed directly to Roslyn compilation,
    /// avoiding a redundant parse round-trip through string form.
    /// </summary>
    public static SyntaxTree RewriteToTree(string csharp)
    {
        var tree = CSharpSyntaxTree.ParseText(csharp);
        var root = tree.GetRoot();

        var rewriter = new RoslynRewriter();

        var newRoot = rewriter.Visit(root);
        return CSharpSyntaxTree.Create((CSharpSyntaxNode)newRoot.NormalizeWhitespace());
    }

    // -----------------------------------------------------------------------
    // Using directives
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var name = node.NamespaceOrType.ToString();
        if (RemoveUsings.Contains(name))
            return null;
        return base.VisitUsingDirective(node);
    }

    // -----------------------------------------------------------------------
    // Namespace: inject "using AlRunner.Runtime;" after opening brace
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        // First recurse into children
        var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;

        // Add using AlRunner.Runtime if not already present
        bool hasRuntimeUsing = visited.Usings.Any(u =>
            u.NamespaceOrType.ToString() == "AlRunner.Runtime");

        if (!hasRuntimeUsing)
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("AlRunner.Runtime"));
            visited = visited.AddUsings(usingDirective);
        }

        return visited;
    }

    // Also handle file-scoped namespaces
    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;

        bool hasRuntimeUsing = visited.Usings.Any(u =>
            u.NamespaceOrType.ToString() == "AlRunner.Runtime");

        if (!hasRuntimeUsing)
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("AlRunner.Runtime"));
            visited = visited.AddUsings(usingDirective);
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Attribute lists: remove BC-specific attributes
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitAttributeList(AttributeListSyntax node)
    {
        var kept = new SeparatedSyntaxList<AttributeSyntax>();
        foreach (var attr in node.Attributes)
        {
            var attrName = GetSimpleAttributeName(attr);
            if (!BcAttributeNames.Contains(attrName))
                kept = kept.Add(attr);
        }

        if (kept.Count == 0)
            return null; // remove entire attribute list

        if (kept.Count == node.Attributes.Count)
            return base.VisitAttributeList(node); // nothing changed

        return node.WithAttributes(kept);
    }

    private static string GetSimpleAttributeName(AttributeSyntax attr)
    {
        // Extract the last identifier from potentially qualified name
        var name = attr.Name;
        if (name is QualifiedNameSyntax qns)
            return qns.Right.Identifier.Text;
        if (name is IdentifierNameSyntax ins)
            return ins.Identifier.Text;
        return name.ToString();
    }

    // -----------------------------------------------------------------------
    // Class declarations: handle base classes, remove BC members, add _parent field
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        // XmlPort object classes (XmlPortNNNN : NavXmlPort) have complex schema
        // initialization code that cannot compile in standalone mode. Replace the
        // entire class with a minimal stub that extends MockXmlPortHandle.
        if (node.BaseList != null && node.BaseList.Types.Any(
                t => t.Type.ToString() == "NavXmlPort"))
        {
            var stubClass = node
                .WithBaseList(SyntaxFactory.BaseList(
                    SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                        SyntaxFactory.SimpleBaseType(
                            SyntaxFactory.ParseTypeName("AlRunner.Runtime.MockXmlPortHandle")))))
                .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(new[]
                {
                    SyntaxFactory.ParseMemberDeclaration(
                        $"public {node.Identifier.Text}() {{ }}")!
                }))
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>());
            return stubClass;
        }

        // Query object classes (QueryNNNN : NavQuery) reference NCLMetaQuery,
        // data-item handles, and other service-tier infrastructure that cannot
        // compile in standalone mode. Replace the entire class with a minimal
        // stub that extends MockQueryHandle, identical to the XmlPort pattern.
        if (node.BaseList != null && node.BaseList.Types.Any(
                t => t.Type.ToString() == "NavQuery"))
        {
            var stubClass = node
                .WithBaseList(SyntaxFactory.BaseList(
                    SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                        SyntaxFactory.SimpleBaseType(
                            SyntaxFactory.ParseTypeName("AlRunner.Runtime.MockQueryHandle")))))
                .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(new[]
                {
                    SyntaxFactory.ParseMemberDeclaration(
                        $"public {node.Identifier.Text}() {{ }}")!
                }))
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>());
            return stubClass;
        }

        // Detect if this is a scope class BEFORE visiting children.
        // We need to know the enclosing class name for _parent field type.
        bool isScopeClass = false;
        bool isRecordClass = false;
        bool isPageExtensionClass = false;
        bool isPageClass = false;
        string? enclosingClassName = null;
        if (node.BaseList != null)
        {
            foreach (var baseType in node.BaseList.Types)
            {
                var typeText = baseType.Type.ToString();
                if (typeText == "NavRecord" || typeText == "NavRecordExtension")
                    isRecordClass = true;
                if (typeText == "NavFormExtension")
                    isPageExtensionClass = true;
                if (typeText == "NavForm")
                    isPageClass = true;
                if (typeText.StartsWith("NavMethodScope<") || typeText.StartsWith("NavTriggerMethodScope<")
                    || typeText.StartsWith("NavEventMethodScope<"))
                {
                    isScopeClass = true;
                    // Extract the generic type parameter as the enclosing class name
                    // NavMethodScope<Codeunit139771> -> Codeunit139771
                    var ltIdx = typeText.IndexOf('<');
                    var gtIdx = typeText.IndexOf('>');
                    if (ltIdx >= 0 && gtIdx > ltIdx)
                        enclosingClassName = typeText.Substring(ltIdx + 1, gtIdx - ltIdx - 1);
                    break;
                }
            }
        }

        // Track the current class name for Rec/xRec table ID extraction.
        // Only set for top-level (non-nested) classes that are record types.
        var previousClassName = _currentClassName;
        if (isRecordClass)
            _currentClassName = node.Identifier.Text;

        // First, visit children recursively
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;

        // Handle base class list
        if (visited.BaseList != null)
        {
            var newTypes = new SeparatedSyntaxList<BaseTypeSyntax>();
            foreach (var baseType in visited.BaseList.Types)
            {
                var typeText = baseType.Type.ToString();

                if (typeText == "NavCodeunit" || typeText == "NavTestCodeunit" || typeText == "NavRecord"
                    || typeText == "NavFormExtension" || typeText == "NavRecordExtension"
                    || typeText == "NavEventScope" || typeText == "NavUpgradeCodeunit"
                    || typeText == "NavForm")
                {
                    // Remove these base classes entirely
                    continue;
                }

                if (typeText.StartsWith("NavMethodScope<") || typeText.StartsWith("NavTriggerMethodScope<")
                    || typeText.StartsWith("NavEventMethodScope<"))
                {
                    // Replace with AlScope
                    var alScopeType = SyntaxFactory.SimpleBaseType(
                        SyntaxFactory.ParseTypeName("AlScope"));
                    newTypes = newTypes.Add(alScopeType);
                    continue;
                }

                newTypes = newTypes.Add(baseType);
            }

            if (newTypes.Count == 0)
                visited = visited.WithBaseList(null);
            else
                visited = visited.WithBaseList(SyntaxFactory.BaseList(newTypes));
        }

        // Remove specific members
        var membersToKeep = new SyntaxList<MemberDeclarationSyntax>();
        foreach (var member in visited.Members)
        {
            if (ShouldRemoveMember(member, visited))
                continue;
            membersToKeep = membersToKeep.Add(member);
        }

        visited = visited.WithMembers(membersToKeep);

        // For scope classes: add a _parent field of the enclosing class type
        if (isScopeClass && enclosingClassName != null)
        {
            var parentField = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(enclosingClassName))
                .WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("_parent"))))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

            // Insert the _parent field at the beginning
            visited = visited.WithMembers(visited.Members.Insert(0, parentField));
        }

        // For Record classes: add delegating methods that forward to Rec (MockRecordHandle).
        // In the original code, Record2632 : NavRecord, so it inherits GetFieldValueSafe, ALModify, etc.
        // After rewriting, the base class is removed and Rec is a separate MockRecordHandle property.
        // Scope classes reference _parent.GetFieldValueSafe(...) which needs to work on the Record class.
        if (isRecordClass)
        {
            var delegatingCode = @"
public NavValue GetFieldValueSafe(int fieldNo, NavType expectedType) => Rec.GetFieldValueSafe(fieldNo, expectedType);
public NavValue GetFieldValueSafe(int fieldNo, NavType expectedType, bool useLocale) => Rec.GetFieldValueSafe(fieldNo, expectedType, useLocale);
public void SetFieldValueSafe(int fieldNo, NavType expectedType, NavValue value) => Rec.SetFieldValueSafe(fieldNo, expectedType, value);
public void SetFieldValueSafe(int fieldNo, NavType expectedType, NavValue value, bool validate) => Rec.SetFieldValueSafe(fieldNo, expectedType, value, validate);
public NavValue GetFieldRefSafe(int fieldNo, NavType expectedType) => Rec.GetFieldRefSafe(fieldNo, expectedType);
public bool ALInsert(DataError errorLevel) => Rec.ALInsert(errorLevel);
public bool ALInsert(DataError errorLevel, bool runTrigger) => Rec.ALInsert(errorLevel, runTrigger);
public bool ALModify(DataError errorLevel) => Rec.ALModify(errorLevel);
public bool ALModify(DataError errorLevel, bool runTrigger) => Rec.ALModify(errorLevel, runTrigger);
public bool ALGet(DataError errorLevel, params NavValue[] keyValues) => Rec.ALGet(errorLevel, keyValues);
public bool ALFind(DataError errorLevel, string searchMethod = ""-"") => Rec.ALFind(errorLevel, searchMethod);
public bool ALFindSet(DataError errorLevel = DataError.ThrowError, bool forUpdate = false) => Rec.ALFindSet(errorLevel, forUpdate);
public bool ALFindFirst(DataError errorLevel = DataError.ThrowError) => Rec.ALFindFirst(errorLevel);
public bool ALFindLast(DataError errorLevel = DataError.ThrowError) => Rec.ALFindLast(errorLevel);
public int ALNext() => Rec.ALNext();
public bool ALDelete(DataError errorLevel, bool runTrigger = false) => Rec.ALDelete(errorLevel, runTrigger);
public void ALDeleteAll(bool runTrigger) => Rec.ALDeleteAll(runTrigger);
public void ALDeleteAll(DataError errorLevel = DataError.ThrowError, bool runTrigger = false) => Rec.ALDeleteAll(errorLevel, runTrigger);
public void ALInit() => Rec.ALInit();
public void ALReset() => Rec.ALReset();
public void ALSetRange(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue) => Rec.ALSetRange(fieldNo, expectedType, fromValue, toValue);
public void ALSetRangeSafe(int fieldNo, NavType expectedType) => Rec.ALSetRangeSafe(fieldNo, expectedType);
public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue value) => Rec.ALSetRangeSafe(fieldNo, expectedType, value);
public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue) => Rec.ALSetRangeSafe(fieldNo, expectedType, fromValue, toValue);
public void ALSetFilter(int fieldNo, string filterExpression, params NavValue[] args) => Rec.ALSetFilter(fieldNo, filterExpression, args);
public void ALSetFilter(int fieldNo, NavType expectedType, string filterExpression, params NavValue[] args) => Rec.ALSetFilter(fieldNo, expectedType, filterExpression, args);
public void ALCopy(MockRecordHandle source, bool shareFilters = false) => Rec.ALCopy(source, shareFilters);
public void ALCopyFilter(int fromFieldNo, MockRecordHandle target, int toFieldNo) => Rec.ALCopyFilter(fromFieldNo, target, toFieldNo);
public void ALCopyFilters(MockRecordHandle source) => Rec.ALCopyFilters(source);
public void ALValidateSafe(int fieldNo, NavType expectedType, NavValue value) => Rec.ALValidateSafe(fieldNo, expectedType, value);
public void ALValidate(DataError errorLevel, int fieldNo, NavType expectedType, NavValue value) => Rec.ALValidate(errorLevel, fieldNo, expectedType, value);
public void ALTestFieldSafe(int fieldNo, NavType expectedType) => Rec.ALTestFieldSafe(fieldNo, expectedType);
public void ALTestFieldSafe(int fieldNo, NavType expectedType, NavValue expectedValue) => Rec.ALTestFieldSafe(fieldNo, expectedType, expectedValue);
public void ALTestField(DataError errorLevel, int fieldNo, NavType expectedType) => Rec.ALTestField(errorLevel, fieldNo, expectedType);
public void ALTestField(DataError errorLevel, int fieldNo, NavType expectedType, NavValue expectedValue) => Rec.ALTestField(errorLevel, fieldNo, expectedType, expectedValue);
public void ALCalcFields(DataError errorLevel, params int[] fieldNos) => Rec.ALCalcFields(errorLevel, fieldNos);
public bool ALCalcSums(DataError errorLevel, params int[] fieldNos) => Rec.ALCalcSums(errorLevel, fieldNos);
public void ALSetCurrentKey(DataError errorLevel, params int[] fieldNos) => Rec.ALSetCurrentKey(errorLevel, fieldNos);
public void ALSetCurrentKey(params int[] fieldNos) => Rec.ALSetCurrentKey(fieldNos);
public void ALSetAscending(int fieldNo, bool ascending) => Rec.ALSetAscending(fieldNo, ascending);
public int ALCount => Rec.ALCount;
public bool ALIsEmpty => Rec.ALIsEmpty;
public bool ALIsTemporary => Rec.ALIsTemporary;
public string ALTableCaption => Rec.ALTableCaption;
public string ALTableName => Rec.ALTableName;
public int ALFieldNo(string fieldName) => Rec.ALFieldNo(fieldName);
public int ALFieldNo(int fieldNo) => Rec.ALFieldNo(fieldNo);
public NavText ALFieldCaption(int fieldNo) => Rec.ALFieldCaption(fieldNo);
public void ALSetRecFilter() => Rec.ALSetRecFilter();
public void ALLockTable(DataError errorLevel = DataError.ThrowError) => Rec.ALLockTable(errorLevel);
public bool ALRename(DataError errorLevel, params NavValue[] newKeyValues) => Rec.ALRename(errorLevel, newKeyValues);
public void ALSetLoadFields(params int[] fieldNos) => Rec.ALSetLoadFields(fieldNos);
public void ALSetAutoCalcFields(params object[] fields) => Rec.ALSetAutoCalcFields(fields);
public string ALGetFilter() => Rec.ALGetFilter();
public string ALGetFilter(int fieldNo) => Rec.ALGetFilter(fieldNo);
public void ALAssign(MockRecordHandle other) => Rec.ALAssign(other);
public int ALFilterGroup { get => Rec.ALFilterGroup; set => Rec.ALFilterGroup = value; }
public object ALReadIsolation { get => Rec.ALReadIsolation; set => Rec.ALReadIsolation = value; }
public void Clear() => Rec.Clear();
public void ALTransferFields(MockRecordHandle source, bool initPrimaryKey = true) => Rec.ALTransferFields(source, initPrimaryKey);
public void ALMark(bool mark = true) => Rec.ALMark(mark);
public bool ALMarkedOnly { get => Rec.ALMarkedOnly; set => Rec.ALMarkedOnly = value; }
public int CurrFieldNo { get; set; }
public string ALGetFilters() => Rec.ALGetFilters();
public NavValue ALGetRangeMinSafe(int fieldNo, NavType expectedType) => Rec.ALGetRangeMinSafe(fieldNo, expectedType);
public NavValue ALGetRangeMaxSafe(int fieldNo, NavType expectedType) => Rec.ALGetRangeMaxSafe(fieldNo, expectedType);
";
            var delegatingMembers = CSharpSyntaxTree.ParseText(
                $"class _Temp_ {{ {delegatingCode} }}").GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;

            visited = visited.WithMembers(visited.Members.AddRange(delegatingMembers));
        }

        // For page extension classes: inject a ParentObject property returning Rec.
        // Page extensions reference this.ParentObject (after base.ParentObject is rewritten)
        // to access the parent record. Without this, page extensions fail compilation and
        // cascade their exclusion to dependent record types.
        if (isPageExtensionClass)
        {
            var parentObjectCode = @"
public MockRecordHandle ParentObject => Rec;
public MockCurrPage CurrPage { get; } = new MockCurrPage();
";
            var pageMembers = CSharpSyntaxTree.ParseText(
                $"class _Temp_ {{ {parentObjectCode} }}").GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;
            visited = visited.WithMembers(visited.Members.AddRange(pageMembers));
        }

        // Standalone page classes (NavForm → removed base) need CurrPage methods injected
        // because CurrPage is (PageNNNN)this — calling CurrPage.Update() calls Page.Update().
        // Also inject extension-dispatch stubs that NavForm provided as virtual methods.
        if (isPageClass)
        {
            var pageMemberCode = @"
public void Update(bool saveRecord = true) { }
public void Close() { }
public void Activate() { }
public void SaveRecord() { }
public void SetTableView(MockRecordHandle rec) { }
protected bool CallGetDecimalPlacesExtensionMethod(int fieldNo, ref string result) { return false; }
protected bool CallGetTableRelationExtensionMethod(int fieldNo, MockRecordHandle rec, ref bool result) { return false; }
protected bool CallGetFormatExtensionMethod(int fieldNo, ref string result) { return false; }
";
            var pageMembers = CSharpSyntaxTree.ParseText(
                $"class _Temp_ {{ {pageMemberCode} }}").GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;
            visited = visited.WithMembers(visited.Members.AddRange(pageMembers));
        }

        // Restore previous class name context
        _currentClassName = previousClassName;

        return visited;
    }

    private static bool ShouldRemoveMember(MemberDeclarationSyntax member, ClassDeclarationSyntax parentClass)
    {
        // Remove BC constructor: public CodeunitXXX(ITreeObject parent) : base(parent, NNN) { }
        // Remove Record constructor: public RecordXXX(ITreeObject parent, NCLMetaTable ...) : base(...) { }
        if (member is ConstructorDeclarationSyntax ctor)
        {
            var paramText = ctor.ParameterList.ToString();
            if (paramText.Contains("ITreeObject parent"))
                return true;
        }

        // Remove methods
        if (member is MethodDeclarationSyntax method)
        {
            var name = method.Identifier.Text;

            // Remove __Construct
            if (name == "__Construct")
                return true;

            // Remove OnInvoke
            if (name == "OnInvoke" && method.ParameterList.Parameters.Count == 2)
            {
                var firstParam = method.ParameterList.Parameters[0].Type?.ToString();
                var secondParam = method.ParameterList.Parameters[1].Type?.ToString();
                if (firstParam == "int" && secondParam == "object[]")
                    return true;
            }

            // Keep OnRun with parameters (the codeunit's outer OnRun wrapper).
            // It creates the scope and calls Run(), which is needed for Codeunit.Run() dispatch.
            // The override keyword and BC-specific parameter types are handled by other rewriter passes.

            // Remove GetMethodScopeFlags (BC runtime permission checking, irrelevant for standalone)
            if (name == "GetMethodScopeFlags")
                return true;

            // Remove BC interface dispatch methods (used by NavInterfaceHandle runtime)
            if (name == "IsInterfaceOfType" || name == "IsInterfaceMethod")
                return true;

            // Remove Page/Extension-specific methods that reference BC Page runtime
            if (name == "OnMetadataLoaded" || name == "EvaluateCaptionClass"
                || name == "OnEvaluateCaptionClass" || name == "RegisterDynamicCaptionExpression"
                || name == "EnsureGlobalVariablesInitialized" || name == "CallEvaluateCaptionClassExtensionMethod"
                || name == "CallOnMetadataLoadedExtensionMethod"
                || name == "RegisterUIPart")
                return true;

            // Remove Page InitializeComponent that contains NavForm-specific calls.
            // Codeunit InitializeComponent doesn't call these, so this is safe.
            if (name == "InitializeComponent" || name == "InitializeForm")
            {
                var bodyText = method.Body?.ToString() ?? "";
                if (bodyText.Contains("CallInitializeComponentExtensionMethod") ||
                    bodyText.Contains("InitializeForm") ||
                    bodyText.Contains("RegisterUIPart"))
                    return true;
            }
        }

        // Remove properties that cast to NavForm — rewritten CurrPage will be injected below.
        if (member is PropertyDeclarationSyntax propCheck)
        {
            var propText = propCheck.ToString();
            if (propText.Contains("(NavForm)"))
                return true;
        }

        // Remove specific properties
        if (member is PropertyDeclarationSyntax prop)
        {
            var name = prop.Identifier.Text;

            // public override string ObjectName => "...";
            if (name == "ObjectName")
                return true;

            // public override bool IsCompiledForOnPremise => true;
            if (name == "IsCompiledForOnPremise")
                return true;

            // public override bool IsSingleInstance => false;
            if (name == "IsSingleInstance")
                return true;

            // Rec/xRec: Don't remove — rewrite to MockRecordHandle stub in VisitPropertyDeclaration
            // (removed the deletion that was here)

            // protected override uint RawScopeId { get => ...; set => ...; }
            if (name == "RawScopeId")
                return true;

            // private new NavRecord ParentObject => ...;
            // Keep ParentObject for all types that have a Rec property — it's rewritten
            // to return Rec in VisitPropertyDeclaration. This prevents page extensions
            // from failing compilation and cascading exclusions to dependent records.
            if (name == "ParentObject")
            {
                var className = parentClass.Identifier.Text;
                if (className.StartsWith("TableExtension") || className.StartsWith("Record")
                    || className.StartsWith("PageExtension"))
                    return false; // keep — will be rewritten to => Rec
                return true; // remove for codeunits, etc.
            }
            // protected override uint[] IndirectPermissionList => ...;
            if (name == "IndirectPermissionList")
                return true;

            // public override NavEventScope EventScope { get; set; }
            if (name == "EventScope")
                return true;

            // public override int MethodId => N;
            if (name == "MethodId")
                return true;
        }

        // Remove static αscopeId field (Unicode \u03b1 prefix)
        if (member is FieldDeclarationSyntax field)
        {
            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.ValueText;
                var text = variable.Identifier.Text;
                // Match by ValueText, Text, or fallback pattern: any field ending with "scopeId"
                if (name == "\u03b1scopeId" || text == "\u03b1scopeId" ||
                    name.EndsWith("scopeId") || text.EndsWith("scopeId"))
                    return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Method declarations: remove 'override' from OnClear
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        // Remove 'override' keyword from methods whose base class was removed.
        // We strip NavCodeunit/NavRecord/NavFormExtension/NavRecordExtension/NavEventScope,
        // so any 'override' in those classes becomes invalid.
        if (visited.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
        {
            var parentClass = node.Parent as ClassDeclarationSyntax;
            if (parentClass?.BaseList != null)
            {
                bool hadRemovedBase = parentClass.BaseList.Types.Any(t =>
                {
                    var txt = t.Type.ToString();
                    return txt == "NavCodeunit" || txt == "NavTestCodeunit" || txt == "NavRecord"
                        || txt == "NavFormExtension" || txt == "NavRecordExtension"
                        || txt == "NavEventScope" || txt == "NavUpgradeCodeunit"
                        || txt == "NavForm";
                });
                if (hadRemovedBase)
                {
                    var newModifiers = SyntaxFactory.TokenList(
                        visited.Modifiers.Where(m => !m.IsKind(SyntaxKind.OverrideKeyword)));
                    visited = visited.WithModifiers(newModifiers);
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Property declarations: rewrite Rec/xRec to MockRecordHandle stubs
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var name = node.Identifier.Text;

        // Rewrite Rec/xRec properties to return a MockRecordHandle with the correct table ID.
        // Original: private NavRecord Rec => (NavRecord)this.SourceTable;
        // Original: private RecordXXX Rec => (RecordXXX)this;
        // Rewritten: public MockRecordHandle Rec { get; } = new MockRecordHandle(tableId);
        // The table ID is extracted from the enclosing class name (e.g. Record74320 -> 74320).
        if (name == "Rec" || name == "xRec")
        {
            // Extract table ID from enclosing class name (Record74320 -> 74320)
            int tableId = 0;
            if (_currentClassName != null && _currentClassName.StartsWith("Record"))
            {
                int.TryParse(_currentClassName.Substring("Record".Length), out tableId);
            }

            // Parse a simple auto-property with a default value
            var stubProp = SyntaxFactory.PropertyDeclaration(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.Identifier(name))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(SyntaxFactory.AccessorList(
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
                .WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.ParseExpression($"new MockRecordHandle({tableId})")))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return stubProp;
        }

        // Rewrite ParentObject to return Rec for table extensions and records.
        // Original: private new NavRecord ParentObject => (NavRecord)base.ParentObject;
        // Rewritten: public MockRecordHandle ParentObject => Rec;
        if (name == "ParentObject")
        {
            var stubProp = SyntaxFactory.PropertyDeclaration(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.Identifier("ParentObject"))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.IdentifierName("Rec")))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return stubProp;
        }

        return base.VisitPropertyDeclaration(node);
    }

    // -----------------------------------------------------------------------
    // Constructor declarations: handle scope ctors with _parent field
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;

        // For scope constructors (internal constructors in nested classes that inherit from AlScope),
        // the base class has been replaced with AlScope which has a parameterless constructor.
        // Remove ALL base(...) initializers from these constructors.
        if (visited.Initializer != null && visited.Initializer.Kind() == SyntaxKind.BaseConstructorInitializer)
        {
            // Check if this is a scope constructor by looking for parent or βparent references,
            // or simply any base() call on an internal constructor (scope constructors are internal).
            var isInternal = visited.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
            var hasParentArg = visited.Initializer.ArgumentList.Arguments
                .Any(a => a.Expression is IdentifierNameSyntax id &&
                    (id.Identifier.ValueText.Contains("parent") || id.Identifier.Text.Contains("parent")));

            if (hasParentArg || isInternal)
            {
                visited = visited.WithInitializer(null);
            }
        }

        // Handle βparent parameter: KEEP it but add _parent assignment to constructor body.
        // Instead of removing the parameter, we keep it and add: this._parent = βparent;
        if (visited.ParameterList.Parameters.Count > 0)
        {
            var firstParam = visited.ParameterList.Parameters[0];
            var paramName = firstParam.Identifier.ValueText;
            if (paramName.Contains("parent") || firstParam.Identifier.Text.Contains("parent"))
            {
                var typeText = firstParam.Type?.ToString() ?? "";
                if (typeText.StartsWith("Codeunit") || typeText.StartsWith("Record") || typeText.StartsWith("Page")
                    || typeText.StartsWith("Query") || typeText.StartsWith("Report") || typeText.StartsWith("XmlPort")
                    || typeText.StartsWith("TableExtension") || typeText.StartsWith("PageExtension"))
                {
                    // Keep the parameter, but add _parent assignment at the start of the body
                    var paramIdentifier = firstParam.Identifier.Text;
                    var assignmentStatement = SyntaxFactory.ParseStatement(
                        $"this._parent = {paramIdentifier};");

                    if (visited.Body != null)
                    {
                        var newStatements = visited.Body.Statements.Insert(0, assignmentStatement);
                        visited = visited.WithBody(visited.Body.WithStatements(newStatements));
                    }
                    else if (visited.ExpressionBody != null)
                    {
                        // Convert expression body to block body with assignment + expression
                        var exprStatement = SyntaxFactory.ExpressionStatement(visited.ExpressionBody.Expression);
                        var body = SyntaxFactory.Block(assignmentStatement, exprStatement);
                        visited = visited.WithExpressionBody(null)
                            .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken))
                            .WithBody(body);
                    }

                    // Do NOT remove the parameter - keep it so callers can pass 'this'
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Identifier names: type replacements
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var text = node.Identifier.Text;

        // INavRecordHandle -> MockRecordHandle
        if (text == "INavRecordHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordHandle"));

        // NavRecordHandle -> MockRecordHandle (used in new NavRecordHandle(...))
        if (text == "NavRecordHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordHandle"));

        // NavCodeunitHandle -> MockCodeunitHandle
        if (text == "NavCodeunitHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockCodeunitHandle"));

        // NavInterfaceHandle -> MockInterfaceHandle
        if (text == "NavInterfaceHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockInterfaceHandle"));

        // ALNumberSequence -> MockNumberSequence
        // The real type's ALExists/ALNext/ALRestart go through NavSession,
        // which is null under standalone mode and throws NullReferenceException.
        if (text == "ALNumberSequence")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockNumberSequence"));

        // ALNavApp -> MockNavApp
        // The real type's ALGetModuleInfo reaches into
        // Microsoft.Dynamics.Nav.CodeAnalysis, which isn't shipped with
        // al-runner — any NavApp.GetModuleInfo/GetCurrentModuleInfo call
        // crashes with an assembly-load failure under standalone mode.
        if (text == "ALNavApp")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockNavApp"));

        // ALSystemOperatingSystem -> MockSystemOperatingSystem
        // The real type's ALHyperlink dispatches through NavSession to
        // open a URL in the client — NullReferenceException under
        // standalone mode. Mock is a no-op.
        if (text == "ALSystemOperatingSystem")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockSystemOperatingSystem"));

        // NavFormHandle -> MockFormHandle
        // BC emits `Page "X"` AL variables as `NavFormHandle p` fields with
        // `new NavFormHandle(this, pageId)` initializers — both args would
        // need an ITreeObject and a real NavForm which standalone mode lacks.
        if (text == "NavFormHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockFormHandle"));

        // NavTestPageHandle -> MockTestPageHandle
        // BC emits `TestPage "X"` AL variables as `NavTestPageHandle tP` fields with
        // `new NavTestPageHandle(this, pageId)` initializers. The real ctor wants
        // ITreeObject which standalone mode lacks.
        if (text == "NavTestPageHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockTestPageHandle"));

        // NavXmlPortHandle -> MockXmlPortHandle
        // BC emits `XmlPort "X"` AL variables as `NavXmlPortHandle xP` fields with
        // `new NavXmlPortHandle(this, xmlPortId)` initializers. The real ctor wants
        // ITreeObject which standalone mode lacks.  After the existing .Target-stripping
        // rewrite, members like Source, Destination, Import(), Export() are called
        // directly on the handle — MockXmlPortHandle satisfies those.
        if (text == "NavXmlPortHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockXmlPortHandle"));

        // NavQueryHandle -> MockQueryHandle
        // BC emits `Query "X"` AL variables as `NavQueryHandle q` fields with
        // `new NavQueryHandle(this, queryId, SecurityFiltering)` initializers.
        // The real ctor wants ITreeObject which standalone mode lacks.  After
        // the existing .Target-stripping rewrite, query API calls (ALOpen,
        // ALRead, ALSetFilter, etc.) are called directly on the handle.
        if (text == "NavQueryHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockQueryHandle"));

        // NavRecordRef -> MockRecordRef
        // NavRecordRef's real ctor wants ITreeObject; MockRecordRef has a
        // parameterless ctor and stub methods so the AL declaration compiles.
        if (text == "NavRecordRef")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordRef"));

        // NavFieldRef -> MockFieldRef
        // NavFieldRef's real ctor wants ITreeObject; MockFieldRef has a
        // parameterless ctor with ALValue/ALNumber/ALAssign/ALField support.
        if (text == "NavFieldRef")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockFieldRef"));

        // NavBLOB -> MockBlob
        // NavBLOB's ALCreateInStream/ALCreateOutStream pass ITreeObject to
        // NavStream ctor which crashes with null in standalone mode.
        // MockBlob is a NavValue subclass with in-memory byte[] storage.
        if (text == "NavBLOB")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockBlob"));

        // NavInStream -> MockInStream
        // NavInStream's ctor requires ITreeObject; MockInStream is standalone.
        if (text == "NavInStream")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockInStream"));

        // NavOutStream -> MockOutStream
        // NavOutStream's ctor requires ITreeObject; MockOutStream is standalone.
        if (text == "NavOutStream")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockOutStream"));

        // ALStream -> MockStream
        // ALStream's static methods operate on INavStreamReader/INavStreamWriter
        // which require session infrastructure. MockStream works with MockInStream/MockOutStream.
        if (text == "ALStream")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockStream"));

        // NavComplexValue -> object
        // The BC compiler uses NavComplexValue as a base type for complex
        // value parameters (RecordRef, Variant, etc.). MockVariant and
        // MockRecordRef don't extend the BC-internal NavComplexValue class,
        // so we replace it with object to allow any mock type to be passed.
        if (text == "NavComplexValue")
            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))
                .WithTriviaFrom(node);

        // NavScope -> object
        // The BC compiler adds a hidden NavScope γReturnValueParent parameter
        // to methods that return a Record or Interface. The parameter is used
        // for ownership tracking. After rewriting, scope classes extend AlScope
        // (not NavScope), so direct same-codeunit calls fail with CS1503. We
        // replace NavScope with object so any scope or null can be passed.
        if (text == "NavScope")
            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))
                .WithTriviaFrom(node);

        // NavVariant -> MockVariant (Variant in AL needs Default/ALAssign methods)
        if (text == "NavVariant")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockVariant"));

        // NavTextConstant -> NavText (avoid BC runtime initialization)
        if (text == "NavTextConstant")
            return node.WithIdentifier(SyntaxFactory.Identifier("NavText"));

        // NavDialog (instance type) -> MockDialog (for dialog/progress window objects)
        // Note: NavDialog static calls (ALMessage, ALError) are handled in VisitInvocationExpression
        if (text == "NavDialog")
        {
            // Check if this is in a type context (field/variable declaration, generic arg, etc.)
            // vs a static member access (NavDialog.ALMessage) which should stay as "NavDialog"
            var parent = node.Parent;
            if (parent is MemberAccessExpressionSyntax ma && ma.Expression == node)
            {
                // NavDialog.ALMessage -- keep as-is, handled by invocation rewriter
                // But for NavDialog.ALError, NavDialog.ALMessage these go through AlDialog
                // Actually check: is this a static method call?
                var methodName = ma.Name.Identifier.Text;
                if (methodName == "ALMessage" || methodName == "ALError")
                    return base.VisitIdentifierName(node); // Keep NavDialog for static rewrite
            }
            return node.WithIdentifier(SyntaxFactory.Identifier("MockDialog"));
        }

        // NavTextBuilder -> MockTextBuilder (avoids NavEnvironment/TrappableOperationExecutor crashes)
        if (text == "NavTextBuilder")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockTextBuilder"));

        // NavEventScope -> object (event scope type used for static fields)
        // Use PredefinedType to emit the C# keyword "object" properly, avoiding
        // namespace resolution issues where "object" as an IdentifierName fails.
        if (text == "NavEventScope")
            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));

        return base.VisitIdentifierName(node);
    }

    // -----------------------------------------------------------------------
    // Generic names: NavArray<MockRecordHandle> -> MockRecordArray
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var visited = (GenericNameSyntax)base.VisitGenericName(node)!;

        // NavArray<MockRecordHandle> -> MockRecordArray
        // NavArray<INavRecordHandle> -> MockRecordArray (INavRecordHandle already rewritten to MockRecordHandle)
        if (visited.Identifier.Text == "NavArray" &&
            visited.TypeArgumentList.Arguments.Count == 1)
        {
            var typeArg = visited.TypeArgumentList.Arguments[0].ToString();
            if (typeArg == "MockRecordHandle")
            {
                return SyntaxFactory.IdentifierName("MockRecordArray");
            }

            // NavArray<T> -> MockArray<T> for ALL types
            // NavArray requires ITreeObject (validates parent != null even in the simple ctor).
            // Use our MockArray<T> which provides 0-based indexing without ITreeObject.
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("MockArray"),
                visited.TypeArgumentList);
        }

        // NavObjectList<T> -> MockObjectList<T>
        // BC emits `List of [Interface X]` as NavObjectList<NavInterfaceHandle>, which
        // demands `T : ITreeObject` with a valid Tree handler — unavailable standalone.
        // MockObjectList<T> exposes the subset of the API the transpiler calls.
        if (visited.Identifier.Text == "NavObjectList" &&
            visited.TypeArgumentList.Arguments.Count == 1)
        {
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("MockObjectList"),
                visited.TypeArgumentList);
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Object creation expressions
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        var typeText = visited.Type.ToString();

        // new NavRecordHandle(this, NNN, false, SecurityFiltering.XXX) -> new MockRecordHandle(NNN)
        // After identifier replacement, this is already MockRecordHandle
        if (typeText == "MockRecordHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 4)
        {
            // The second argument is the table ID
            var tableIdArg = visited.ArgumentList.Arguments[1];
            var newArgs = SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(tableIdArg.Expression)));
            return visited.WithArgumentList(newArgs);
        }

        // new MockCodeunitHandle(this, NNN) -> MockCodeunitHandle.Create(NNN)
        if (typeText == "MockCodeunitHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var codeunitId = visited.ArgumentList.Arguments[1].Expression;
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                        SyntaxFactory.IdentifierName("Create")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(codeunitId))));
            }
        }

        // new MockInterfaceHandle(this) -> new MockInterfaceHandle()
        // Strip the 'this' arg since MockInterfaceHandle doesn't need ITreeObject
        if (typeText == "MockInterfaceHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockFormHandle(this, pageId) -> new MockFormHandle(pageId)
        // Preserve the page id so the runtime can dispatch helper-procedure
        // calls on the Page<N> class via reflection.
        if (typeText == "MockFormHandle" && visited.ArgumentList != null)
        {
            if (visited.ArgumentList.Arguments.Count == 2)
            {
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.ArgumentList.Arguments[1])));
            }
            if (visited.ArgumentList.Arguments.Count == 1)
            {
                // Single-arg `new MockFormHandle(this)` (no page id known).
                // Fall back to parameterless ctor.
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockTestPageHandle(this, pageId) -> new MockTestPageHandle(pageId)
        // Same pattern as MockFormHandle: strip ITreeObject 'this', keep page ID.
        if (typeText == "MockTestPageHandle" && visited.ArgumentList != null)
        {
            if (visited.ArgumentList.Arguments.Count == 2)
            {
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.ArgumentList.Arguments[1])));
            }
            if (visited.ArgumentList.Arguments.Count == 1)
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockXmlPortHandle(this, xmlPortId) -> new MockXmlPortHandle(xmlPortId)
        // BC emits `new NavXmlPortHandle(this, id)` in scope-class field initializers.
        // Strip ITreeObject 'this', keep the XmlPort ID so the mock knows which port it is.
        if (typeText == "MockXmlPortHandle" && visited.ArgumentList != null)
        {
            if (visited.ArgumentList.Arguments.Count == 2)
            {
                // BC emits new NavXmlPortHandle(this, xmlPortId) — strip ITreeObject 'this', keep ID.
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.ArgumentList.Arguments[1])));
            }
            if (visited.ArgumentList.Arguments.Count == 1 &&
                visited.ArgumentList.Arguments[0].Expression.ToString() == "this")
            {
                // Single-arg form with 'this' (no ID) — strip the ITreeObject parent.
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockQueryHandle(this, queryId, SecurityFiltering) -> new MockQueryHandle(queryId, SecurityFiltering)
        // BC emits `new NavQueryHandle(this, queryId, SecurityFiltering.Filtered)` in scope-class
        // field initializers. Strip ITreeObject 'this', keep query ID and SecurityFiltering.
        if (typeText == "MockQueryHandle" && visited.ArgumentList != null)
        {
            if (visited.ArgumentList.Arguments.Count == 3)
            {
                // 3 args: (this, queryId, SecurityFiltering) → keep args [1] and [2].
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(new[]
                        {
                            visited.ArgumentList.Arguments[1],
                            visited.ArgumentList.Arguments[2]
                        })));
            }
            if (visited.ArgumentList.Arguments.Count == 2 &&
                visited.ArgumentList.Arguments[0].Expression.ToString() == "this")
            {
                // 2 args: (this, queryId) → keep arg [1].
                return visited.WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.ArgumentList.Arguments[1])));
            }
            if (visited.ArgumentList.Arguments.Count == 1 &&
                visited.ArgumentList.Arguments[0].Expression.ToString() == "this")
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockRecordRef(this, ...) -> new MockRecordRef()
        // BC emits `new NavRecordRef(this, SecurityFiltering.Validated)` (or
        // variants with table id / company / temp flag) at scope-class init time.
        // The stub has no ITreeObject dependency — strip all constructor args.
        if (typeText == "MockRecordRef")
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new MockObjectList<T>(this) -> new MockObjectList<T>()
        // After VisitGenericName, NavObjectList has already been renamed.
        // MockObjectList doesn't need the ITreeObject parent.
        if (typeText.StartsWith("MockObjectList<") && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new MockDialog(this) -> new MockDialog()
        // After identifier replacement, NavDialog is now MockDialog.
        // Strip the ITreeObject 'this' argument.
        if (typeText == "MockDialog" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new NavCode(maxLen, value) -> AlCompat.CreateNavCode(maxLen, value)
        // NavCode constructor calls EnsureValueIsUppercasedIfNeeded() which triggers NavEnvironment on Linux.
        // AlCompat.CreateNavCode pre-uppercases the string to avoid NavEnvironment access.
        if (typeText == "NavCode" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var maxLenArg = visited.ArgumentList.Arguments[0].Expression;
            var valueArg = visited.ArgumentList.Arguments[1].Expression;
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("CreateNavCode")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(new[] {
                        SyntaxFactory.Argument(maxLenArg),
                        SyntaxFactory.Argument(valueArg)
                    })));
        }

        // new NavTextConstant(langIds, strings, null, null) -> new NavText(strings[0])
        // After VisitIdentifierName, NavTextConstant is already renamed to NavText
        // NavTextConstant triggers NavEnvironment initialization; replace with simple NavText
        if ((typeText == "NavTextConstant" || typeText == "NavText") && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 4)
        {
            // The second argument is the string array: new string[] { "the text" }
            var stringsArg = visited.ArgumentList.Arguments[1].Expression;
            if (stringsArg is ImplicitArrayCreationExpressionSyntax implArr && implArr.Initializer.Expressions.Count > 0)
            {
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("NavText"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(implArr.Initializer.Expressions[0]))));
            }
            if (stringsArg is ArrayCreationExpressionSyntax arrCreate && arrCreate.Initializer != null && arrCreate.Initializer.Expressions.Count > 0)
            {
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("NavText"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(arrCreate.Initializer.Expressions[0]))));
            }
        }

        // new MockFieldRef(this) -> new MockFieldRef()
        // After VisitIdentifierName, NavFieldRef is now MockFieldRef.
        // Strip the ITreeObject 'this' argument.
        if (typeText == "MockFieldRef" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new NavArray<MockRecordHandle>(new MockRecordHandle.Factory2(this, tableId, false, SecurityFiltering.X), N)
        // -> new MockRecordArray(tableId, N)
        // Also matches after GenericName rewrite: new MockRecordArray(new MockRecordHandle.Factory2(...), N)
        // NavArray<T> requires IFactory<T> which is internal; use our MockRecordArray instead
        if ((typeText.StartsWith("NavArray<") || typeText == "MockRecordArray") &&
            visited.ArgumentList != null && visited.ArgumentList.Arguments.Count == 2)
        {
            var factoryArg = visited.ArgumentList.Arguments[0].Expression;
            var sizeArg = visited.ArgumentList.Arguments[1].Expression;

            // Extract the table ID from the Factory2 constructor call
            if (factoryArg is ObjectCreationExpressionSyntax factoryCreation &&
                factoryCreation.Type.ToString().Contains("Factory2") &&
                factoryCreation.ArgumentList != null)
            {
                // Factory2(this, tableId, false, SecurityFiltering.X) — tableId is arg[1]
                // or Factory2(tableId, false, SecurityFiltering.X) — tableId is arg[0]
                var factoryArgs = factoryCreation.ArgumentList.Arguments;
                ExpressionSyntax? tableIdExpr = null;
                if (factoryArgs.Count >= 4)
                    tableIdExpr = factoryArgs[1].Expression; // (this, tableId, ...)
                else if (factoryArgs.Count >= 1)
                    tableIdExpr = factoryArgs[0].Expression; // (tableId, ...)

                if (tableIdExpr != null)
                {
                    return SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("MockRecordArray"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList(new[] {
                                SyntaxFactory.Argument(tableIdExpr),
                                SyntaxFactory.Argument(sizeArg)
                            })));
                }
            }
        }

        // new MockArray<MockVariant>(new NavVariant.Factory(...), N) -> new MockArray<MockVariant>(N, () => new MockVariant())
        // NavArray with Factory pattern: replace Factory-based construction with lambda-based MockArray
        if (typeText.StartsWith("MockArray<MockVariant>") && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var factoryArg = visited.ArgumentList.Arguments[0].Expression;
            var sizeArg = visited.ArgumentList.Arguments[1].Expression;
            if (factoryArg is ObjectCreationExpressionSyntax)
            {
                // new MockArray<MockVariant>(size, () => new MockVariant())
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("MockArray<MockVariant>"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(new[] {
                            SyntaxFactory.Argument(sizeArg),
                            SyntaxFactory.Argument(
                                SyntaxFactory.ParenthesizedLambdaExpression(
                                    SyntaxFactory.ObjectCreationExpression(
                                        SyntaxFactory.ParseTypeName("MockVariant"))
                                        .WithArgumentList(SyntaxFactory.ArgumentList())))
                        })));
            }
        }

        // Catch any remaining MockVariant.Factory or NavVariant.Factory construction
        if (typeText.Contains("MockVariant") && typeText.Contains("Factory") && visited.ArgumentList != null)
        {
            // This shouldn't be reached anymore but kept as safety net
            return SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName("NavVariant.Factory"))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("MockVariant"),
                                SyntaxFactory.IdentifierName("StubTreeObject"))))));
        }

        // new NavArray<T>(this, defaultValue, size) -> new MockArray<T>(defaultValue, size)
        // Drop the ITreeObject 'this' argument from NavArray/MockArray constructors.
        // After generic name rewrite, NavArray<T> becomes MockArray<T>.
        // MockArray<T>(T initValue, params int[] dimensions) matches the remaining args.
        if ((typeText.StartsWith("MockArray") || typeText.StartsWith("NavArray")) &&
            visited.ArgumentList != null && visited.ArgumentList.Arguments.Count >= 3)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var newArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                for (int i = 1; i < visited.ArgumentList.Arguments.Count; i++)
                    newArgs = newArgs.Add(visited.ArgumentList.Arguments[i]);
                return visited.WithArgumentList(SyntaxFactory.ArgumentList(newArgs));
            }
        }

        // NOTE: We no longer strip 'this' from scope constructor calls.
        // Scope constructors now keep the βparent parameter and store it as _parent.
        // AlScope implements ITreeObject, so 'this' is a valid non-null ITreeObject
        // for any Nav*/AL* type constructor — no CS1503 and no null-check failures.

        return visited;
    }

    // -----------------------------------------------------------------------
    // Invocation expressions: NavDialog, StmtHit, CStmtHit, NavRuntimeHelpers, ALCompiler
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Keep StmtHit(N) and CStmtHit(N) calls for coverage tracking.
        // They call through to AlScope which tracks hit statements.
        if (node.Expression is IdentifierNameSyntax stmtIdent &&
            (stmtIdent.Identifier.Text == "StmtHit" || stmtIdent.Identifier.Text == "CStmtHit"))
        {
            return base.VisitInvocationExpression(node);
        }

        // `NavOption.Create(existing.NavOptionMetadata, V)` — reassignment
        // pattern BC emits when an AL enum variable is re-assigned. Route
        // through AlCompat.CloneTaggedOption so the new instance inherits
        // the source enum-id tag.
        if (node.ArgumentList.Arguments.Count == 2 &&
            node.Expression is MemberAccessExpressionSyntax cloneCallMa &&
            cloneCallMa.Expression is IdentifierNameSyntax cloneCallIdent &&
            cloneCallIdent.Identifier.Text == "NavOption" &&
            cloneCallMa.Name.Identifier.Text == "Create" &&
            node.ArgumentList.Arguments[0].Expression is MemberAccessExpressionSyntax srcMetaMa &&
            srcMetaMa.Name.Identifier.Text == "NavOptionMetadata")
        {
            // Visit the captured sub-expressions so `.Target` strips and
            // other nested rewrites land on them before we embed them in
            // the outer helper call. Without this, an expression like
            // `this.r.Target.GetFieldValueSafe(...)` survives with the
            // `.Target` still attached and Roslyn rejects it later.
            var existingExpr = (ExpressionSyntax)Visit(srcMetaMa.Expression)!;
            var ordinalExpr = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[1].Expression)!;
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("CloneTaggedOption")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(existingExpr),
                    SyntaxFactory.Argument(ordinalExpr)
                })));
        }

        // Special-case: `NavOption.Create(NCLEnumMetadata.Create(N), V)`
        // must be rewritten to `AlCompat.CreateTaggedOption(N, V)` so the
        // NavOption instance remembers its source enum — later calls to
        // `.ALOrdinals` / `.ALNames` can then resolve it via the tagged
        // ConditionalWeakTable. Match on the pre-visit tree because the
        // inner NCLEnumMetadata.Create rewrite would otherwise erase N.
        if (node.ArgumentList.Arguments.Count == 2 &&
            node.Expression is MemberAccessExpressionSyntax optCreateMa &&
            optCreateMa.Expression is IdentifierNameSyntax optIdent &&
            optIdent.Identifier.Text == "NavOption" &&
            optCreateMa.Name.Identifier.Text == "Create" &&
            node.ArgumentList.Arguments[0].Expression is InvocationExpressionSyntax optMetaInv &&
            optMetaInv.Expression is MemberAccessExpressionSyntax optMetaMa &&
            optMetaMa.Expression is IdentifierNameSyntax optMetaIdent &&
            optMetaIdent.Identifier.Text == "NCLEnumMetadata" &&
            optMetaMa.Name.Identifier.Text == "Create" &&
            optMetaInv.ArgumentList.Arguments.Count == 1)
        {
            var enumIdArg = (ExpressionSyntax)Visit(optMetaInv.ArgumentList.Arguments[0].Expression)!;
            var ordinalArg = (ExpressionSyntax)Visit(node.ArgumentList.Arguments[1].Expression)!;
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("CreateTaggedOption")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(enumIdArg),
                    SyntaxFactory.Argument(ordinalArg)
                })));
        }

        // Special-case: `NCLEnumMetadata.Create(N).GetOrdinals()` / `.Names()`
        // must be intercepted BEFORE recursing into children, because the
        // inner NCLEnumMetadata.Create rewrite would otherwise erase the
        // enum object ID N. Handle the original (pre-visit) pattern here.
        if (node.ArgumentList.Arguments.Count == 0 &&
            node.Expression is MemberAccessExpressionSyntax outerMa &&
            outerMa.Expression is InvocationExpressionSyntax innerInv &&
            innerInv.Expression is MemberAccessExpressionSyntax innerMa &&
            innerMa.Expression is IdentifierNameSyntax innerIdent &&
            innerIdent.Identifier.Text == "NCLEnumMetadata" &&
            innerMa.Name.Identifier.Text == "Create" &&
            innerInv.ArgumentList.Arguments.Count == 1)
        {
            var enumIdArg = innerInv.ArgumentList.Arguments[0].Expression;
            var outerMethod = outerMa.Name.Identifier.Text;
            string? helper = outerMethod switch
            {
                "GetOrdinals" => "GetEnumOrdinals",
                "GetNames" => "GetEnumNames",
                _ => null
            };
            if (helper is not null)
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName(helper)),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(enumIdArg))));
            }
        }

        // Now recurse into children first
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        // NavText?.ToLowerInvariant() / ?.ToUpperInvariant() -> NavText?.ToString().ToLowerInvariant()
        // NavText implicitly converts to ReadOnlySpan<char>, which picks up the wrong
        // MemoryExtensions.ToLowerInvariant(ReadOnlySpan, Span) overload. Insert .ToString()
        // to force string.ToLowerInvariant() resolution.
        if (visited.Expression is MemberBindingExpressionSyntax memberBinding &&
            visited.ArgumentList.Arguments.Count == 0 &&
            (memberBinding.Name.Identifier.Text == "ToLowerInvariant" ||
             memberBinding.Name.Identifier.Text == "ToUpperInvariant"))
        {
            // x?.ToLowerInvariant() -> x?.ToString().ToLowerInvariant()
            var toStringCall = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberBindingExpression(SyntaxFactory.IdentifierName("ToString")),
                SyntaxFactory.ArgumentList());
            return visited.WithExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    toStringCall,
                    memberBinding.Name));
        }

        // x.ToLowerInvariant() / x.ToUpperInvariant() (non-conditional) -> x.ToString().ToLowerInvariant()
        if (visited.Expression is MemberAccessExpressionSyntax maToLower &&
            visited.ArgumentList.Arguments.Count == 0 &&
            (maToLower.Name.Identifier.Text == "ToLowerInvariant" ||
             maToLower.Name.Identifier.Text == "ToUpperInvariant"))
        {
            var toStringCall = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    maToLower.Expression,
                    SyntaxFactory.IdentifierName("ToString")),
                SyntaxFactory.ArgumentList());
            return visited.WithExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    toStringCall,
                    maToLower.Name));
        }

        // NavDialog.ALMessage(this.Session, System.Guid.Parse("..."), fmt, args...)
        // -> AlDialog.Message(fmt, args...)
        if (visited.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var exprText = memberAccess.Expression.ToString();
            var methodName = memberAccess.Name.Identifier.Text;

            // NavDialog.ALConfirm(session, guid, question, [default]) -> MockDialog.ALConfirm(question, [default])
            if ((exprText == "NavDialog" || exprText == "MockDialog") && methodName == "ALConfirm")
            {
                var args = visited.ArgumentList.Arguments;
                // Skip first two args (Session and Guid)
                if (args.Count >= 3)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 2; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);

                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockDialog"),
                            SyntaxFactory.IdentifierName("ALConfirm")),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavDialog.ALMessage / NavDialog.ALError
            if (exprText == "NavDialog" && (methodName == "ALMessage" || methodName == "ALError"))
            {
                var newMethodName = methodName == "ALMessage" ? "Message" : "Error";
                var args = visited.ArgumentList.Arguments;

                // Skip first two args (this.Session and System.Guid.Parse("..."))
                if (args.Count >= 2)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 2; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);

                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlDialog"),
                            SyntaxFactory.IdentifierName(newMethodName)),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavRuntimeHelpers.CompilationError(...)
            if (exprText == "NavRuntimeHelpers" && methodName == "CompilationError")
            {
                // Replace with: throw new InvalidOperationException("Compilation error")
                // But this is an expression, we need to return an invocation.
                // The throw will be handled in VisitExpressionStatement.
                // Mark it for statement-level replacement by keeping it recognizable.
                return visited;
            }

            // NavCodeunit.RunCodeunit(DataError, id, record) -> MockCodeunitHandle.RunCodeunit(errorLevel, id)
            // NavCodeunit.RunCodeunit is a static dispatch method requiring NavSession
            if (exprText == "NavCodeunit" && methodName == "RunCodeunit")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                {
                    // First argument is DataError (TrapError or ThrowError)
                    var errorLevelArg = args[0].Expression;
                    // The second argument is the codeunit ID
                    var codeunitIdArg = args[1].Expression;
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                            SyntaxFactory.IdentifierName("RunCodeunit")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList<ArgumentSyntax>(new[] {
                                SyntaxFactory.Argument(errorLevelArg),
                                SyntaxFactory.Argument(codeunitIdArg)
                            })));
                }
            }

            // NavXmlPort.Import(DataError, xmlPortId, stream [, rec]) -> MockXmlPortHandle.StaticImport(...)
            // NavXmlPort.Export(DataError, xmlPortId, stream [, rec]) -> MockXmlPortHandle.StaticExport(...)
            // BC emits these static calls for the short form: XmlPort.Import(XmlPort::"X", InStr).
            // NavXmlPort requires the service tier; forward to stub that throws NotSupportedException.
            if (exprText == "NavXmlPort" && (methodName == "Import" || methodName == "Export"))
            {
                var stubMethod = methodName == "Import" ? "StaticImport" : "StaticExport";
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockXmlPortHandle"),
                        SyntaxFactory.IdentifierName(stubMethod)),
                    visited.ArgumentList);
            }

            // NavQuery.ALSaveAsCsv/ALSaveAsXml/ALSaveAsJson/ALSaveAsExcel -> MockQueryHandle statics
            // BC emits these static calls for Query.SaveAsCsv(queryId, ...) etc.
            // NavQuery requires the service tier; forward to MockQueryHandle stubs.
            if (exprText == "NavQuery" && (methodName == "ALSaveAsCsv" || methodName == "ALSaveAsXml"
                || methodName == "ALSaveAsJson" || methodName == "ALSaveAsExcel"))
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockQueryHandle"),
                        SyntaxFactory.IdentifierName(methodName)),
                    visited.ArgumentList);
            }

            // ALIsolatedStorage.ALSet/ALGet/ALContains/ALDelete -> MockIsolatedStorage
            if (exprText == "ALIsolatedStorage" &&
                (methodName == "ALSet" || methodName == "ALGet" || methodName == "ALContains" || methodName == "ALDelete"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockIsolatedStorage"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // NavJsonToken/NavJsonObject/NavJsonArray/NavJsonValue: ALWriteTo, ALReadFrom,
            // ALSelectToken, ALSelectTokens -> MockJsonHelper static methods.
            // These BC methods go through TrappableOperationExecutor -> NavEnvironment
            // which crashes in standalone mode. MockJsonHelper does the same work
            // using Newtonsoft.Json directly.
            if (methodName is "ALWriteTo" or "ALReadFrom" or "ALSelectToken" or "ALSelectTokens")
            {
                var helperMethod = methodName switch
                {
                    "ALWriteTo" => "WriteTo",
                    "ALReadFrom" => "ReadFrom",
                    "ALSelectToken" => "SelectToken",
                    "ALSelectTokens" => "SelectTokens",
                    _ => null
                };
                if (helperMethod is not null)
                {
                    // Rewrite: x.ALWriteTo(args) -> MockJsonHelper.WriteTo(x, args)
                    var args = visited.ArgumentList.Arguments;
                    var newArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    newArgs = newArgs.Add(SyntaxFactory.Argument(memberAccess.Expression));
                    foreach (var arg in args)
                        newArgs = newArgs.Add(arg);

                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockJsonHelper"),
                            SyntaxFactory.IdentifierName(helperMethod)),
                        SyntaxFactory.ArgumentList(newArgs));
                }
            }

            // NavForm.Run(pageId, record) -> no-op (page navigation not supported standalone)
            // NavForm.RunModal(bool, bool, pageId, record) -> FormResult.LookupOK
            // Accept the fully-qualified form too — older BC versions emit
            // `Microsoft.Dynamics.Nav.Runtime.NavForm.Run(...)`.
            if ((exprText == "NavForm" || exprText.EndsWith(".NavForm", StringComparison.Ordinal)) &&
                (methodName == "Run" || methodName == "RunModal"))
            {
                // RunModal returns FormResult, but the enum is from Nav.Ncl. Return a constant.
                // FormResult.LookupOK = used in transpiled code for dialog lookups
                // For Run (void), we can't return anything - the statement-level handler will remove it
                if (methodName == "RunModal")
                {
                    // Return 0 cast to the expected type (FormResult enum), or just return default
                    return SyntaxFactory.DefaultExpression(
                        SyntaxFactory.ParseTypeName("FormResult"));
                }
                // For Run: will be removed at statement level as a no-op
                return visited;
            }

            // NCLEnumMetadata.Create(N) -> NCLOptionMetadata.Default
            // NCLEnumMetadata.Create goes through NavGlobal.MetadataProvider -> NavEnvironment
            // NCLOptionMetadata.Default creates a simple default metadata without NavGlobal access
            if (exprText == "NCLEnumMetadata" && methodName == "Create")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("NCLOptionMetadata"),
                    SyntaxFactory.IdentifierName("Default"));
            }

            // ALSession.ALStartSession(...) -> MockSession.ALStartSession(...)
            // ALSession.ALIsSessionActive(...) -> MockSession.ALIsSessionActive(...)
            // ALSession.ALStopSession(...) -> MockSession.ALStopSession(...)
            // Session management APIs require a live BC service tier. MockSession dispatches
            // StartSession synchronously via MockCodeunitHandle (same pattern as Codeunit.Run).
            if (exprText == "ALSession" &&
                (methodName == "ALStartSession" || methodName == "ALIsSessionActive" || methodName == "ALStopSession"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockSession"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // NavSession.Sleep(ms) -> MockSession.Sleep(ms)
            // Sleep requires NavSession which doesn't exist in standalone mode.
            if (exprText == "NavSession" && methodName == "Sleep")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockSession"),
                        SyntaxFactory.IdentifierName("Sleep")));
            }

            // ALCompiler.ToSecretText(navText) -> AlCompat.ToSecretText(navText)
            // ToSecretText wraps a text value as NavSecretText; requires NavSession in BC.
            if (exprText == "ALCompiler" && methodName == "ToSecretText")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ToSecretText")));
            }

            // ALCompiler.ToNavValue(x) -> AlCompat.ToNavValue(x)
            // ToNavValue chains through NavValueFormatter -> NavSession -> NavEnvironment
            if (exprText == "ALCompiler" && methodName == "ToNavValue")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ToNavValue")));
            }

            // ALCompiler.ObjectToExactNavValue<T>(x) -> (T)(object)x
            if (exprText == "ALCompiler" && methodName == "ObjectToExactNavValue")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                // Extract T from the generic method name
                if (memberAccess.Name is GenericNameSyntax genericName &&
                    genericName.TypeArgumentList.Arguments.Count == 1)
                {
                    var targetType = genericName.TypeArgumentList.Arguments[0];
                    return SyntaxFactory.CastExpression(
                        targetType,
                        SyntaxFactory.ParenthesizedExpression(
                            SyntaxFactory.CastExpression(
                                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
                                SyntaxFactory.ParenthesizedExpression(arg))));
                }
            }

            // ALCompiler.ObjectToDecimal -> AlCompat.ObjectToDecimal
            // ObjectToDecimal accesses NavSession for culture-aware parsing; our version is simpler.
            if (exprText == "ALCompiler" && methodName == "ObjectToDecimal")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToDecimal")));
            }

            // ALCompiler.ToInterface(this, codeunit) -> MockInterfaceHandle.Wrap(codeunit).
            // BC's generated code uses this to push a codeunit into a position that
            // expects NavInterfaceHandle (e.g. a NavObjectList<NavInterfaceHandle> entry).
            // Wrapping preserves the boxing so the target's T constraint is satisfied;
            // consumers that only need the raw implementation already unwrap via
            // MockInterfaceHandle.ALAssign / InvokeInterfaceMethod.
            if (exprText == "ALCompiler" && methodName == "ToInterface")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                {
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockInterfaceHandle"),
                            SyntaxFactory.IdentifierName("Wrap")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(args[1].Expression))));
                }
            }

            // ALCompiler.ObjectToExactINavRecordHandle(x) -> (MockRecordHandle)x
            if (exprText == "ALCompiler" && methodName == "ObjectToExactINavRecordHandle")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                return SyntaxFactory.CastExpression(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.ParenthesizedExpression(arg));
            }

            // ALCompiler.NavIndirectValueToDecimal(x) -> AlCompat.ObjectToDecimal(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToDecimal")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToDecimal")));
            }

            // ALCompiler.NavIndirectValueToBoolean(x) -> AlCompat.NavIndirectValueToBoolean(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToBoolean")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("NavIndirectValueToBoolean")));
            }

            // ALCompiler.NavIndirectValueToInt32(x) -> AlCompat.NavIndirectValueToInt32(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToInt32")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("NavIndirectValueToInt32")));
            }

            // ALCompiler.NavIndirectValueToNavValue<T>(x) -> AlCompat.NavIndirectValueToNavValue<T>(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToNavValue")
            {
                // Preserve the generic type arguments (e.g., <NavText>)
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        memberAccess.Name)); // keep GenericNameSyntax with type args
            }

            // ALCompiler.NavIndirectValueToINavRecordHandle(x) -> (MockRecordHandle)x
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToINavRecordHandle")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                return SyntaxFactory.CastExpression(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.ParenthesizedExpression(arg));
            }

            // ALCompiler.ToVariant(this, value) -> AlCompat.ToVariant(value)
            // ALCompiler.NavValueToVariant(this, value) -> AlCompat.ToVariant(value)
            if (exprText == "ALCompiler" && (methodName == "ToVariant" || methodName == "NavValueToVariant"))
            {
                var args = visited.ArgumentList.Arguments;
                // Skip the first 'this' argument (ITreeObject)
                if (args.Count >= 2)
                {
                    var valueArg = args[1];
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("ToVariant")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(valueArg)));
                }
            }

            // NavInStream.Default(this) -> NavInStream.Default(null!)
            // NavOutStream.Default(this) -> NavOutStream.Default(null!)
            // NavFieldRef.Default(this) -> NavFieldRef.Default(null!)
            // These BC types require ITreeObject but work with null in standalone mode.
            // MockFieldRef.Default(this) -> MockFieldRef.Default()
            // After VisitIdentifierName, NavFieldRef is now MockFieldRef.
            // MockFieldRef.Default() takes no args — strip entirely.
            if (exprText == "MockFieldRef" && methodName == "Default")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1 && args[0].Expression.ToString() == "this")
                {
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList());
                }
            }

            // MockInStream.Default(this) -> MockInStream.Default()
            // MockOutStream.Default(this) -> MockOutStream.Default()
            // After VisitIdentifierName, NavInStream/NavOutStream are now MockInStream/MockOutStream.
            // MockInStream/MockOutStream.Default() takes optional null parent — strip the arg.
            if ((exprText == "MockInStream" || exprText == "MockOutStream")
                && methodName == "Default")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1 && args[0].Expression.ToString() == "this")
                {
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList());
                }
            }

            // ALSystemString.ALLowercase(x) -> (x?.ToString().ToLowerInvariant() ?? "")
            // ALSystemString.ALUppercase(x) -> (x?.ToString().ToUpperInvariant() ?? "")
            // These BC runtime methods access NavEnvironment for CultureInfo, crashing on Linux.
            // We call .ToString() first to ensure the string overload is used, not the
            // MemoryExtensions.ToLowerInvariant(ReadOnlySpan, Span) overload from System.Memory.
            if (exprText == "ALSystemString" && (methodName == "ALLowercase" || methodName == "ALUppercase"))
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1)
                {
                    var netMethod = methodName == "ALLowercase" ? "ToLowerInvariant" : "ToUpperInvariant";
                    // Build: (x?.ToString().ToLowerInvariant() ?? "")
                    var arg = args[0].Expression;
                    var toStringCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberBindingExpression(
                            SyntaxFactory.IdentifierName("ToString")),
                        SyntaxFactory.ArgumentList());
                    var caseCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            toStringCall,
                            SyntaxFactory.IdentifierName(netMethod)),
                        SyntaxFactory.ArgumentList());
                    var nullCoalesce = SyntaxFactory.ParenthesizedExpression(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.CoalesceExpression,
                            SyntaxFactory.ConditionalAccessExpression(arg, caseCall),
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(""))));
                    return nullCoalesce;
                }
            }

            // ALSystemString.ALStrSubstNo(fmt, arg1, arg2, ...) -> AlCompat.StrSubstNo(fmt, arg1, arg2, ...)
            // The real BC ALStrSubstNo routes each argument through NavValueFormatter.Format(NavSession, ...)
            // which crashes with NullReferenceException when NavSession is null (runner context).
            // AlCompat.StrSubstNo uses AlCompat.Format() instead, which handles all BC types without NavSession.
            if (exprText == "ALSystemString" && methodName == "ALStrSubstNo")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("StrSubstNo")));
            }

            // ALDatabase.ALIsInWriteTransaction() -> false
            // The real ALIsInWriteTransaction calls NavSession.HasWriteTransaction() which crashes
            // with NullReferenceException when NavSession is null (runner context, no DB).
            if (exprText == "ALDatabase" && methodName == "ALIsInWriteTransaction")
            {
                return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                    .WithTriviaFrom(visited);
            }

            // ALSystemDate.ALWorkDate(null!) -> ALSystemDate.ALWorkDate(NavDate.Default)
            // The rewriter turns this.Session -> null!, which makes ALWorkDate ambiguous between
            // the NavSession and NavDate overloads. We disambiguate to the NavDate overload.
            if (exprText == "ALSystemDate" && methodName == "ALWorkDate")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1)
                {
                    var argText = args[0].Expression.ToString();
                    // Match null!, default! or similar null patterns from Session rewriting
                    if (argText.Contains("null"))
                    {
                        return SyntaxFactory.InvocationExpression(
                            visited.Expression,
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(
                                        SyntaxFactory.DefaultExpression(
                                            SyntaxFactory.ParseTypeName("NavDate"))))));
                    }
                }
            }

            // ALCompiler.NavRecordToVariant(this, record) -> AlCompat.ToVariant(record)
            // Strip the ITreeObject first argument
            if (exprText == "ALCompiler" && methodName == "NavRecordToVariant")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                {
                    var recordArg = args[1];
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("ToVariant")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(recordArg)));
                }
            }

            // ALCompiler.ObjectToInt32(x) -> Convert.ToInt32(x)
            // Simple numeric conversion
            if (exprText == "ALCompiler" && methodName == "ObjectToInt32")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("Convert"),
                        SyntaxFactory.IdentifierName("ToInt32")));
            }

            // ALCompiler.ObjectToBoolean(x) -> AlCompat.ObjectToBoolean(x)
            if (exprText == "ALCompiler" && methodName == "ObjectToBoolean")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToBoolean")));
            }

            // ALSystemNumeric.ALRandomize(seed) -> AlCompat.ALRandomize(seed)
            // ALSystemNumeric.ALRandom(max) -> AlCompat.ALRandom(max)
            // ALRandomize/ALRandom require NavSession; our versions use System.Random.
            if (exprText == "ALSystemNumeric" && (methodName == "ALRandomize" || methodName == "ALRandom"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // value.ALByValue(this) -> value  (strip ITreeObject calls)
            // value.ModifyLength(N) -> value  (strip length modification)
            if (StripITreeObjectArgMethods.Contains(methodName))
            {
                // Return just the expression the method is called on
                return memberAccess.Expression;
            }

            // blob.ALCreateInStream(this, inStr) -> blob.ALCreateInStream(null!, inStr)
            // Replace the first 'this' (ITreeObject) argument with null! but keep the rest.
            // These are real BC runtime methods that require ITreeObject; they work with null
            // in standalone mode for the operations we support.
            if (NullifyFirstThisArgMethods.Contains(methodName))
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 1 && args[0].Expression.ToString() == "this")
                {
                    var nullBang = SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.SuppressNullableWarningExpression,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    var newArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    newArgs = newArgs.Add(SyntaxFactory.Argument(nullBang));
                    for (int i = 1; i < args.Count; i++)
                        newArgs = newArgs.Add(args[i]);
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList(newArgs));
                }
            }

            // recRef.ALField(this, fieldNo) -> recRef.ALField(fieldNo)
            // Strip the first 'this' (ITreeObject) argument from mock type methods.
            if (StripFirstThisArgMethods.Contains(methodName))
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2 && args[0].Expression.ToString() == "this")
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 1; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavFormatEvaluateHelper.Format(this.Session, value) -> AlCompat.Format(value)
            if (exprText == "NavFormatEvaluateHelper" && methodName == "Format")
            {
                var args = visited.ArgumentList.Arguments;
                // Skip the first 'this.Session' argument
                if (args.Count >= 2)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 1; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("Format")),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // ALSystemVariable.ALCopyStream(dataError, outStream, inStream)
            // -> MockStream.ALCopyStream(dataError, outStream, inStream)
            // BC emits COPYSTREAM as a call to ALSystemVariable.ALCopyStream which takes
            // NavOutStream/NavInStream. After the NavOutStream/NavInStream type rename those
            // args are MockOutStream/MockInStream, so we redirect to MockStream.ALCopyStream
            // which accepts the mock types.
            if (exprText == "ALSystemVariable" && methodName == "ALCopyStream")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockStream"),
                        SyntaxFactory.IdentifierName("ALCopyStream")));
            }

            // ALSystemErrorHandling.ALClearLastError() -> AlScope.LastErrorText = ""
            // ALSystemErrorHandling.ALGetLastErrorTextFunc(...) -> AlScope.LastErrorText
            if (exprText == "ALSystemErrorHandling")
            {
                if (methodName == "ALClearLastError")
                {
                    // Return an assignment expression: AlScope.LastErrorText = ""
                    return SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlScope"),
                            SyntaxFactory.IdentifierName("LastErrorText")),
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal("")));
                }

                if (methodName == "ALGetLastErrorTextFunc")
                {
                    // Return just AlScope.LastErrorText (ignore the excludeCustomerData arg)
                    return SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlScope"),
                        SyntaxFactory.IdentifierName("LastErrorText"));
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Member access expressions: remove .Target., rewrite base.Parent._parent
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

        // Pattern: base.Parent.xxx -> _parent.xxx
        // After recursion, base.Parent is a MemberAccessExpression where:
        //   Expression = BaseExpression ("base"), Name = "Parent"
        // So base.Parent.field shows up as: (base.Parent).field
        if (visited.Expression is MemberAccessExpressionSyntax innerAccess2 &&
            innerAccess2.Name.Identifier.Text == "Parent" &&
            innerAccess2.Expression is BaseExpressionSyntax)
        {
            // Special case: base.Parent.__ThisHandle -> MockCodeunitHandle.FromInstance(_parent)
            // BC compiler emits __ThisHandle for exit(this) in codeunit-returning methods.
            // __ThisHandle is a NavCodeunitHandle property on NavCodeunit; after stripping the
            // base class it is undefined. Replace with a factory call that wraps the instance.
            if (visited.Name.Identifier.Text == "__ThisHandle")
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                        SyntaxFactory.IdentifierName("FromInstance")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("_parent")))));
            }
            // Replace base.Parent.xxx with _parent.xxx
            return visited.WithExpression(SyntaxFactory.IdentifierName("_parent"));
        }

        // Pattern: base.ParentObject -> this.ParentObject
        // Page/table extensions reference base.ParentObject but base class is stripped to object.
        // ParentObject is rewritten to return Rec, so we just replace base with this.
        if (visited.Name.Identifier.Text == "ParentObject" &&
            visited.Expression is BaseExpressionSyntax)
        {
            return visited.WithExpression(SyntaxFactory.ThisExpression());
        }

        // Pattern: xxx.Target -> xxx (strip .Target accessor on handles)
        // This covers both xxx.Target.Method (already handled) and standalone xxx.Target
        if (visited.Name.Identifier.Text == "Target")
        {
            // Just strip .Target and return the expression
            return visited.Expression;
        }

        // (NavOptionMetadata access left as-is — NavOption type is preserved)

        // Pattern: xxx.Target.MethodName -> xxx.MethodName (legacy — now redundant but kept for safety)
        if (visited.Expression is MemberAccessExpressionSyntax innerAccess &&
            innerAccess.Name.Identifier.Text == "Target")
        {
            var outerMethodName = visited.Name.Identifier.Text;

            // Record target methods
            if (RecordTargetMethods.Contains(outerMethodName))
            {
                return visited.WithExpression(innerAccess.Expression);
            }

            // Codeunit target method: Invoke
            if (outerMethodName == "Invoke")
            {
                return visited.WithExpression(innerAccess.Expression);
            }

            // Also handle ToDecimal, and other methods that may chain after Target
            // e.g. this.spikeItem.Target.GetFieldValueSafe(3, NavType.Decimal).ToDecimal()
            // The GetFieldValueSafe is caught above; ToDecimal chains on its result, not on Target.
        }

        // Pattern: ALSystemErrorHandling.ALGetLastErrorText -> AlScope.LastErrorText
        // ALSystemErrorHandling accesses NavCurrentThread.Session which is null in standalone mode.
        if (visited.Expression is IdentifierNameSyntax errHandlingId &&
            errHandlingId.Identifier.Text == "ALSystemErrorHandling")
        {
            var errProp = visited.Name.Identifier.Text;
            if (errProp == "ALGetLastErrorText" || errProp == "ALGetLastErrorCode" || errProp == "ALGetLastErrorCallStack")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlScope"),
                    SyntaxFactory.IdentifierName("LastErrorText"));
            }
        }

        // Pattern: ALDatabase.ALCompanyName / ALDatabase.ALUserID / ALDatabase.ALTenantID / ALDatabase.ALSerialNumber
        // These static property accesses crash because ALDatabase requires a live BC session.
        // Rewrite to empty-string literals — standalone mode has no company/user/tenant context.
        if (visited.Expression is IdentifierNameSyntax dbId &&
            dbId.Identifier.Text == "ALDatabase" &&
            ALDatabaseStringProps.Contains(visited.Name.Identifier.Text))
        {
            return SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(""))
                .WithTriviaFrom(visited);
        }

        // Pattern: value.ALIsBoolean, value.ALIsText, etc. (NavVariant type-check properties)
        // Rewrite to: AlCompat.ALIsBoolean(value) invocation
        var memberName = visited.Name.Identifier.Text;

        // Pattern: xxx.Session.IsEventSessionRecorderEnabled -> false
        // Also: xxx.Session -> null! (Session property removed with base class)
        if (memberName == "Session" &&
            (visited.Expression is ThisExpressionSyntax || visited.Expression is IdentifierNameSyntax))
        {
            return SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
                .WithTriviaFrom(visited);
        }
        if (memberName == "IsEventSessionRecorderEnabled")
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                .WithTriviaFrom(visited);
        }
        if (memberName.StartsWith("ALIs") && NavVariantTypeCheckProps.Contains(memberName))
        {
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName(memberName)),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(visited.Expression))));
        }

        // NavOption.ALNames / NavOption.ALOrdinals — property getters that
        // reach into NCLOptionMetadata native code. Redirect to AlCompat
        // helpers which look up the tagged enum id via ConditionalWeakTable.
        if (memberName == "ALNames" || memberName == "ALOrdinals")
        {
            var helper = memberName == "ALNames" ? "GetNamesForOption" : "GetOrdinalsForOption";
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName(helper)),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(visited.Expression))));
        }

        // Pattern: ALSystemLanguage.ALGlobalLanguage -> MockLanguage.ALGlobalLanguage
        // ALSystemLanguage.get_ALGlobalLanguage / set_ALGlobalLanguage crash because there
        // is no live BC session context in standalone mode. Redirect both get and set to
        // MockLanguage.ALGlobalLanguage which is a plain static property backed by an int field.
        if (visited.Expression is IdentifierNameSyntax langId &&
            langId.Identifier.Text == "ALSystemLanguage" &&
            memberName == "ALGlobalLanguage")
        {
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("MockLanguage"),
                SyntaxFactory.IdentifierName("ALGlobalLanguage"))
                .WithTriviaFrom(visited);
        }

        return visited;
    }

    /// <summary>
    /// Returns true if an expression refers to the BC NavForm type, whether
    /// written as a bare `NavForm` identifier or fully-qualified such as
    /// `Microsoft.Dynamics.Nav.Runtime.NavForm`. BC compiler output varies
    /// across versions.
    /// </summary>
    private static bool IsNavFormReference(ExpressionSyntax expr)
    {
        var text = expr.ToString();
        return text == "NavForm" || text.EndsWith(".NavForm", StringComparison.Ordinal);
    }

    /// <summary>
    /// ALDatabase static property names that return strings and crash without a BC session.
    /// Rewritten to empty-string literals in standalone mode.
    /// </summary>
    private static readonly HashSet<string> ALDatabaseStringProps = new(StringComparer.Ordinal)
    {
        "ALCompanyName",    // CompanyName built-in
        "ALUserID",         // UserId built-in
        "ALTenantID",       // TenantId built-in (added for completeness)
        "ALSerialNumber",   // SerialNumber built-in (added for completeness)
    };

    private static readonly HashSet<string> NavVariantTypeCheckProps = new(StringComparer.Ordinal)
    {
        "ALIsBoolean", "ALIsOption", "ALIsInteger", "ALIsByte", "ALIsBigInteger",
        "ALIsDecimal", "ALIsText", "ALIsCode", "ALIsChar", "ALIsTextConst",
        "ALIsDate", "ALIsTime", "ALIsDuration", "ALIsDateTime", "ALIsDateFormula",
        "ALIsGuid", "ALIsRecordId", "ALIsRecord", "ALIsRecordRef", "ALIsFieldRef",
        "ALIsCodeunit", "ALIsFile",
    };

    // -----------------------------------------------------------------------
    // If statements: strip the `if (X.γeventScope == null && !Session.IsEventSessionRecorderEnabled) return;`
    // guard at the top of BC-generated event methods. Without this, the
    // generated method bails before reaching the rewritten FireEvent
    // statement because γeventScope stays null (no BC-level subscribers
    // registered in standalone mode).
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        if (node.Condition is BinaryExpressionSyntax bin &&
            bin.OperatorToken.IsKind(SyntaxKind.AmpersandAmpersandToken))
        {
            static bool MentionsEventScope(ExpressionSyntax expr) =>
                expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Any(id => id.Identifier.ValueText == "γeventScope");

            if (MentionsEventScope(bin.Left) || MentionsEventScope(bin.Right))
            {
                return SyntaxFactory.EmptyStatement();
            }
        }
        return base.VisitIfStatement(node);
    }

    // -----------------------------------------------------------------------
    // Expression statements: remove StmtHit(N); and handle NavRuntimeHelpers
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // Keep StmtHit(N) calls for coverage tracking (they call AlScope.StmtHit)
        if (node.Expression is InvocationExpressionSyntax invocation)
        {

            // Remove calls to BC-only methods (ALGetTable, ALClose, RunEvent) that can't work standalone
            if (invocation.Expression is MemberAccessExpressionSyntax stripMa &&
                StripEntireCallMethods.Contains(stripMa.Name.Identifier.Text))
            {
                // Return empty statement instead of null to avoid crash inside using blocks
                return SyntaxFactory.EmptyStatement();
            }

            // `βscope.RunEvent()` dispatches event subscribers. Walk the
            // ancestor tree to recover the enclosing Codeunit<N> type and
            // the enclosing event method name, then rewrite the statement
            // to a direct call into AlCompat.FireEvent which consults the
            // subscriber registry built at runtime via reflection.
            if (invocation.Expression is MemberAccessExpressionSyntax runEventMa &&
                runEventMa.Name.Identifier.Text == "RunEvent" &&
                invocation.ArgumentList.Arguments.Count == 0)
            {
                int? cuId = null;
                string? eventName = null;
                foreach (var ancestor in node.Ancestors())
                {
                    if (eventName == null && ancestor is MethodDeclarationSyntax mdecl)
                        eventName = mdecl.Identifier.Text;
                    if (ancestor is ClassDeclarationSyntax cdecl && cdecl.Identifier.Text.StartsWith("Codeunit"))
                    {
                        var idStr = cdecl.Identifier.Text.Substring("Codeunit".Length);
                        if (int.TryParse(idStr, out var id)) cuId = id;
                        break;
                    }
                }
                if (cuId.HasValue && eventName != null)
                {
                    var call = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("FireEvent")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                        {
                            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                                SyntaxKind.NumericLiteralExpression,
                                SyntaxFactory.Literal(cuId.Value))),
                            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(eventName)))
                        })));
                    return SyntaxFactory.ExpressionStatement(call);
                }
                // Fallback: strip the call, same as before.
                return SyntaxFactory.EmptyStatement();
            }

            // NavForm.Run / NavForm.RunModal / NavForm.SetRecord called as bare statements
            // (i.e. value discarded) -> no-op. Page navigation is not supported standalone;
            // when the return value is assigned, the expression-level rewriter turns it
            // into default(FormResult) instead — which is not a valid statement, so we must
            // strip it here first.
            //
            // Accept both `NavForm.Method(...)` and fully-qualified
            // `Microsoft.Dynamics.Nav.Runtime.NavForm.Method(...)` — older BC
            // compiler versions emit the qualified form, so a strict `== "NavForm"`
            // check missed them and let the broken call survive into Roslyn.
            if (invocation.Expression is MemberAccessExpressionSyntax navFormMa &&
                IsNavFormReference(navFormMa.Expression) &&
                (navFormMa.Name.Identifier.Text == "Run" ||
                 navFormMa.Name.Identifier.Text == "RunModal" ||
                 navFormMa.Name.Identifier.Text == "SetRecord"))
            {
                return SyntaxFactory.EmptyStatement();
            }

            // NavRuntimeHelpers.CompilationError(...) -> throw new InvalidOperationException("Compilation error");
            if (invocation.Expression is MemberAccessExpressionSyntax ma &&
                ma.Expression.ToString() == "NavRuntimeHelpers" &&
                ma.Name.Identifier.Text == "CompilationError")
            {
                return SyntaxFactory.ThrowStatement(
                    SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("InvalidOperationException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.StringLiteralExpression,
                                        SyntaxFactory.Literal("Compilation error")))))));
            }
        }

        return base.VisitExpressionStatement(node);
    }
}
