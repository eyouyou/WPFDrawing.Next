using System;
using System.Collections.Generic;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer;

namespace Hevo.Charting.WorkFlow
{
    /// <summary>
    /// 纯合并器基类 —— 把 N 路同 <typeparamref name="TItem"/> 的上游 <see cref="IWorkflow{T}"/> 接到自身,
    /// 派生类 override <see cref="Merge"/> 决定合并语义(replace-by-slot / union / dedupe / 自定义 reducer)。
    ///
    /// <para>
    /// <b>调用约定 — 业务可以完全脱离蓝图程序化使用</b>:
    /// </para>
    /// <code>
    /// var merger = new MyMerger();
    /// merger.Attach(upstreamA.Stream);   // slot 0
    /// merger.Attach(upstreamB.Stream);   // slot 1
    /// merger.Stream.Subscribe(snap => ...);
    /// </code>
    /// <para>
    /// 蓝图侧自动装配走 <see cref="DataSourceModel.UpstreamRefs"/> 一等节点引用 ——
    /// framework 替业务把 upstream DS.Stream <see cref="Attach"/> 进来。<see cref="CompositeDataSource{TSource,TItem}"/>
    /// 自己不感知"蓝图"概念,也不调 LoadAsync —— 上游 DS 的 lifecycle 由 framework / 业务自己管。
    /// </para>
    /// <para>
    /// <b>跟旧版的区别</b>:旧 <c>Upstreams[]</c> inline spec / <c>UpstreamIds[]</c> / <c>InitializeUpstreams</c> 全部移除 ——
    /// 那些都是"蓝图味"很重的字段,污染了"我就想合个流"的领域基类。inline 场景蓝图侧改用 node-wrap
    /// (每个上游一个独立 DataSourceModel + UpstreamRefs 引用),程序化用法直接 <see cref="Attach"/>。
    /// </para>
    /// </summary>
    public abstract class CompositeDataSource<TSource, TItem> : BufferedDataSource<TSource, TItem>
        where TSource : CompositeDataSource<TSource, TItem>
    {
        // 上游订阅句柄 —— Dispose 时一并解绑。Attach 顺序 = slot 顺序。
        private readonly List<IDisposable> _upstreamSubs = new();

        /// <summary>已 <see cref="Attach"/> 的上游数量(= slot 总数)。派生类的 <see cref="ShouldPublishAfterMerge"/> / merge 状态机用它判 WhenAll 是否凑齐。</summary>
        public int UpstreamCount { get; private set; }

        /// <summary>LogicalLength 默认 = 当前展柜数组长度;派生类有自定义"逻辑长度"(KLine 那种 row-of-Block)时 override。</summary>
        public override int LogicalLength => _readSnapshot.Length;

        /// <summary>
        /// 接一路上游 Stream,slotIndex 按 Attach 顺序递增。返回的 IDisposable 解绑该路订阅;
        /// composite Dispose 时所有 Attach 出来的 sub 都一起释放。
        /// <para>
        /// 线程安全:本方法本身不加锁(典型调用在装配期 UI 线程单线程);并发 Attach 不支持。
        /// 但 Attach 期间收到的上游 publish 会跑去 <see cref="Merge"/>,后者在 <c>_lock</c> 内执行,数据竞争安全。
        /// </para>
        /// </summary>
        public IDisposable Attach(IWorkflow<DataSnapshot<TItem>> stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            int slot = UpstreamCount++;
            var sub = stream.Subscribe(snap => UpdateAndPublish(buffer => Merge(buffer, slot, snap), slot));
            _upstreamSubs.Add(sub);
            return sub;
        }

        /// <summary>
        /// 业务侧合并策略:把上游第 <paramref name="slotIndex"/> 路 publish 来的 <paramref name="upstreamSnapshot"/>
        /// 合到当前 <paramref name="buffer"/>。dedupe / sort / replace / 丢旧保新 全由派生类决定。
        /// <para>
        /// 调用约定:framework 在 <c>_lock</c> 锁下 invoke,本方法返回后由 <see cref="ShouldPublishAfterMerge"/> 决定是否
        /// <see cref="BufferedDataSource{TSource,TItem}.Publish()"/>。不要在本方法里访问其他 DS 状态(可能死锁);只动 <paramref name="buffer"/>。
        /// </para>
        /// </summary>
        protected abstract void Merge(List<TItem> buffer, int slotIndex, DataSnapshot<TItem> upstreamSnapshot);

        /// <summary>
        /// Publish gate —— framework 在 Merge 返回后、Publish 前调用,返回 false 跳过本次 Publish。
        /// 默认放行(WhenAny 语义);派生类 override 实现 WhenAll 首发门槛 / 业务自定义 throttle。
        /// </summary>
        protected virtual bool ShouldPublishAfterMerge(int slotIndex) => true;

        // 在 _lock 内修改 buffer + (条件)Publish —— 直接复用 BufferedDataSource 的双缓冲机制。
        private void UpdateAndPublish(Action<List<TItem>> mutation, int slotIndex)
        {
            lock (_lock)
            {
                mutation(_buffer);
                if (ShouldPublishAfterMerge(slotIndex)) Publish();
            }
        }

        public override void Dispose()
        {
            foreach (var s in _upstreamSubs) try { s.Dispose(); } catch { }
            _upstreamSubs.Clear();
            base.Dispose();
        }
    }

