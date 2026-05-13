using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// Phase 4 节点化:**wrapper 式** DS 反射探针 —— 蓝图层在 <c>object</c> 引用上反查 TItem + Stream 实例,
    /// 不强迫 <see cref="BufferedDataSource{TSource,TItem}"/> 基类实现任何"蓝图相关"接口。
    /// <para>
    /// 现状(2026-05):业务侧自由派生 <c>BufferedDataSource&lt;TSource, TItem&gt;</c>,蓝图层零侵入。
    /// framework 在 <see cref="BlueprintRunner"/> 装配时 cast `obj` → Inspect → 反射 + <see cref="Expression"/> 编译
    /// 出 <c>(object → object) Stream 取值委托</c>,首次扫一笔反射,后续按 <c>dsType</c> 命中缓存零开销。
    /// </para>
    /// <para>
    /// 跟早期 IBlueprintRunnable 接口的差别:
    /// </para>
    /// <list type="bullet">
    ///   <item>接口路径需要 <c>BufferedDataSource&lt;,&gt;</c> 显式实现,DS 基类被蓝图概念污染 —— 业务的 DS 派生类
    ///         读 doc/方法签名时会看到 <c>RunBlueprint</c> 这类不属于"数据源"职责的方法</item>
    ///   <item>wrapper 路径所有蓝图概念关在 <c>Hevo.Charting.LowCode.Designer</c> 命名空间,DS 基类只管"双缓冲读写分离 + Stream 推送"</item>
    /// </list>
    /// </summary>
    internal static class BlueprintDataSourceProbe
    {
        // 每个具体 DS 类型一笔缓存:TItem + 编译好的 Stream 委托。
        // ConcurrentDictionary key=Type 进程级共享,蓝图重载不需要重扫。
        private static readonly ConcurrentDictionary<Type, Info> _cache = new();

        private sealed class Info
        {
            public required Type ItemType { get; init; }
            public required Func<object, object> StreamGetter { get; init; }
        }

        /// <summary>反查 DS 实例的 TItem + Stream。失败抛 <see cref="InvalidOperationException"/>。</summary>
        public static (Type ItemType, object Stream) Inspect(object dsInstance)
        {
            if (dsInstance == null) throw new ArgumentNullException(nameof(dsInstance));
            var info = _cache.GetOrAdd(dsInstance.GetType(), BuildInfo);
            return (info.ItemType, info.StreamGetter(dsInstance));
        }

        private static Info BuildInfo(Type dsType)
        {
            // 沿继承链找 BufferedDataSource<TSource, TItem>,第 2 泛型参数 = 业务行类型。
            // 不直接匹配 DataSource<,> —— BufferedDataSource 继承 DataSource<TSource, DataSnapshot<TItem>>,
            // 那条路径上 T = DataSnapshot<TItem> 不是业务行字段类型(同 NodeFactory.FindDataSourceItemType 修复)。
            Type? itemType = null;
            for (var t = dsType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Hevo.Charting.BufferedDataSource<,>))
                {
                    itemType = t.GetGenericArguments()[1];
                    break;
                }
            }
            if (itemType == null)
                throw new InvalidOperationException(
                    $"{dsType.FullName} 不是 BufferedDataSource<TSource, TItem> 的派生类型 —— 节点化协议要求蓝图 DS 派生自 BufferedDataSource。");

            // Stream 属性在 DataSource<TSource, T> 基类上声明,返回 IWorkflow<T>。
            // 派生类继承自动可见 —— 直接 GetProperty 公开 BindingFlags 即可。
            var streamProp = dsType.GetProperty("Stream", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"{dsType.FullName}.Stream 属性反射不到 —— DataSource<,> 基类应该已经声明。");

            // 编译 (object obj) => (object)((dsType)obj).Stream —— 反射零开销,跟 IBlueprintDataSource 接口性能等价。
            var param = Expression.Parameter(typeof(object), "ds");
            var cast = Expression.Convert(param, dsType);
            var getter = Expression.Property(cast, streamProp);
            var box = Expression.Convert(getter, typeof(object));
            var streamGetter = Expression.Lambda<Func<object, object>>(box, param).Compile();

            return new Info { ItemType = itemType, StreamGetter = streamGetter };
        }

        /// <summary>
        /// 纯类型反射(不需要实例) —— 沿 <c>dsType</c> 继承链找
        /// <see cref="Hevo.Charting.BufferedDataSource{TSource,TItem}"/>,返回 TItem。
        /// 找不到返回 null。
        /// <para>
        /// 用途:<see cref="BlueprintRunner"/> 装配前要把开放泛型(<c>Composite&lt;&gt;</c>)闭合成
        /// 具体 <c>Composite&lt;TItem&gt;</c>,需要从 Upstreams[0].TypeName 反推 TItem,但此时上游 DS
        /// 还没实例化,所以走纯类型反射版本。
        /// </para>
        /// </summary>
        public static Type? GetItemType(Type dsType)
        {
            for (var t = dsType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Hevo.Charting.BufferedDataSource<,>))
                    return t.GetGenericArguments()[1];
            }
            return null;
        }

        /// <summary>
        /// 校验 <c>dsType</c> 是否可被 framework 装配(BufferedDataSource 派生 + 有 Stream 属性 + 无参 ctor)。
        /// 给 <see cref="BlueprintLauncher.DryRun"/> 用 —— 装配期早炸而不是首次 Run 时炸。
        /// </summary>
        public static bool IsValidDataSourceType(Type dsType, out string? error)
        {
            for (var t = dsType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Hevo.Charting.BufferedDataSource<,>))
                {
                    if (dsType.GetProperty("Stream", BindingFlags.Public | BindingFlags.Instance) == null)
                    {
                        error = $"{dsType.Name}.Stream 属性反射不到 —— DataSource<,> 基类应该已经声明。";
                        return false;
                    }
                    error = null;
                    return true;
                }
            }
            error = $"{dsType.Name} 不是 BufferedDataSource<TSource, TItem> 的派生类型。";
            return false;
        }
    }
}
