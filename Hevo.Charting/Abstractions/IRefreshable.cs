using System.Threading;
using System.Threading.Tasks;

namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 💥 可刷新能力契约：声明"我能被外部触发去重拉一次最新数据"。
    /// <para/>
    /// 与 <see cref="IPausable"/> 的关系：
    ///   - IPausable 关心生命周期闸门（冻结/解冻）；
    ///   - IRefreshable 关心数据时效（再拉一次最新）。
    /// 一个数据源可以同时实现两者：Suspend/Resume 由宿主托管；Resume 时宿主顺手调一次 RefreshAsync。
    /// <para/>
    /// 框架契约：<see cref="Core.ReactiveSchema"/> 在 Resume 末尾会自动遍历自己托管的可释放资源，
    /// 对实现 IRefreshable 的逐个 fire-and-forget 调用 RefreshAsync。业务图无需任何注册代码。
    /// </summary>
    public interface IRefreshable
    {
        /// <summary>用当前内部状态重拉一次最新数据。语义由实现方定义；典型实现 = 用最近一次的 Context 重发请求。</summary>
        Task RefreshAsync(CancellationToken token = default);
    }
}
