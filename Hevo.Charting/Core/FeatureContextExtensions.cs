using System.Runtime.CompilerServices;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// Hook 寻址 key:[CallerFilePath] 注入的 file 是 intern string(同一编译单元同一字面量复用同一 instance),
    /// Equals 走 ReferenceEquals + int 比较,GetHashCode 用 identity hash —— 跳过 string 内容 hash + 内容比较,
    /// 替代旧 string-key 实现的 <c>$"{file}:{line}"</c> 拼接路径,稳态 0 GC + 微秒级查找。
    /// <para>
    /// IntDisc 给循环场景 0-GC discriminator(<c>i.ToString()</c> 那种 GC 不再);StrDisc 兼容旧调用。
    /// 二者互斥使用,默认 0/null。
    /// </para>
    /// </summary>
    internal readonly struct HookKey : IEquatable<HookKey>
    {
        public readonly string File;
        public readonly int Line;
        public readonly int IntDisc;
        public readonly string? StrDisc;

        public HookKey(string file, int line, int intDisc, string? strDisc)
        {
            File = file;
            Line = line;
            IntDisc = intDisc;
            StrDisc = strDisc;
        }

        // ReferenceEquals 命中 99.9%(intern string);极端跨编译单元同路径不命中时落 string.Equals 兜底。
        public bool Equals(HookKey other) =>
            Line == other.Line
            && IntDisc == other.IntDisc
            && (ReferenceEquals(File, other.File) || string.Equals(File, other.File, StringComparison.Ordinal))
            && string.Equals(StrDisc, other.StrDisc, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is HookKey other && Equals(other);

        // identity hash:RuntimeHelpers.GetHashCode 走 sync block index,常数时间,不算 string 内容。
        // file 是 intern string,同 callsite 永远同一 instance hash。
        public override int GetHashCode() =>
            HashCode.Combine(
                RuntimeHelpers.GetHashCode(File),
                Line,
                IntDisc,
                StrDisc);
    }

    // ==========================================
    // 💥 FeatureContext 核心口袋声明
    // ==========================================
    public partial class FeatureContext
    {
        // 【终极口袋】：所有 Hook 的状态都存在这个字典里。
        // key 由 (file, line, discriminator) 编译期注入,基于 reference identity 寻址,
        // 业务侧在 if/for 内随便写,执行顺序乱了也准确找到属于自己的那份内存。
        internal readonly Dictionary<HookKey, object> _hookPocket = new();

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
        /// 💥 终极 UseMemo：零游标、零手写 Key、任意嵌套调用！稳态 0 GC(HookKey 是 readonly struct)。
        /// </summary>
        public static (TResult Value, bool IsChanged) UseMemo<TDep, TResult>(
            this FeatureContext ctx,
            TDep dep,
            Func<TDep, TResult> factory,
            string? discriminator = null, // 仅当在循环中调用时才需要传(int 重载更优,见下)
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
            => UseMemoCore(ctx, dep, factory, new HookKey(file, line, 0, discriminator), file, line);

        /// <summary>
        /// 💥 循环友好版 UseMemo:int discriminator 直接进 HookKey,跳过 <c>i.ToString()</c> 的 GC。
        /// 调用方式:<c>ctx.UseMemo(dep, factory, discriminator: i)</c>。
        /// </summary>
        public static (TResult Value, bool IsChanged) UseMemo<TDep, TResult>(
            this FeatureContext ctx,
            TDep dep,
            Func<TDep, TResult> factory,
            int discriminator,
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
            => UseMemoCore(ctx, dep, factory, new HookKey(file, line, discriminator, null), file, line);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (TResult Value, bool IsChanged) UseMemoCore<TDep, TResult>(
            FeatureContext ctx,
            TDep dep,
            Func<TDep, TResult> factory,
            HookKey key,
            string file,
            int line)
        {
            // 💥 防御塔一层：运行时熔断！
            if (ctx._isInsideHookFactory)
                throw new InvalidOperationException($"[Runtime Linter] 严禁在 Hook 的 factory 委托中嵌套调用其他 Hook！出错位置: {file} 行 {line}");

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
        /// 💥 终极 UseState：自动寻址，使用廉价的 LocalRevision 管理局部更新。稳态 0 GC。
        /// </summary>
        public static (T Value, Action<T> SetValue, bool IsChanged) UseState<T>(
            this FeatureContext ctx,
            T initialValue,
            string? discriminator = null,
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
            => UseStateCore(ctx, initialValue, new HookKey(file, line, 0, discriminator), file, line);

        /// <summary>
        /// 💥 循环友好版 UseState:int discriminator,跳过 <c>i.ToString()</c> 的 GC。
        /// </summary>
        public static (T Value, Action<T> SetValue, bool IsChanged) UseState<T>(
            this FeatureContext ctx,
            T initialValue,
            int discriminator,
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
            => UseStateCore(ctx, initialValue, new HookKey(file, line, discriminator, null), file, line);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (T Value, Action<T> SetValue, bool IsChanged) UseStateCore<T>(
            FeatureContext ctx,
            T initialValue,
            HookKey key,
            string file,
            int line)
        {
            if (ctx._isInsideHookFactory)
                throw new InvalidOperationException($"[Runtime Linter] 严禁在 Hook 的 factory 委托中嵌套调用其他 Hook！出错位置: {file} 行 {line}");

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
