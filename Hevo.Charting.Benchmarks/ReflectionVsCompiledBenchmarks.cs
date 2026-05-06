using System.Reflection;
using BenchmarkDotNet.Attributes;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// §2 ctor 实例化:Activator.CreateInstance vs ComponentRegistry 编译委托。
    /// 用 EmptyPoco 而非 LineSeriesFeature —— 避免 ctor 内部重活 (~330KB 分配) 淹没 dispatch 开销。
    /// EmptyPoco ctor 接近纯 newobj,反射 vs 编译委托的差距才看得到。
    /// </summary>
    [MemoryDiagnoser]
    public class CtorReflectionVsCompiledBenchmarks
    {
        public class EmptyPoco { }
        private readonly Type _type = typeof(EmptyPoco);

        public CtorReflectionVsCompiledBenchmarks()
        {
            // 预热编译缓存,排除 first-call 编译成本对测准的污染。
            _ = ComponentRegistry.CreateInstance(_type);
        }

        [Benchmark(Baseline = true, Description = "Activator.CreateInstance(type) — 反射")]
        public object Activator_CreateInstance()
            => Activator.CreateInstance(_type)!;

        [Benchmark(Description = "ComponentRegistry.CreateInstance(type) — 编译委托缓存")]
        public object ComponentRegistry_CreateInstance()
            => ComponentRegistry.CreateInstance(_type);
    }

    /// <summary>
    /// §3 属性注入:PropertyInfo.SetValue vs SmartActivator.InjectProperties(走编译 setter 缓存)。
    /// 两边都跑一次"找属性 + 写值"完整路径,公平对比 dispatch 链。
    /// </summary>
    [MemoryDiagnoser]
    public class SetterReflectionVsCompiledBenchmarks
    {
        private readonly Type _ownerType = typeof(LineSeriesFeature);
        private readonly LineSeriesFeature _target = new();
        private PropertyInfo _propInfo = null!;
        private readonly Dictionary<string, object?> _injectDict = new()
        {
            [nameof(LineSeriesFeature.MetaName)] = "x",
        };

        public SetterReflectionVsCompiledBenchmarks()
        {
            GraphViewerBootstrap.Initialize();
            _propInfo = _ownerType.GetProperty(nameof(LineSeriesFeature.MetaName))!;
            SmartActivator.InjectProperties(_target, _injectDict);  // 预热 setter cache
        }

        [Benchmark(Baseline = true, Description = "PropertyInfo.SetValue — 反射 (含 GetProperty 一次)")]
        public void Reflection_GetProperty_SetValue()
        {
            // 公平对比:每次都模拟 SmartActivator 老路径 (GetProperty + SetValue),
            // 编译版的 InjectProperties 内部已经把 GetProperty 缓存了。
            var pi = _ownerType.GetProperty(nameof(LineSeriesFeature.MetaName),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            pi!.SetValue(_target, "x");
        }

        [Benchmark(Description = "SmartActivator.InjectProperties — 编译 setter 缓存")]
        public void Compiled_InjectProperties()
            => SmartActivator.InjectProperties(_target, _injectDict);

        [Benchmark(Description = "PropertyInfo.SetValue — 反射 (无 GetProperty,直接写)")]
        public void Reflection_SetValueDirect()
            => _propInfo.SetValue(_target, "x");
    }

    /// <summary>
    /// §4 Seed dispatch:MakeGenericMethod + Invoke vs 编译 Action 缓存。
    /// 不实际跑 Seed body (避免 IFeatureContext 实例化干扰),只比 dispatch overhead 本身。
    /// </summary>
    [MemoryDiagnoser]
    public class SeedDispatchReflectionVsCompiledBenchmarks
    {
        private readonly Type _traitType = typeof(BenchmarkTrait);
        private readonly BenchmarkTrait _traitInstance = BenchmarkTrait.Default;
        private readonly StubFeatureContext _stub = new();
        private MethodInfo _seedOpenGeneric = null!;

        // 自己仿照 ChartBlueprint 内部那套缓存逻辑写一份测试用的 ——
        // 跟生产代码同语义,但放在 benchmark 项目内方便测准。
        private static readonly Dictionary<Type, Action<IFeatureContext, object>> _localSeedCache = new();

        public SeedDispatchReflectionVsCompiledBenchmarks()
        {
            GraphViewerBootstrap.Initialize();
            _seedOpenGeneric = typeof(IFeatureContext).GetMethod(nameof(IFeatureContext.Seed))!;
            // 预热缓存
            _ = GetSeedInvoker(_traitType);
        }

        [Benchmark(Baseline = true, Description = "MakeGenericMethod + Invoke — 反射 (每次)")]
        public object? Reflection_MakeGenericInvoke()
            => _seedOpenGeneric.MakeGenericMethod(_traitType).Invoke(_stub, new object[] { _traitInstance });

        [Benchmark(Description = "缓存的编译 Action<IFeatureContext, object>")]
        public void Compiled_CachedDelegate()
            => GetSeedInvoker(_traitType)(_stub, _traitInstance);

        private Action<IFeatureContext, object> GetSeedInvoker(Type traitType)
        {
            if (_localSeedCache.TryGetValue(traitType, out var d)) return d;
            var ctxParam   = System.Linq.Expressions.Expression.Parameter(typeof(IFeatureContext), "ctx");
            var traitParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "trait");
            var seedClosed = _seedOpenGeneric.MakeGenericMethod(traitType);
            var call       = System.Linq.Expressions.Expression.Call(
                ctxParam, seedClosed,
                System.Linq.Expressions.Expression.Convert(traitParam, traitType));
            var compiled = System.Linq.Expressions.Expression
                .Lambda<Action<IFeatureContext, object>>(call, ctxParam, traitParam)
                .Compile();
            _localSeedCache[traitType] = compiled;
            return compiled;
        }

        // 最小 IFeatureContext stub —— 只给 Seed<T> 一个 no-op 实现,避免依赖真实 ChartCell。
        // 接口其它方法均不被 benchmark 触发,直接 no-op 满足契约即可。
        private sealed class StubFeatureContext : IFeatureContext
        {
            public IFeatureContext Seed<T>(T trait) where T : class, IVisualTrait => this;
            public IFeatureContext Add(ChartFeature feature) => this;
            public IFeatureContext Remove<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature => this;
            public void Remove(ChartFeature feature) { }
            public void Transact(Action<IFeatureContext> action) => action(this);
            public bool HasFeature<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature => false;
            public TFeature? Find<TFeature>(Func<TFeature, bool>? predicate = null) where TFeature : ChartFeature => null;
        }
    }

    /// <summary>用作 Seed&lt;T&gt; 调度测试的 trait。无业务含义,仅满足 IVisualTrait 约束。</summary>
    public record BenchmarkTrait(double X = 1.0) : IVisualTrait
    {
        public static readonly BenchmarkTrait Default = new();
    }
}
