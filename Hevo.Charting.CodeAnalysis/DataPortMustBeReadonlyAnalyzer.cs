using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Hevo.Charting.CodeAnalysis
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class DataPortMustBeReadonlyAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "HEVO007";
        private static readonly string Title = "引脚必须是不可变的";
        private static readonly string MessageFormat = "物理线缆引脚 '{0}' 必须被声明为 readonly (字段) 或 init (属性)！";
        private static readonly string Category = "HevoArchitecture";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId, Title, MessageFormat, Category,
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeProperty, SyntaxKind.PropertyDeclaration);
        }

        private void AnalyzeProperty(SyntaxNodeAnalysisContext context)
        {
            var propDecl = (PropertyDeclarationSyntax)context.Node;

            // 如果类型名包含 DataPort
            if (propDecl.Type.ToString().Contains("DataPort"))
            {
                // 检查是否包含 set 访问器且没有 init
                var hasSet = propDecl.AccessorList?.Accessors.Any(a => a.Keyword.IsKind(SyntaxKind.SetKeyword)) ?? false;
                var hasInit = propDecl.AccessorList?.Accessors.Any(a => a.Keyword.IsKind(SyntaxKind.InitKeyword)) ?? false;

                if (hasSet && !hasInit)
                {
                    var diagnostic = Diagnostic.Create(Rule, propDecl.Identifier.GetLocation(), propDecl.Identifier.Text);
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
    }
}
