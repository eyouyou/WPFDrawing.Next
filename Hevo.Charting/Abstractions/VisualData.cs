using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 标记接口：代表这是一个全局/共享的作用域
    /// </summary>
    public interface ISharedScope
    {

    }
    /// <summary>
    /// 视觉特征标记接口。
    /// 实现此接口的类必须是纯数据类 (POCO)，严禁包含任何 UI 控件引用或业务逻辑。
    /// </summary>
    public interface IVisualTrait { }

    /// <summary>
    /// TTrait 是这个 Layer 支持配置的数据包类型
    /// 这个接口虽然是空的，但它承载了极其重要的类型信息
    /// </summary>
    /// <typeparam name="TTrait"></typeparam>
    public interface IConsumes<TTrait> where TTrait : IVisualTrait
    {
    }

    public abstract class VisualTrait : IVisualTrait, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void NotifyProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        protected void NotifyProperty([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
    /// <summary>
    /// TODO: 是否需要dispose
    /// </summary>
    public interface IVisualData: ISharedScope
    {
        /// <summary>
        /// Publish (发布/替换)
        /// 此时此刻，旧的已死，新的当立。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        void Publish<T>(T snapshot) where T : class;

        /// <summary>
        /// Trait: 只有 Config (配置/修改) - 强制 Lambda
        /// 你拿不到引用，你别想存，你别想替换。
        /// 你只能在括号里乖乖改属性。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="configure"></param>
        // void Config<T>(Action<T> configure) where T : class, INotifyPropertyChanged, new();

        /// <summary>
        /// Read: 只有 Get (只读)
        /// 谁都能用：Layer 用来画，Tooltip 用来显，Exporter 用来存。
        /// 返回可空，时刻提醒你数据可能还没准备好。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        T? Get<T>() where T : class;
    }

    /// <summary>
    /// 物理存储容器：实现 IVisualData。
    /// 内部封装了 ConcurrentDictionary 以支持高并发读取。
    /// </summary>
// ==========================================
    // 0. 全局发号器 (空间换时间的魔法)
    // ==========================================
    internal static class TraitIndexer
    {
        // Trait 类型总数硬上限。正常用例 < 200,设置 1024 给二次开发留 5x 余地;
        // 命中即说明存在 trait 泛型滥用(例如把业务实体类塞进 trait 槽位),应排查而非扩容。
        public const int MaxTraitTypes = 1024;

        private static int _counter = -1;
        public static int Next()
        {
            int next = Interlocked.Increment(ref _counter);
            if (next >= MaxTraitTypes)
                throw new InvalidOperationException(
                    $"已注册的 IVisualTrait 类型超过硬上限 {MaxTraitTypes}。请检查是否有泛型滥用导致 TraitId<T> 不断膨胀。");
            return next;
        }
        public static int CurrentCount => Volatile.Read(ref _counter) + 1;
    }

    public static class TraitId<T> where T : class
    {
        // 泛型静态缓存：JIT 编译期就会把它优化为极致的常量访问
        public static readonly int Id = TraitIndexer.Next();
    }

    // ==========================================
    // 核心实现：基于数组的 0 GC 数据袋
    // ==========================================
    public sealed class VisualDataBag : IVisualData
    {
        // 核心仓库：彻底私有。初始 0 内存分配！
        private object[] _repo = Array.Empty<object>();

        // 事务追踪：仅记录本次操作修改了哪些 ID，让 Commit 和 Clear 速度拉满
        private readonly List<int> _changedIds = new();

        // ==========================================
        // 1. IVisualData 基础接口实现
        // ==========================================

        /// <summary>
        /// 发布/覆盖数据快照 (Record)。
        /// </summary>
        public void Publish<T>(T snapshot) where T : class
        {
            if (snapshot == null) return;

            int id = TraitId<T>.Id;

            // 动态扩容：只在刚启动“预热期”触发，此后 0 GC
            if (id >= _repo.Length)
            {
                // 参考全局已注册的 Trait 总量进行冗余分配，确保极少发生 Resize
                int targetSize = Math.Max(id + 1, TraitIndexer.CurrentCount + 16);
                Array.Resize(ref _repo, targetSize);
            }

            _repo[id] = snapshot;
            _changedIds.Add(id);
        }

        /// <summary>
        /// 读取指定类型的数据快照。
        /// </summary>
        public T? Get<T>() where T : class
        {
            int id = TraitId<T>.Id;
            // O(1) 极限数组索引访问
            if (id >= _repo.Length) return null;
            return _repo[id] as T;
        }

        // ==========================================
        // 2. 内部事务管理 API (供 RenderContext 使用)
        // ==========================================

        /// <summary>
        /// 检查容器是否为空。
        /// </summary>
        internal bool IsEmpty => _changedIds.Count == 0;

        /// <summary>
        /// 原子提交：将草稿箱 (Draft) 中的数据合并到当前容器。
        /// </summary>
        internal void CommitFrom(VisualDataBag? draftBag)
        {
            if (draftBag == null || draftBag.IsEmpty) return;

            // 如果草稿箱的数组比当前正式库大，正式库需要同步扩容
            if (draftBag._repo.Length > _repo.Length)
            {
                Array.Resize(ref _repo, draftBag._repo.Length);
            }

            // 精准覆盖：只搬运草稿箱里发生过变化的 Trait，极大节省 CPU 周期
            foreach (int id in draftBag._changedIds)
            {
                _repo[id] = draftBag._repo[id];
            }
        }

        /// <summary>
        /// 清空草稿箱数据，避免内存泄漏。
        /// </summary>
        internal void Clear()
        {
            // 精准清理：只把被弄脏的位置置空，不遍历整个数组！
            foreach (int id in _changedIds)
            {
                _repo[id] = null!;
            }
            _changedIds.Clear();
        }

        // ==========================================
        // 3. 多线程渲染的终极杀器 (选配)
        // ==========================================

        /// <summary>
        /// 借出一个绝对线程安全的只读快照数组。
        /// 用完后请务必调用 ArrayPool<object>.Shared.Return() 归还。
        /// </summary>
        internal object[] RentSnapshot()
        {
            if (_repo.Length == 0) return Array.Empty<object>();

            var snap = ArrayPool<object>.Shared.Rent(_repo.Length);
            Array.Copy(_repo, snap, _repo.Length);
            return snap;
        }

        /// <summary>
        /// 供底层引擎 (SubmitSync) 进行 O(1) 极速对象引用比对。0 GC。
        /// </summary>
        internal object? GetById(int id)
        {
            if (id >= _repo.Length) return null;
            return _repo[id];
        }
    }
}