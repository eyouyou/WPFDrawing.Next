using System.Runtime.CompilerServices;

namespace Hevo.Charting.Core
{
    // ==========================================
    // 💥 FeatureContext 核心口袋声明
    // ==========================================
    public partial class FeatureContext
    {
        // 【终极口袋】：所有 Hook 的状态都存在这个字典里。
        // 因为它是基于 string (绝对路径+行号) 来查找的，所以你可以在 if、for 循环里随便写 Hook，
        // 执行顺序乱了也没关系，它永远能准确找到属于自己的那份内存！
        internal readonly Dictionary<string, object> _hookPocket = new();

        // 【运行时防爆探针】：用来标记“当前是不是正在执行用户的业务回调代码”。
        internal bool _isInsideHookFactory = false;
    }

    // ==========================================
    // 💥 智能 Hook 扩展实现
    // ==========================================
    public static class FeatureContextExtensions
    {
        // 局部状态容器：回归最简单的 long 计数，不再污染高贵的 VersionToken
        private class HookState<T>
        {
            public T Value = default!;
            public long LocalRevision = 1;
            public long LastSeenRevision = 0;
        }

        // 局部计算容器：依靠 IsDirty 标记
        private class MemoState<TVal, TDeps>
        {
            public TVal Value = default!;
            public TDeps Deps = default!;
            public bool IsDirty;
        }

        /// <summary>
        /// 💥 核心黑科技：编译期自动注入文件路径和行号，生成绝对唯一的 Key。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string GenerateHookKey(string file, int line, string? discriminator)
        {
            return discriminator == null ? $"{file}:{line}" : $"{file}:{line}#{discriminator}";
        }

        /// <summary>
        /// 💥 终极 UseMemo：零游标、零手写 Key、任意嵌套调用！
        /// </summary>
        public static (TResult Value, bool IsChanged) UseMemo<TDep, TResult>(
            this FeatureContext ctx,
            TDep dep,
            Func<TDep, TResult> factory,
            string? discriminator = null, // 仅当在循环中调用时才需要传
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            // 💥 防御塔一层：运行时熔断！
            if (ctx._isInsideHookFactory)
                throw new InvalidOperationException($"[Runtime Linter] 严禁在 Hook 的 factory 委托中嵌套调用其他 Hook！出错位置: {file} 行 {line}");

            string key = GenerateHookKey(file, line, discriminator);

            if (!ctx._hookPocket.TryGetValue(key, out var obj))
            {
                obj = new MemoState<TResult, TDep> { Value = default!, Deps = dep, IsDirty = true };
                ctx._hookPocket[key] = obj;
            }
            var state = (MemoState<TResult, TDep>)obj;

            bool changed = false;

            // 依靠 EqualityComparer 查脏，对于 Tuple/Record/ReadOnlyMemory 都是 0-GC
            if (!EqualityComparer<TDep>.Default.Equals(state.Deps, dep))
            {
                state.Deps = dep;
                state.IsDirty = true;
            }

            if (state.IsDirty)
            {
                // 💥 防御塔二层：点亮红灯，执行用户逻辑
                ctx._isInsideHookFactory = true;
                try
                {
                    state.Value = factory(dep);
                    changed = true; // 真实发生重算才报 changed
                }
                finally
                {
                    ctx._isInsideHookFactory = false; // 必灭红灯
                }
                state.IsDirty = false;
            }

            return (state.Value, changed);
        }

        /// <summary>
        /// 💥 终极 UseState：自动寻址，使用廉价的 LocalRevision 管理局部更新。
        /// </summary>
        public static (T Value, Action<T> SetValue, bool IsChanged) UseState<T>(
            this FeatureContext ctx,
            T initialValue,
            string? discriminator = null,
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (ctx._isInsideHookFactory)
                throw new InvalidOperationException($"[Runtime Linter] 严禁在 Hook 的 factory 委托中嵌套调用其他 Hook！出错位置: {file} 行 {line}");

            string key = GenerateHookKey(file, line, discriminator);

            if (!ctx._hookPocket.TryGetValue(key, out var obj))
            {
                obj = new HookState<T> { Value = initialValue };
                ctx._hookPocket[key] = obj;
            }
            var state = (HookState<T>)obj;

            bool changed = state.LocalRevision != state.LastSeenRevision;
            state.LastSeenRevision = state.LocalRevision;

            // SetValue 时仅推演局部 Revision
            return (state.Value, newValue => { state.Value = newValue; state.LocalRevision++; }, changed);
        }

        public static HevoRect GetPlotArea(this RenderContext ctx)
        {
            var areaTrait = ctx.Shared().Read<PlotAreaTrait>();
            if (areaTrait != null && areaTrait.Area.Width > 0 && areaTrait.Area.Height > 0)
            {
                return areaTrait.Area;
            }

            var vpTrait = ctx.Shared().Read<ViewportSizeTrait>();
            return vpTrait != null ? new HevoRect(0, 0, (float)vpTrait.Width, (float)vpTrait.Height) : HevoRect.Empty;
        }
    }
}
