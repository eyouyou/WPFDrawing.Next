namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 💥 可暂停生命周期契约（Phase 11 / plan §H）
    ///
    /// 支持"热待机"场景：TabControl 切 Tab、Window 最小化、ItemsControl 虚拟化离屏等
    /// "隐藏但未卸载"的宿主状态下，暂停所有后台流（请求管线、Push 订阅、心跳定时器等）。
    ///
    /// 语义对比：
    ///   - Dispose  : 永久终结，资源归还；
    ///   - Suspend  : 冻结，保留状态快照，等待 Resume；
    ///   - Resume   : 解冻，必要时补一次刷新让 UI 回到最新。
    ///
    /// 幂等性：Suspend/Resume 均可连续调用，基类通过 _isActive flag 保护。
    /// </summary>
    public interface IPausable
    {
        /// <summary>当前是否处于工作状态。</summary>
        bool IsActive { get; }

        /// <summary>冻结所有内部流。幂等：若已 Suspend 再次调用无副作用。</summary>
        void Suspend();

        /// <summary>解冻并补一次刷新。幂等：若已 Resume 再次调用无副作用。</summary>
        void Resume();
    }
}