    /// <summary>
    /// Composite 上游汇聚语义。
    /// <list type="bullet">
    ///   <item><see cref="WhenAny"/>(默认):任一上游 publish 即立刻 republish。上游可能不齐;下游 cascade 会被多打几次。</item>
    ///   <item><see cref="WhenAll"/>:所有上游各 publish 过至少一次后才首次 republish;之后退化为 WhenAny(任一上游再 publish 都立刻 republish,
    ///         保持实时性)。适合"下游 fetch 成本高、不想被半清单触发"场景(典型:scanner V2 批量请求)。</item>
    /// </list>
    /// </summary>
    public enum MergeMode
    {
        WhenAny = 0,
        WhenAll = 1,
    }

    /// <summary>
    /// 蓝图全配置式合并 DS —— 业务侧 0 类定义,蓝图 JSON 完整描述:
    /// <list type="bullet">
    ///   <item>TypeName="Composite"(框架内置 sentinel,蓝图协议层识别)</item>
    ///   <item><see cref="DataSourceModel.UpstreamRefs"/> = 上游 DataSource 节点 Id 列表(node-wrap)</item>
    ///   <item>Properties.MergeMode = WhenAny / WhenAll(<see cref="WorkFlow.MergeMode"/>,默认 WhenAny)</item>
    ///   <item>Properties.MergeHandler(可选) = <see cref="BlueprintHandlerAttribute"/> 名,签名
    ///     <c>void (List&lt;TItem&gt;, int, DataSnapshot&lt;TItem&gt;)</c>;缺省走默认 replace-by-slot 语义</item>
    /// </list>
    /// <para>
    /// <b>默认语义(零 handler)</b> —— "N 上游各占一个 slot,某上游 publish 新 snapshot 替换它自己的 slot,
    /// 其余上游 slot 不动";主 buffer 按 slot 顺序拼接全部 slot。等价于"展示 N 个独立数据源的当前状态",
    /// 90% 单纯合并用例直接吃这个 free,无需写 handler。
    /// </para>
    /// <para>
    /// <b>handler 覆盖</b>(可选) —— 需要 dedupe / sort / 条件过滤 这种业务策略时,业务写一个静态
    /// <see cref="BlueprintHandlerAttribute"/> 方法,蓝图 MergeHandler 字段填名字即可。完全接管 Merge,framework
    /// 默认 slot 逻辑不再 fire。
    /// </para>
    /// <para>
    /// TItem 由 framework 反射上游 DS 的 TItem 推断;装配期 <c>MakeGenericType</c> 闭合 <c>Composite&lt;TItem&gt;</c>。
    /// </para>
    /// </summary>
    [Hevo.Charting.LowCode.Designer.BlueprintTypeAlias("Composite")]
    public sealed class Composite<TItem> : CompositeDataSource<Composite<TItem>, TItem>, IHandlerAware
    {
        /// <summary>
        /// 可选合并策略 handler 名。空时走默认 replace-by-slot 语义。
        /// <para>
        /// handler 签名:<c>void M(List&lt;TItem&gt; buffer, int slotIndex, DataSnapshot&lt;TItem&gt; snapshot)</c>。
        /// framework 在第一次 Merge 时 lazy resolve,后续帧零反射开销;未注册 / 签名不匹配 → 打 warning 后退化为默认语义。
        /// </para>
        /// </summary>
        public string? MergeHandler { get; init; }

        /// <summary>
        /// 上游汇聚语义,详见 <see cref="WorkFlow.MergeMode"/>。
        /// <para>蓝图 JSON 写字符串:<c>"MergeMode": "WhenAll"</c>(<see cref="ComponentRegistry.InjectProperties"/> 走 Enum.Parse 反序列化)。</para>
        /// <para>
        /// 跟 handler 路径的交互:WhenAll 门槛由 framework 在 Publish 前 gate;不管 default 还是 handler 路径都生效。
        /// 区别是 default 路径在未凑齐前还会跳过 buffer 重建(避免暴露半态);handler 路径仍会调你的 handler,
        /// 业务可自行决定是否要在 buffer 上动手 —— framework 只保证未凑齐前不 Publish。
        /// </para>
        /// </summary>
        public MergeMode MergeMode { get; init; } = MergeMode.WhenAny;

