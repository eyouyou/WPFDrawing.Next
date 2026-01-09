using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Hevo.Charting.CodeAnalysis
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class HevoHookSafetyAnalyzer : DiagnosticAnalyzer
    {
        // ==========================================
        // 💥 规则定义区：三大铁律，全标为 Error 阻断编译！
        // ==========================================

        // 铁律 1: 嵌套调用熔断 (前面提过的，防死锁)
        public const string RuleId_NoNesting = "HEVO004";
        private static readonly DiagnosticDescriptor Rule_NoNesting = new DiagnosticDescriptor(
            RuleId_NoNesting, "禁止嵌套调用 Hook", "严禁在 Hook 的 factory 回调内部调用另一个 Hook ({0})",
            "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

        // 铁律 2: 循环必传区分符 (解决陷阱 1)
        public const string RuleId_LoopDiscriminator = "HEVO005";
        private static readonly DiagnosticDescriptor Rule_LoopDiscriminator = new DiagnosticDescriptor(
            RuleId_LoopDiscriminator, "循环内必须传递 discriminator", "在循环体内部调用 Hook ({0}) 时，必须显式提供 discriminator 参数以防止 Key 碰撞",
            "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

        // 铁律 3: 自定义 Hook 强制透传 (解决陷阱 2)
        public const string RuleId_CallerInfoPassThrough = "HEVO006";
        private static readonly DiagnosticDescriptor Rule_CallerInfoPassThrough = new DiagnosticDescriptor(
            RuleId_CallerInfoPassThrough, "自定义 Hook 必须透传调用信息", "自定义 Hook ({0}) 内部调用了基础 Hook，必须在其方法签名中声明并向下透传 [CallerFilePath] 和 [CallerLineNumber]",
            "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule_NoNesting, Rule_LoopDiscriminator, Rule_CallerInfoPassThrough);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            // 监听所有的方法调用
            context.RegisterSyntaxNodeAction(AnalyzeHookInvocation, SyntaxKind.InvocationExpression);
        }

        // ==========================================
        // 💥 核心检查逻辑
        // ==========================================
        private void AnalyzeHookInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var methodSymbol = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

            // 1. 甄别是否为系统的 Hook 方法 (基于 FeatureContext 且以 Use 开头)
            if (!IsHevoHook(methodSymbol)) return;

            // ---------------------------------------------------------
            // 🛡️ 检查铁律 1: 嵌套熔断 (HEVO004)
            // ---------------------------------------------------------
            var parentLambda = invocation.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
            if (parentLambda != null)
            {
                var outerInvocation = parentLambda.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (outerInvocation != null && IsHevoHook(context.SemanticModel.GetSymbolInfo(outerInvocation).Symbol as IMethodSymbol))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule_NoNesting, invocation.GetLocation(), methodSymbol!.Name));
                    return; // 已经犯了死罪，直接 return
                }
            }

            // ---------------------------------------------------------
            // 🛡️ 检查铁律 2: 循环陷阱防御 (HEVO005)
            // ---------------------------------------------------------
            // 向上查找是否处于 for, foreach, while, do 循环中
            bool isInsideLoop = invocation.Ancestors().Any(a =>
                a.IsKind(SyntaxKind.ForStatement) ||
                a.IsKind(SyntaxKind.ForEachStatement) ||
                a.IsKind(SyntaxKind.WhileStatement) ||
                a.IsKind(SyntaxKind.DoStatement));

            if (isInsideLoop)
            {
                // 检查是否传入了名为 discriminator 的参数
                bool hasDiscriminator = invocation.ArgumentList.Arguments.Any(arg =>
                    arg.NameColon?.Name.Identifier.Text == "discriminator" ||
                    IsMappedToParameter(context, arg, "discriminator"));

                if (!hasDiscriminator)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule_LoopDiscriminator, invocation.GetLocation(), methodSymbol!.Name));
                }
            }

            // ---------------------------------------------------------
            // 🛡️ 检查铁律 3: 自定义 Hook 的视界遮蔽防御 (HEVO006)
            // ---------------------------------------------------------
            // 向上查找当前代码所在的声明方法
            var enclosingMethodSyntax = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (enclosingMethodSyntax != null)
            {
                var enclosingMethodSymbol = context.SemanticModel.GetDeclaredSymbol(enclosingMethodSyntax);

                // 如果外面包着的也是一个自定义 Hook (以 Use 开头且扩展了 FeatureContext)
                if (IsHevoHook(enclosingMethodSymbol))
                {
                    // 检查内部调用是否显式透传了 file 和 line 参数
                    bool passedFile = invocation.ArgumentList.Arguments.Any(arg => arg.NameColon?.Name.Identifier.Text == "file" || arg.Expression.ToString() == "file");
                    bool passedLine = invocation.ArgumentList.Arguments.Any(arg => arg.NameColon?.Name.Identifier.Text == "line" || arg.Expression.ToString() == "line");

                    // 如果没有透传，说明存在“视界遮蔽”撞车风险
                    if (!passedFile || !passedLine)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Rule_CallerInfoPassThrough, invocation.GetLocation(), enclosingMethodSymbol!.Name));
                    }
                }
            }
        }

        // ==========================================
        // 💥 辅助鉴别方法
        // ==========================================
        private bool IsHevoHook(IMethodSymbol? methodSymbol)
        {
            if (methodSymbol == null) return false;
            if (!methodSymbol.Name.StartsWith("Use")) return false;
            if (methodSymbol.ContainingType.Name == "FeatureContext") return true;
            if (methodSymbol.IsExtensionMethod && methodSymbol.Parameters.Length > 0 && methodSymbol.Parameters[0].Type.Name == "FeatureContext") return true;
            return false;
        }

        private bool IsMappedToParameter(SyntaxNodeAnalysisContext context, ArgumentSyntax arg, string parameterName)
        {
            // 辅助判断位置参数是否对应目标参数名称（简化版处理）
            if (arg.NameColon == null)
            {
                var invocation = arg.Parent?.Parent as InvocationExpressionSyntax;
                if (invocation != null)
                {
                    var symbol = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (symbol != null)
                    {
                        var index = ((ArgumentListSyntax)arg.Parent!).Arguments.IndexOf(arg);
                        if (index >= 0 && index < symbol.Parameters.Length)
                        {
                            return symbol.Parameters[index].Name == parameterName;
                        }
                    }
                }
            }
            return false;
        }
    }
}
