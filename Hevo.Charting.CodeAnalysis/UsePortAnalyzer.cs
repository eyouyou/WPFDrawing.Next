using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Hevo.Charting.CodeAnalysis
{
    // 💥 概念代码：Hevo Hook 铁律检查器
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class UsePortAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "HEVO003", "违背 Hook 铁律",
            "UsePort 方法不能在循环或条件分支 {0} 中调用，必须保证每次 OnProject 调用的顺序和次数绝对一致！",
            "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (invocation.Expression.ToString().EndsWith("UsePort"))
            {
                // 向上遍历语法树，查找危险节点
                var parent = invocation.Parent;
                while (parent != null && !(parent is MethodDeclarationSyntax)) // 查到方法级别为止
                {
                    if (parent is ForEachStatementSyntax || parent is ForStatementSyntax ||
                        parent is WhileStatementSyntax || parent is IfStatementSyntax)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), parent.Kind().ToString()));
                        break;
                    }
                    parent = parent.Parent;
                }
            }
        }
    }
}
