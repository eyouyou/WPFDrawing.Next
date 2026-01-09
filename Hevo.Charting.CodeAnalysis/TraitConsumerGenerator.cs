using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Hevo.Charting.CodeAnalysis
{
    [Generator(LanguageNames.CSharp)]
    public class TraitConsumerGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
#if DEBUG
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                //System.Diagnostics.Debugger.Launch();
            }
#endif

            // 1. 语法树初筛：极速过滤出 partial class
            var classDeclarations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => IsTargetClass(s),
                    transform: static (ctx, _) => GetSemanticClassInfo(ctx)) // <--- 核心：进入语义分析
                .Where(static m => m is not null);

            // 2. 输出生成代码
            context.RegisterSourceOutput(classDeclarations, static (spc, source) => Execute(spc, source!));
        }

        private static bool IsTargetClass(SyntaxNode node)
        {
            return node is ClassDeclarationSyntax classSyntax &&
                   classSyntax.Modifiers.Any(m => m.ValueText == "partial");
        }

        // ==========================================================
        // 🔥 核心魔法：使用语义模型 (Semantic Model) 降维打击
        // ==========================================================
        private static ClassGenerateInfo? GetSemanticClassInfo(GeneratorSyntaxContext context)
        {
            var classSyntax = (ClassDeclarationSyntax)context.Node;
            var semanticModel = context.SemanticModel;

            // 🚨 修复一：使用语义模型获取类的完整符号信息，以正确处理泛型！
            var classSymbol = semanticModel.GetDeclaredSymbol(classSyntax) as INamedTypeSymbol;
            if (classSymbol == null) return null;

            // 找 OnUpdate 方法
            var onUpdateMethod = classSyntax.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "OnUpdate");

            if (onUpdateMethod == null) return null;

            var traits = new HashSet<string>();

            // 遍历 OnUpdate 里的所有方法调用
            var invocations = onUpdateMethod.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
                {
                    var returnType = methodSymbol.ReturnType;

                    bool isVisualTrait = returnType.TypeKind != TypeKind.Interface &&
                                         returnType.AllInterfaces.Any(i => i.Name == "IVisualTrait");

                    if (isVisualTrait)
                    {
                        string fullTypeName = returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        traits.Add(fullTypeName);
                    }
                }
            }

            // 【新增】特性扫描逻辑：找出类头上所有的 [Consumes(typeof(XXX))]
            var attributes = classSyntax.AttributeLists.SelectMany(al => al.Attributes);
            foreach (var attr in attributes)
            {
                if (attr.Name.ToString().Contains("Consumes") && attr.ArgumentList != null)
                {
                    var arg = attr.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    if (arg is TypeOfExpressionSyntax typeOfExpr)
                    {
                        // 这里最好也通过语义模型获取安全的全名，但考虑到你原先的设计，先保持 ToString 兜底
                        var typeSymbol = semanticModel.GetTypeInfo(typeOfExpr.Type).Type;
                        if (typeSymbol != null)
                        {
                            traits.Add(typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                        }
                        else
                        {
                            traits.Add(typeOfExpr.Type.ToString());
                        }
                    }
                }
            }

            if (traits.Count == 0) return null;

            // 🚨 修复二：提取类的声明名称，必须包含泛型参数！(例如：AxisLayer<TDomain>)
            string typeParameters = classSymbol.TypeParameters.IsEmpty
                ? ""
                : "<" + string.Join(", ", classSymbol.TypeParameters.Select(t => t.Name)) + ">";

            string classNameWithGenerics = classSymbol.Name + typeParameters;

            // 获取命名空间
            string namespaceName = classSymbol.ContainingNamespace.ToDisplayString();

            return new ClassGenerateInfo(namespaceName, classNameWithGenerics, classSymbol.Name, traits);
        }

        private static void Execute(SourceProductionContext context, ClassGenerateInfo info)
        {
            // 拼接 IConsumes 接口
            var interfaces = string.Join(",\n        ",
                info.Traits.Select(t => $"global::Hevo.Charting.Abstractions.IConsumes<{t}>"));

            string sourceCode = $@"// <auto-generated />
// 本文件由 Semantic Source Generator 自动生成，无惧任何扩展方法封装！
using System;

namespace {info.NamespaceName}
{{
    public partial class {info.ClassNameWithGenerics} : 
        {interfaces}
    {{
    }}
}}
";
            // 文件名不能包含尖括号 < >，所以使用原始的类名 info.BaseClassName
            context.AddSource($"{info.BaseClassName}_Consumes.g.cs", SourceText.From(sourceCode, Encoding.UTF8));
        }
    }

    // 🚨 修复三：扩充 DTO，增加 BaseClassName 用于生成安全的文件名
    internal record ClassGenerateInfo(string NamespaceName, string ClassNameWithGenerics, string BaseClassName, HashSet<string> Traits);
}