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

        /// <summary>
        /// 💥 核心魔法：获取强类型物理桶
        /// </summary>
        public Dictionary<TKey, TValue> GetBucket<TKey, TValue>() where TKey : notnull
        {
            // 使用 Key 和 Value 的复合类型作为唯一标识，防止不同类型的 Key 撞车
            var typeKey = typeof(KeyValuePair<TKey, TValue>);

            // 极简、线程安全的获取或创建逻辑
            return (Dictionary<TKey, TValue>)_buckets.GetOrAdd(
                typeKey,
                _ => new Dictionary<TKey, TValue>()
            );
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
            foreach (var bucketObj in _buckets.Values)
            {
                // 利用反射调用 Clear (发生在清理阶段，非高频渲染路径，性能可接受)
                var clearMethod = bucketObj.GetType().GetMethod("Clear");
                clearMethod?.Invoke(bucketObj, null);
            }
        }
    }
}
