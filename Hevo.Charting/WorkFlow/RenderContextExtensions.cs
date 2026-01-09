using System.Runtime.CompilerServices;

namespace Hevo.Charting.WorkFlow
{
    /// <summary>
    /// 💥 记忆化宿主契约：任何实现此接口的类，自动解锁 0 GC 的 Memo 超能力！
    /// </summary>
    public interface IMemoHost
    {
        Dictionary<string, object> MemoStore { get; }
    }

    /// <summary>
    /// 💥 图表业务特征专属的高级扩展库
    /// </summary>
    public static class MemoExtensions
    {
        // 私有缓存记录结构
        private record MemoCache<TArgs, TResult>(TArgs Args, TResult Result);

        /// <summary>
        /// 💥 极速缓存：只要入参 arg1 和 arg2 不变，就直接返回上次的结果，跳过 factory 运算！
        /// </summary>
        public static TResult Memo<T1, T2, TResult>(
            this IMemoHost host,
            T1 arg1, T2 arg2,
            Func<T1, T2, TResult> factory,
            [CallerLineNumber] int line = 0) // 自动拿调用处的行号当 Key，绝不冲突！
        {
            string key = line.ToString();
            var currentArgs = (arg1, arg2); // 利用 C# 元组 (ValueTuple) 进行极速值比较

            // 1. 尝试读缓存
            if (host.MemoStore.TryGetValue(key, out var cachedObj) &&
                cachedObj is MemoCache<(T1, T2), TResult> cache)
            {
                // 如果参数完全一致，直接返回之前算好的结果
                if (EqualityComparer<(T1, T2)>.Default.Equals(cache.Args, currentArgs))
                {
                    return cache.Result;
                }
            }

            // 2. 参数变了（或首次执行），重新计算
            var newResult = factory(arg1, arg2);

            // 3. 覆盖缓存
            host.MemoStore[key] = new MemoCache<(T1, T2), TResult>(currentArgs, newResult);

            return newResult;
        }

        public static TResult Memo<T1, T2, T3, T4, TResult>(
            this IMemoHost host,
            T1 arg1, T2 arg2, T3 arg3, T4 arg4,
            Func<T1, T2, T3, T4, TResult> factory,
            [CallerLineNumber] int line = 0)
        {
            // 💥 关键：把 4 个参数打包成一个 ValueTuple 进行值比较
            string key = line.ToString();
            var currentArgs = (arg1, arg2, arg3, arg4);

            // 1. 尝试从存储中读取缓存
            if (host.MemoStore.TryGetValue(key, out var cachedObj) &&
                cachedObj is MemoCache<(T1, T2, T3, T4), TResult> cache)
            {
                // 2. 如果 4 个参数都没变，直接返回结果，跳过 factory 执行
                if (EqualityComparer<(T1, T2, T3, T4)>.Default.Equals(cache.Args, currentArgs))
                {
                    return cache.Result;
                }
            }

            // 3. 只要有一个参数变了，重新计算并更新缓存
            var newResult = factory(arg1, arg2, arg3, arg4);
            host.MemoStore[key] = new MemoCache<(T1, T2, T3, T4), TResult>(currentArgs, newResult);

            return newResult;
        }
    }
}
