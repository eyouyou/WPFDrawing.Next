using System;
using System.Collections.Generic;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// 蓝图层"纯副作用"节点:输入 → 委托(无返回值)。
    /// 信号决策 / 下单(Python 函数体内调 <c>hevo_indicators.trade.place_order(...)</c>)/ 报警 /
    /// 日志推送等场景的标准入口。
    ///
    /// <para>
    /// <b>单输入</b>:<see cref="InputPort"/> → Action&lt;ROM&lt;double&gt;&gt;。
    /// </para>
    /// <para>
    /// <b>多输入(§D2.X)</b>:<see cref="Inputs"/> 字典 + <see cref="InputOrder"/> 声明形参顺序 →
    /// Action&lt;ROM, ROM, ROM, ...&gt;(N 参,无返回)。
    /// </para>
    ///
    /// <para>
    /// <b>蓝图协议(单输入)</b>:
    /// </para>
    /// <code>
    /// {
    ///   "TypeName": "HandlerFeature",
    ///   "Properties": {
    ///     "Handler": "ma_cross_strategy",
    ///     "MinIntervalMs": 1000               // 节流:同 handler 至少 1 秒才再触发一次
    ///   },
    ///   "InputBindings": { "InputPort": "Indicator_MA20" }
    /// }
    /// </code>
    ///
    /// <para>
    /// <b>蓝图协议(多输入)</b>:Properties 多 InputOrder 数组,InputBindings 走 nested key
    /// <c>"Inputs.{name}"</c>。
    /// </para>
    ///
    /// <para>
    /// <b>节流</b>:<see cref="MinIntervalMs"/> &gt; 0 时,两次调用间隔不足 N 毫秒 → 跳过当帧。
    /// 0 = 不节流,每次输入端口数据变更都调一次。
    /// </para>
    ///
    /// <para>
    /// <b>异常处理</b>:Handler 抛异常 → 记日志,主流不被毒化(典型场景:策略代码 bug
    /// 不该把整张图带崩,业务侧自行查日志修复)。
    /// </para>
    /// </summary>
    public sealed class HandlerFeature : ChartFeature
    {
        /// <summary>跑在 Series 之后(55),典型用法:监听已计算好的指标端口触发副作用。</summary>
        public override FeaturePhase Phase => (FeaturePhase)55;

        /// <summary>
        /// 单输入端口。跟 <see cref="Inputs"/> 互斥:Inputs.Count > 0 时走多输入路径。
        /// </summary>
        public DataPort<ReadOnlyMemory<double>> InputPort { get; init; } = null!;

        /// <summary>§D2.X 多输入端口字典 —— PortBindings <c>"Inputs.{name}"</c> 焊接到这里。</summary>
        public Dictionary<string, DataPort<ReadOnlyMemory<double>>> Inputs { get; init; } = new();

        /// <summary>§D2.X Handler 委托的形参顺序。多输入路径必填。</summary>
        public string[] InputOrder { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 副作用委托。蓝图 JSON 写 string handler 名,加载阶段翻成 <see cref="Action"/> / Action&lt;...&gt;。
        /// 单输入 = Action&lt;ROM&gt;;多输入 = Action&lt;ROM, ..., ROM&gt;。
        /// </summary>
        public Delegate? Handler { get; init; }

        /// <summary>节流间隔(毫秒)。0 = 不节流。典型策略 1000(1 秒)避免每 tick 下单。</summary>
        public int MinIntervalMs { get; init; } = 0;

        private long _lastInvokeMs = long.MinValue;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            if (Handler == null) return;

            // §D2.X 多输入路径
            if (Inputs.Count > 0)
            {
                if (InputOrder == null || InputOrder.Length == 0)
                {
                    Console.WriteLine("[HandlerFeature] Inputs 字典非空但 InputOrder 未声明,跳过装配。");
                    return;
                }
                var orderedPorts = new DataPort<ReadOnlyMemory<double>>[InputOrder.Length];
                for (int i = 0; i < InputOrder.Length; i++)
                {
                    if (!Inputs.TryGetValue(InputOrder[i], out var p) || p == null)
                    {
                        Console.WriteLine($"[HandlerFeature] InputOrder['{InputOrder[i]}'] 未在 Inputs 字典中焊接,跳过装配。");
                        return;
                    }
                    orderedPorts[i] = p;
                }
                var watchKeys = new object[orderedPorts.Length];
                for (int i = 0; i < orderedPorts.Length; i++) watchKeys[i] = orderedPorts[i];

                // 协议规范 §B:Handler 是业务副作用回调(可能 IO / 重计算),走 WatchAsync 后台。
                // argsBuf 改 lambda 内 local —— WatchAsync 后台 task 并发会撞共享 buffer。
                flow.WatchAsync(watchKeys, board =>
                {
                    if (MinIntervalMs > 0)
                    {
                        long nowMs = Environment.TickCount64;
                        if (nowMs - _lastInvokeMs < MinIntervalMs) return;
                        _lastInvokeMs = nowMs;
                    }

                    // ① 瞬间读锁捞 inputs
                    var argsBuf = new object?[orderedPorts.Length];
                    using (board.AcquireReadLock())
                    {
                        for (int i = 0; i < orderedPorts.Length; i++)
                        {
                            var v = board.Read(orderedPorts[i]);
                            if (v.Length == 0) return;
                            argsBuf[i] = v;
                        }
                    }

                    // ② 锁外裸奔 handler(不阻塞 UI / 其他 reader)
                    try { Handler.DynamicInvoke(argsBuf); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[HandlerFeature] handler 异常: {(ex.InnerException ?? ex).Message}");
                    }
                });
                return;
            }

            // 单输入兼容路径
            if (InputPort == null) return;
            flow.WatchAsync(new object[] { InputPort }, board =>
            {
                if (MinIntervalMs > 0)
                {
                    long nowMs = Environment.TickCount64;
                    if (nowMs - _lastInvokeMs < MinIntervalMs) return;
                    _lastInvokeMs = nowMs;
                }

                // ① 瞬间读锁捞 input
                ReadOnlyMemory<double> input;
                using (board.AcquireReadLock())
                {
                    input = board.Read(InputPort);
                    if (input.Length == 0) return;
                }

                // ② 锁外裸奔 handler
                {
                    try
                    {
                        if (Handler is Action<ReadOnlyMemory<double>> typed) typed(input);
                        else Handler.DynamicInvoke(input);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[HandlerFeature] handler 异常: {(ex.InnerException ?? ex).Message}");
                    }
                }
            });
        }

        protected override void OnProject(FeatureContext ctx)
        {
            // 副作用由 OnCompose 里的 flow.Watch 驱动,本帧路径不必做事。
        }
    }
}
