using System;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting.Core;

namespace Hevo.Charting.WorkFlow
{
    /// <summary>
    /// 单飞 + 丧尸拦截 + 可 await 的"请求总线" —— 给数据源 / 业务模块挂业务化 typed 请求用,
    /// 不强制走 <see cref="ReactiveDataSource{TSource, TContext, TItem}"/> 基类的"全 framework 接管"路径。
    /// <para>
    /// <b>语义</b>:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>FetchLatest 单飞</b>:bus 同时只跑一个 handler 实例;新请求到来取消正在跑的(handler 应自己 cooperate <c>token.ThrowIfCancellationRequested</c>)</item>
    ///   <item><b>调用方令牌</b>:<see cref="RequestAsync"/> 接受 user CancellationToken,跟 bus 内部令牌 linked,任一取消 → handler token 取消</item>
    ///   <item><b>丧尸拦截</b>:handler 已完成但 user token 已 cancel 时,TCS 走 SetCanceled(不让晚到的结果写穿调用方业务态)</item>
    ///   <item><b>fire-and-forget</b>:<see cref="Push"/> 不返 Task,适用于"心跳吐 demand,失败 swallow + log"场景</item>
    ///   <item><b>typed 结果</b>:handler 返回 <typeparamref name="TResult"/>,<see cref="RequestAsync"/> 也返 <c>Task&lt;TResult&gt;</c> —— 调用方拿到本次"获取条数 / 状态码"等业务结果</item>
    /// </list>
    /// <para>
    /// <b>典型用法</b>(KLine 多种 demand 类型走同一 bus,handler 返回本次回包条数):
    /// </para>
    /// <code>
    /// _demands = new RequestBus&lt;IKLineDemand, int&gt;(async (demand, ct) =&gt;
    /// {
    ///     var block = await DoFetch(demand, ct);
    ///     UpdateBuffer(b =&gt; MergeByDemand(b, block));
    ///     return block.Count;
    /// });
    /// public Task&lt;int&gt; RequestAsync(IKLineDemand demand, CancellationToken ct) =&gt; _demands.RequestAsync(demand, ct);
    /// </code>
    /// <para>
    /// 不需要结果时用 <c>RequestBus&lt;TRequest, Unit&gt;</c>,handler 返 <see cref="Unit.Default"/>。
    /// 一个 DS 可以同时持有多个 RequestBus(不同 TRequest 类型),互不干扰。
    /// </para>
    /// </summary>
    public sealed class RequestBus<TRequest, TResult> : IDisposable
    {
        private readonly struct Envelope
        {
            public TRequest Request { get; }
            public TaskCompletionSource<TResult>? Tcs { get; }   // null = fire-and-forget
            public CancellationToken Token { get; }
            public Envelope(TRequest request, TaskCompletionSource<TResult>? tcs, CancellationToken token)
            { Request = request; Tcs = tcs; Token = token; }
        }

        private readonly WorkflowTrigger<Envelope> _trigger = new();
        private readonly Func<TRequest, CancellationToken, Task<TResult>> _handler;
        private IDisposable? _pipelineSub;
        private bool _disposed;

        public RequestBus(Func<TRequest, CancellationToken, Task<TResult>> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _pipelineSub = BuildPipeline();
        }

        private IDisposable BuildPipeline()
        {
            return _trigger
                .FetchLatest(async (env, busToken) =>
                {
                    try
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(busToken, env.Token);
                        var result = await _handler(env.Request, linkedCts.Token).ConfigureAwait(false);
                        return (Env: env, Result: result, Error: (Exception?)null);
                    }
                    catch (Exception ex)
                    {
                        return (Env: env, Result: default(TResult)!, Error: ex);
                    }
                })
                .Subscribe(res =>
                {
                    if (res.Error != null) { res.Env.Tcs?.TrySetException(res.Error); return; }
                    // 丧尸拦截:user 已取消时不让 TCS 误置 Success
                    if (res.Env.Token.IsCancellationRequested) { res.Env.Tcs?.TrySetCanceled(res.Env.Token); return; }
                    res.Env.Tcs?.TrySetResult(res.Result);
                });
        }

        /// <summary>fire-and-forget 入队。失败异常被吞,只在 await 路径回传 ——
        /// 心跳类调用方典型 try/catch 包外层 log 即可。</summary>
        public void Push(TRequest request)
        {
            if (_disposed) return;
            _trigger.Push(new Envelope(request, null, CancellationToken.None));
        }

        /// <summary>
        /// 可 await 的请求。Task 在 handler 完成时 setresult;handler 抛异常 setexception;
        /// <paramref name="token"/> 取消 setcanceled(handler token 也会被 linked-cancel)。
        /// </summary>
        public async Task<TResult> RequestAsync(TRequest request, CancellationToken token = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RequestBus<TRequest, TResult>));

            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = token.CanBeCanceled
                ? token.Register(() => tcs.TrySetCanceled(token))
                : default;

            _trigger.Push(new Envelope(request, tcs, token));
            return await tcs.Task.ConfigureAwait(false);
        }

        // 重建管线 —— DataSource Suspend/Resume 可调,跟 ReactiveDataSource 5 参版的 _pipelineSub 同款语义。
        public void Suspend()
        {
            _pipelineSub?.Dispose();
            _pipelineSub = null;
        }

        public void Resume()
        {
            if (_disposed) return;
            _pipelineSub ??= BuildPipeline();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pipelineSub?.Dispose();
            _pipelineSub = null;
        }
    }
}
