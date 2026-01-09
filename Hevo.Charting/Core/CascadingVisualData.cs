using Hevo.Charting.Abstractions;
using System.Buffers;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 级联数据视图：只在 Layer.Update 瞬间存在
    /// 职责：让 Layer 感觉不到数据来源的区别，优先读局部，兜底读全局
    /// </summary>
    internal interface IChildDataProvider
    {
        IVisualData? GetChildData(IChartLayer child);
    }

    /// <summary>
    /// 快照寻址器：允许在当前帧的快照中查找特定图层的数据
    /// </summary>
    internal interface IFrameSnapshotProvider
    {
        object[] GetLocalSnapshot(IChartLayer layer);
    }

    /// <summary>
    /// 基于池化快照数组的级联透镜。
    /// 绝对线程安全，提供 O(1) 极速读取，支持 DOM 树级联。
    /// </summary>
    public readonly struct CascadingVisualData : IVisualData, IChildDataProvider
    {
        private readonly object[] _localSnap;
        private readonly object[] _globalSnap;
        private readonly IFrameSnapshotProvider? _frameProvider;

        internal CascadingVisualData(
            object[] localSnap,
            object[] globalSnap,
            IFrameSnapshotProvider? frameProvider = null)
        {
            _localSnap = localSnap;
            _globalSnap = globalSnap;
            _frameProvider = frameProvider;
        }

        public object? GetById(int id)
        {
            // 1. 查局部快照 (层级数据)
            if (id < _localSnap.Length)
            {
                var obj = _localSnap[id];
                if (obj != null) return obj;
            }

            // 2. 查全局快照 (全局样式/主题)
            if (id < _globalSnap.Length)
            {
                var obj = _globalSnap[id];
                if (obj != null) return obj;
            }

            return null;
        }
        // --- 核心：只读获取，自带级联回退 ---
        public T? Get<T>() where T : class
        {
            int id = TraitId<T>.Id;

            if (id < _localSnap.Length)
            {
                var obj = _localSnap[id];
                if (obj != null) return (T)obj; // 极限性能：因为槽位类型绝对安全，直接硬转
            }

            if (id < _globalSnap.Length)
            {
                var obj = _globalSnap[id];
                if (obj != null) return (T)obj; // 极限性能：同上
            }

            return null;
        }

        // --- 写入方法：安全拦截 ---
        public void Publish<T>(T snapshot) where T : class
        {
            // 【架构安全红线】
            // 在多线程渲染阶段 (OnUpdate)，数据已经是快照了。
            // 严禁在此处修改数据！所有修改必须在主线程的 .Plot() 阶段通过 RenderContext 进行！
            throw new InvalidOperationException("Fatal: 严禁在图层渲染(OnUpdate)期间修改 VisualData 数据！请在 Plot 阶段写入。");
        }

        // --- 子层级联 (DOM 树支持) ---
        public IVisualData? GetChildData(IChartLayer child)
        {
            if (_frameProvider == null)
            {
                return new CascadingVisualData(Array.Empty<object>(), _globalSnap, null);
            }

            // 直接向当前帧的快照管理器索要子图层的安全数组！0 GC！
            var childLocalSnap = _frameProvider.GetLocalSnapshot(child);
            return new CascadingVisualData(childLocalSnap, _globalSnap, _frameProvider);
        }
    }

    /// <summary>
    /// 图表的帧数据存储
    /// </summary>
    public sealed class FrameSnapshot : IDisposable, IFrameSnapshotProvider
    {
        public readonly List<RenderTask> Tasks = new();

        private readonly object[] _globalSnap;
        // 使用结构体数组替代字典，彻底消灭字典扩容和 Node 节点的内存分配
        private readonly KeyValuePair<IChartLayer, object[]>[] _layerMap;
        private readonly int _layerCount;

        internal FrameSnapshot(object[] globalSnap, KeyValuePair<IChartLayer, object[]>[] layerMap, int layerCount)
        {
            _globalSnap = globalSnap;
            _layerMap = layerMap;
            _layerCount = layerCount;
        }

        // 实现寻址接口
        public object[] GetLocalSnapshot(IChartLayer layer)
        {
            // 极限性能：对于少量图层，线性遍历比 Hash 字典查找快得多
            for (int i = 0; i < _layerCount; i++)
            {
                if (ReferenceEquals(_layerMap[i].Key, layer))
                {
                    return _layerMap[i].Value;
                }
            }
            return Array.Empty<object>();
        }

        public void Dispose()
        {
            // 1. 归还全局数组
            if (_globalSnap.Length > 0) ArrayPool<object>.Shared.Return(_globalSnap, clearArray: true);

            // 2. 归还所有局部数组
            for (int i = 0; i < _layerCount; i++)
            {
                var localArr = _layerMap[i].Value;
                if (localArr.Length > 0) ArrayPool<object>.Shared.Return(localArr, clearArray: true);
            }

            // 3. 归还 Map 本身
            if (_layerMap.Length > 0) ArrayPool<KeyValuePair<IChartLayer, object[]>>.Shared.Return(_layerMap, clearArray: true);
        }
    }
}
