using System.Reflection;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// 💥 蓝图组件自动登记器:扫描程序集,把可被 JSON 蓝图引用的类型按名字塞进 <see cref="ComponentRegistry"/>。
    /// <para>
    /// 业务侧典型用法:
    /// <code>
    /// // 应用启动时挂一次,业务的 DataSource / 自定义 Trait 也跟着登记
    /// BuiltinRegistration.RegisterBuiltins();
    /// BuiltinRegistration.RegisterAssemblyOf&lt;TimeShareDataSource&gt;();
    /// </code>
    /// </para>
    /// <para>
    /// 收录条件 (ChartFeature):public、非抽象、非泛型定义、有 public 无参 ctor。
    /// 收录条件 (IVisualTrait):public、非抽象、非泛型定义。无参 ctor 不强制 (允许 record 走 Preset)。
    /// 收录条件 (DataSource):public、非抽象、非泛型定义。
    /// </para>
    /// </summary>
    public static class BuiltinRegistration
    {
        /// <summary>登记 Hevo.Charting 程序集自带的 Feature / Trait / DataSource 基类。</summary>
        public static void RegisterBuiltins()
        {
            RegisterAssembly(typeof(ChartFeature).Assembly);
        }

        /// <summary>按某类型所在程序集做批量登记 (业务侧自定义组件用)。</summary>
        public static void RegisterAssemblyOf<T>() => RegisterAssembly(typeof(T).Assembly);

        /// <summary>扫描 <paramref name="assembly"/>,把符合收录条件的类型登记进 <see cref="ComponentRegistry"/>。</summary>
        public static void RegisterAssembly(Assembly assembly)
        {
            if (assembly is null) throw new ArgumentNullException(nameof(assembly));

            Type[] allTypes;
            try { allTypes = assembly.GetTypes(); }
            // 程序集里掺了部分加载失败的类型时仍尽力登记可用部分,免得整批翻车
            catch (ReflectionTypeLoadException ex) { allTypes = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var type in allTypes)
            {
                if (type == null || !type.IsPublic || type.IsAbstract || type.IsGenericTypeDefinition) continue;

                if (typeof(ChartFeature).IsAssignableFrom(type))
                {
                    if (!HasParameterlessCtor(type)) continue;
                    ComponentRegistry.Register(type);
                }
                else if (typeof(IVisualTrait).IsAssignableFrom(type))
                {
                    ComponentRegistry.Register(type);
                }
                else if (LooksLikeDataSource(type))
                {
                    ComponentRegistry.Register(type);
                }
            }
        }

        /// <summary>
        /// 业务侧也常用 Type 直接登记,补 ComponentRegistry 上一个非泛型 API 即可。
        /// </summary>
        private static bool HasParameterlessCtor(Type type)
            => type.GetConstructor(Type.EmptyTypes) != null;

        /// <summary>
        /// 沿继承链找 <see cref="DataSource{TSource, TItem}"/>。我们不想把 IDisposable / IPausable 全收录,
        /// 只挑符合"图表数据源"约定的类型。
        /// </summary>
        private static bool LooksLikeDataSource(Type type)
        {
            var t = type.BaseType;
            while (t != null && t != typeof(object))
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DataSource<,>)) return true;
                t = t.BaseType;
            }
            return false;
        }
    }
}
