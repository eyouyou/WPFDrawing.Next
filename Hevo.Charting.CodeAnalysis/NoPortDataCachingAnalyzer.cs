using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace Hevo.Charting.CodeAnalysis
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class NoPortDataCachingAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "HEVO001";
        private static readonly string Title = "禁止跨帧缓存底层数据引用";
        private static readonly string MessageFormat = "绝对禁止将 UsePort 或 Read 获取的引用类型/内存切片赋值给字段 '{0}'，这会导致跨帧内存泄漏！";
        private static readonly string Category = "HevoArchitecture";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, Title, MessageFormat, Category,
            DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
        }

        private void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
        {
            var assignment = (AssignmentExpressionSyntax)context.Node;

            if (assignment.Right is InvocationExpressionSyntax invocation)
            {
                var methodName = GetMethodName(invocation);
                if (methodName != "UsePort" && methodName != "Read") return;

                var leftSymbol = context.SemanticModel.GetSymbolInfo(assignment.Left).Symbol;

                if (leftSymbol is IFieldSymbol fieldSymbol)
                {
                    CheckTypeAndReport(context, assignment, fieldSymbol.Name, fieldSymbol.Type);
                }
                else if (leftSymbol is IPropertySymbol propertySymbol)
                {
                    CheckTypeAndReport(context, assignment, propertySymbol.Name, propertySymbol.Type);
                }
            }
        }

        private void CheckTypeAndReport(SyntaxNodeAnalysisContext context, AssignmentExpressionSyntax assignment, string symbolName, ITypeSymbol type)
        {
            bool isDangerous = false;

            // 1. 💥 拦截所有引用类型 (class, interface, delegate 等)
            if (type.IsReferenceType)
            {
                isDangerous = true;
            }
            // 2. 💥 拦截“包裹引用的伪装者” (ReadOnlyMemory, Memory, Span 等)
            else if (type.IsValueType && IsMemoryOrSpan(type))
            {
                isDangerous = true;
            }

            // 3. 💥 如果是普通的 struct (如 DoubleRange, int, float)，安全放行！不报警！

            if (isDangerous)
            {
                var diagnostic = Diagnostic.Create(Rule, assignment.GetLocation(), symbolName);
                context.ReportDiagnostic(diagnostic);
            }
        }

        private bool IsMemoryOrSpan(ITypeSymbol type)
        {
            var name = type.Name;
            return name.Contains("Memory") || name.Contains("Span");
        }

        private string GetMethodName(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                return memberAccess.Name.Identifier.Text;
            if (invocation.Expression is IdentifierNameSyntax identifier)
                return identifier.Identifier.Text;
            return string.Empty;
        }
    }
}