        private BlueprintHandlerRegistry? _handlers;
        BlueprintHandlerRegistry? IHandlerAware.Handlers { set => _handlers = value; }

        // resolve 一次后缓存,后续每帧 publish 走 fast path —— 跟 ContextIngestor 委托缓存同款思路。
        private Action<List<TItem>, int, DataSnapshot<TItem>>? _mergeFn;
        private bool _mergeFnResolved;

        // 默认 replace-by-slot 语义专用:per-slot 本地 buffer。Merge 内懒分配,按 UpstreamCount 上限扩展。
        private List<TItem>[]? _slots;

        // WhenAll 门槛追踪:每路上游是否 publish 过至少一次。lazy 分配,跟 _slots 同寿命。
        // 一旦 _allSeen 转 true 就不再回 false(WhenAll 仅做"首发门槛",之后退化为 WhenAny 实时透传)。
        private bool[]? _seen;
        private bool _allSeen;

        protected override bool ShouldPublishAfterMerge(int slotIndex)
        {
            if (MergeMode == MergeMode.WhenAny) return true;
            return _allSeen;
        }

        protected override void Merge(List<TItem> buffer, int slotIndex, DataSnapshot<TItem> upstreamSnapshot)
        {
            if (!_mergeFnResolved)
            {
                _mergeFnResolved = true;
                if (!string.IsNullOrEmpty(MergeHandler))
                {
                    var del = _handlers?.TryGet(MergeHandler);
                    if (del is Action<List<TItem>, int, DataSnapshot<TItem>> typed) _mergeFn = typed;
                    else if (del != null)
                        Console.WriteLine(
                            $"[Composite<{typeof(TItem).Name}>] MergeHandler '{MergeHandler}' 签名不匹配 " +
                            $"Action<List<{typeof(TItem).Name}>, int, DataSnapshot<{typeof(TItem).Name}>>;" +
                            $"实际:{del.GetType()}。退化为默认 replace-by-slot 语义。");
                    else
                        Console.WriteLine(
                            $"[Composite<{typeof(TItem).Name}>] MergeHandler '{MergeHandler}' 未注册,退化为默认 replace-by-slot 语义。");
                }
            }

            // _slots / _seen 懒分配 + 按需 grow —— UpstreamCount 由基类追踪,每次 Attach 后 +1。
            // 注意:Merge 调用时 slotIndex < UpstreamCount(基类 Attach 后才 sub.OnNext);取 UpstreamCount 当上限分配。
            EnsureSlotCapacity(UpstreamCount);

            // 门槛追踪:无论 default 还是 handler 路径都 mark seen。一旦 _allSeen=true 就锁定,后续帧零开销。
            if (!_allSeen && _seen != null)
            {
                _seen[slotIndex] = true;
                bool all = UpstreamCount > 0;
                for (int i = 0; i < UpstreamCount; i++) { if (!_seen[i]) { all = false; break; } }
                if (all) _allSeen = true;
            }

            // Handler 路径:让用户的 handler 接管 buffer。framework 仍在外层 gate Publish。
            if (_mergeFn != null) { _mergeFn(buffer, slotIndex, upstreamSnapshot); return; }

            // 默认 replace-by-slot:总是更新 slot(保留最新 snapshot),
            // 但在 WhenAll 未凑齐时跳过 buffer 重建 —— 避免 GetSnapshot() 暴露半清单。
            var slot = _slots![slotIndex];
            slot.Clear();
            var span = upstreamSnapshot.Items.AsSpan().Slice(0, upstreamSnapshot.Count);
            for (int i = 0; i < span.Length; i++) slot.Add(span[i]);

            if (MergeMode == MergeMode.WhenAll && !_allSeen) return;

            buffer.Clear();
            for (int i = 0; i < UpstreamCount; i++) buffer.AddRange(_slots[i]);
        }

        private void EnsureSlotCapacity(int needed)
        {
            int oldLen = _slots?.Length ?? 0;
            if (oldLen >= needed) return;
            Array.Resize(ref _slots, needed);
            Array.Resize(ref _seen, needed);
            for (int i = oldLen; i < needed; i++) _slots![i] = new List<TItem>();
        }
    }

    /// <summary>
    /// Framework 内部接口 —— DS 实例(典型:<see cref="Composite{TItem}"/>)需要在装配后访问
    /// <see cref="BlueprintHandlerRegistry"/> 时,实现此接口;<see cref="BlueprintRunner"/>
    /// 在 leaf 装配完毕注入 handler 注册表。业务代码不应直接实现,继承 <see cref="Composite{TItem}"/> 自动得到。
    /// </summary>
    internal interface IHandlerAware
    {
        BlueprintHandlerRegistry? Handlers { set; }
    }
}
