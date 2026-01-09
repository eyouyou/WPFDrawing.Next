using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Hevo.Charting.CodeAnalysis
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class ReadOnlyProjectPhaseAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "HEVO002";
        private static readonly string Title = "Project 阶段单向数据流违规";
        private static readonly string MessageFormat = "在 Project / OnProject 渲染阶段内，绝对禁止调用 '{0}' 写入派生状态，请移至 Compose 阶段的算子或交互事件中！";
        private static readonly string Category = "HevoArchitecture";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, Title, MessageFormat, Category,
            DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            // 监听方法声明
            context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        }

        private void AnalyzeMethod(SyntaxNodeAnalysisContext context)
        {
            var methodDecl = (MethodDeclarationSyntax)context.Node;
            var methodName = methodDecl.Identifier.Text;

            // 只盯防 Project 或 OnProject 方法
            if (methodName != "Project" && methodName != "OnProject") return;
            if (methodDecl.Body == null && methodDecl.ExpressionBody == null) return;

            // 遍历该方法内部的所有方法调用
            var invocations = methodDecl.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var targetMethod = GetMethodName(invocation);

                // 抓捕黑板写入动作
                if (targetMethod == "Write" || targetMethod == "WriteIfChanged" || targetMethod == "Publish")
                {
                    var diagnostic = Diagnostic.Create(Rule, invocation.GetLocation(), targetMethod);
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }

        private string GetMethodName(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                return memberAccess.Name.Identifier.Text;
            return string.Empty;
        }
    }
}
