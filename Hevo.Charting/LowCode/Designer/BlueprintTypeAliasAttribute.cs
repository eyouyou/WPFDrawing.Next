using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// 声明 .NET 类型在蓝图 JSON 里的 TypeName 别名 —— 反序列化 / 序列化两端用本别名替代反射 <see cref="Type.Name"/>。
    /// <para>
    /// <b>动机</b> —— 开放泛型类型(<see cref="Hevo.Charting.WorkFlow.Composite{TItem}"/> 这种)反射拿到的
    /// <c>Type.Name</c> 带 .NET 内部格式后缀(<c>"Composite`1"</c>),不是协议层期望的字符串;
    /// 框架内置 sentinel 节点需要一个稳定、字面、跨进程的字符串作为蓝图 TypeName。
    /// 标注本 attribute 后,<see cref="GraphViewer.NodeFactory.CreateNode"/> 自动读 Alias 作为 node.TypeName,
    /// 字符串集中到类自身定义,框架通用工厂层不再特判任何具体类型,新加 sentinel 时只动该类一行。
    /// </para>
    /// <para>
    /// <b>用法</b>:
    /// </para>
    /// <code>
    /// [BlueprintTypeAlias("Composite")]
    /// public sealed class Composite&lt;TItem&gt; : ... { }
    /// </code>
    /// <para>
    /// <b>泛型语义</b> —— attribute 放在开放泛型类型定义上(<c>Composite&lt;&gt;</c>),NodeFactory 处理闭合泛型
    /// (<c>Composite&lt;Security&gt;</c>)时通过 <see cref="Type.GetGenericTypeDefinition"/> 反查到 attribute。
    /// 非泛型类型直接标在该类上即可。
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class BlueprintTypeAliasAttribute : Attribute
    {
        /// <summary>蓝图 JSON 里 <c>DataSourceModel.TypeName</c> / <c>FeatureModel.TypeName</c> 用的字符串。</summary>
        public string Alias { get; }

        public BlueprintTypeAliasAttribute(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
                throw new ArgumentException("BlueprintTypeAlias: alias 不能为空", nameof(alias));
            Alias = alias;
        }
    }

    /// <summary>
    /// <see cref="BlueprintTypeAliasAttribute"/> 反查的静态 helper —— 处理闭合泛型 → 开放泛型的归一化
    /// 以及反射结果缓存(attribute 不可变,程序集生命周期内永不失效)。
    /// </summary>
    public static class BlueprintTypeAlias
    {
        private static readonly ConcurrentDictionary<Type, string?> _aliasCache = new();

        /// <summary>
        /// 返回类型声明的蓝图别名。闭合泛型反查到开放泛型上的 attribute;未标 attribute → 返 null,
        /// 调用方按需 fall back 到 <see cref="Type.Name"/>。
        /// </summary>
        public static string? GetAlias(Type type)
            => _aliasCache.GetOrAdd(type, static t =>
            {
                var defTok = t.IsGenericType && !t.IsGenericTypeDefinition ? t.GetGenericTypeDefinition() : t;
                return defTok.GetCustomAttribute<BlueprintTypeAliasAttribute>()?.Alias;
            });

        /// <summary>
        /// 判断蓝图 TypeName 是否匹配某个 sentinel 类型(典型:<c>typeof(Composite&lt;&gt;)</c>)。
        /// <para>
        /// canonical 路径 = <paramref name="sentinelTypeDef"/> 上的 <see cref="BlueprintTypeAliasAttribute.Alias"/>。
        /// 容错路径 = .NET 反射格式 <see cref="Type.Name"/>(如 <c>"Composite`1"</c>) ——
        /// 早期 NodeFactory 漏归一化把闭合泛型名写到磁盘,留此条款让旧持久化文件平滑装载,不算维护协议变体。
        /// </para>
        /// </summary>
        public static bool MatchesAlias(string? typeName, Type sentinelTypeDef)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            var alias = GetAlias(sentinelTypeDef);
            if (alias != null && string.Equals(typeName, alias, StringComparison.Ordinal)) return true;
            // 容错:CLR 内部命名(IsGenericTypeDefinition.Name = "Composite`1")
            return string.Equals(typeName, sentinelTypeDef.Name, StringComparison.Ordinal);
        }
    }
}
