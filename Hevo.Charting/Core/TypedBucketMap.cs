using System.Collections.Concurrent;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 💥 工业级 0-GC 异构桶字典 (Heterogeneous Bucket Map)
    /// 核心设计：将不同类型的值路由到独立的强类型物理字典中，彻底消灭值类型的拆箱与装箱。
    /// </summary>
    public class TypedBucketMap
    {
        // 💥 外层使用 ConcurrentDictionary，保证在多线程初始化不同类型的桶时绝对安全。
        // 字典的 Key 是 typeof(KeyValuePair<TKey, TValue>)，Value 是强类型的 Dictionary<TKey, TValue>。
        private readonly ConcurrentDictionary<Type, object> _buckets = new();

        // 💥 与 _buckets 同步：每个桶对应的强类型 Clear 委托，避免清理阶段反射开销。
        private readonly ConcurrentDictionary<Type, Action<object>> _bucketClearers = new();

        /// <summary>
        /// 💥 核心魔法：获取强类型物理桶
        /// </summary>
        public Dictionary<TKey, TValue> GetBucket<TKey, TValue>() where TKey : notnull
        {
            // 使用 Key 和 Value 的复合类型作为唯一标识，防止不同类型的 Key 撞车
            var typeKey = typeof(KeyValuePair<TKey, TValue>);

            // 已存在直接 fast-path 返回，避免每次 GetBucket 都触碰 _bucketClearers。
            if (_buckets.TryGetValue(typeKey, out var existing))
            {
                return (Dictionary<TKey, TValue>)existing;
            }

            // 首次创建：必须先登记 Clearer，再发布 Bucket。
            // 这样 Clear() 在迭代 _buckets 看到新桶时，_bucketClearers 一定已可见，避免并发裂缝。
            _bucketClearers[typeKey] = static obj => ((Dictionary<TKey, TValue>)obj).Clear();
            var fresh = new Dictionary<TKey, TValue>();
            if (_buckets.TryAdd(typeKey, fresh))
            {
                return fresh;
            }
            // TryAdd 失败说明竞态下已有别人先登记，clearer 也是同一份委托，写覆盖等价。
            return (Dictionary<TKey, TValue>)_buckets[typeKey];
        }

        /// <summary>
        /// 💥 0-GC 安全读取
        /// </summary>
        public bool TryGetValue<TKey, TValue>(TKey key, out TValue value) where TKey : notnull
        {
            var bucket = GetBucket<TKey, TValue>();
            if (bucket.TryGetValue(key, out var val))
            {
                value = val;
                return true;
            }
            value = default!;
            return false;
        }

        /// <summary>
        /// 💥 0-GC 安全写入
        /// </summary>
        public void Set<TKey, TValue>(TKey key, TValue value) where TKey : notnull
        {
            var bucket = GetBucket<TKey, TValue>();
            bucket[key] = value;
        }

        /// <summary>
        /// 💥 一键清空所有数据，但保留桶的内存容量，避免触发后续 GC
        /// </summary>
        public void Clear()
        {
            // 直接走 GetBucket 缓存好的强类型 Clear 委托，零反射。
            foreach (var kv in _buckets)
            {
                if (_bucketClearers.TryGetValue(kv.Key, out var clearer))
                {
                    clearer(kv.Value);
                }
            }
        }
    }
}
