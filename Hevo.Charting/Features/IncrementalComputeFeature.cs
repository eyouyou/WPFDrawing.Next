using System;
using System.Collections.Generic;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer.GraphViewer.Wrappers;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    /// <summary>
    /// §D2.6.4 增量计算节点(单 + 多输入统一模型):
    ///   <c>(input..., prev_state) → handler → (output, next_state)</c>。
    /// EMA / RSI(单输入)/ ATR-stateful(H/L/C 多输入)/ Kalman 滤波 等内部带累加器的指标。
    /// state 装 feature 内部 private 字段,O(1) 派生 + handler 仍是纯函数(D3 Remote 友好)。
    ///
    /// <para>
    /// <b>state 装哪里 — feature 内部 private 字段</b>(决策 1,§D2.6.4):
    /// </para>
    /// <list type="bullet">
    ///   <item>蓝图协议简洁:仅 InputPort / Inputs.* + OutputPort,无 StatePort 必填 ceremony</item>
    ///   <item>无 board 反馈环概念;每个 feature 实例自带 state,无法跨实例串味</item>
    ///   <item>D3 Remote:RemoteIncrementalComputeFeature 子类把 _state 序列化进 RPC 入参,跟"state 在 wire 上"语义等价</item>
    /// </list>
    ///
    /// <para>
    /// <b>单输入</b>(蓝图):<see cref="InputPort"/> 单端口 + handler 签名 <c>Func&lt;ROM, ROM, (ROM, ROM)&gt;</c>
    /// </para>
    /// <code>
    /// {
    ///   "TypeName": "IncrementalComputeFeature",
    ///   "Properties": { "Compute": "ema_20_inc" },
    ///   "InputBindings": { "InputPort": "candle_close" },
    ///   "OutputBindings": { "OutputPort": "ind_ema_20" }
    /// }
    /// </code>
    ///
    /// <para>
    /// <b>多输入</b>(蓝图,§D2.6.4 + §D2.X 复用):<see cref="Inputs"/> 字典 + <see cref="InputOrder"/> +
    /// handler 签名 <c>Func&lt;ROM, ROM, ..., ROM /*state*/, (ROM, ROM)&gt;</c>(N 个数据输入 + 1 个 prev_state 形参 + ValueTuple 返回)
    /// </para>
    /// <code>
    /// {
    ///   "TypeName": "IncrementalComputeFeature",
    ///   "Properties": { "Compute": "atr_14_inc", "InputOrder": ["high","low","close"] },
    ///   "InputBindings": {
    ///     "Inputs.high":  "RealTime_High",
    ///     "Inputs.low":   "RealTime_Low",
    ///     "Inputs.close": "RealTime_Close"
    ///   },
    ///   "OutputBindings": {
    ///     "OutputPort":   "Indicator_ATR_inc"
    ///   }
    /// }
    /// </code>
    ///
    /// <para>
    /// <b>handler 签名约定</b>:state 是**隐含的最末位形参**,不出现在 <see cref="InputOrder"/> /
    /// Python <c>inputs=[...]</c> 列表里。两侧都只列数据输入名。
    /// </para>
    ///
    /// <para>
    /// <b>非蓝图业务侧用法</b>:
    /// </para>
    /// <code>
    /// // 单输入(强类型 init,无需 ValueTuple<,> 拼写):
    /// schema.Add(new IncrementalComputeFeature {
    ///     InputPort       = closePort,
    ///     OutputPort      = emaOutPort,
    ///     StatefulCompute = (close, prev) => {
    ///         var alpha = 2.0 / (20 + 1);
    ///         var prevVal = prev.Length == 0 ? close.Span[0] : prev.Span[0];
    ///         var newEma = close.Span[^1] * alpha + prevVal * (1 - alpha);
    ///         var arr = new[] { newEma };
    ///         return (arr, arr);
    ///     },
    /// });
    ///
    /// // 多输入(走 Compute Delegate,业务自己写 typed Func):
    /// schema.Add(new IncrementalComputeFeature {
    ///     Inputs     = new() { ["high"]=h, ["low"]=l, ["close"]=c },
    ///     InputOrder = new[] { "high","low","close" },
    ///     OutputPort = atrOutPort,
    ///     Compute    = new Func&lt;ROM&lt;double&gt;, ROM&lt;double&gt;, ROM&lt;double&gt;, ROM&lt;double&gt;,
    ///                          (ROM&lt;double&gt;, ROM&lt;double&gt;)&gt;(
    ///         (h, l, c, prev) => { /* stateful ATR */ }),
    /// });
    /// </code>
    /// </summary>
    // 不 sealed —— 业务派生子类(典型:`EmaFeature : IncrementalComputeFeature` 把 length / alpha 等参数化进字段)
    // DryRun 走 IsAssignableFrom 自动覆盖派生类,§D2.6.4 决策。
    public class IncrementalComputeFeature : ComputeNodeFeature
    {
        /// <summary>
        /// 单输入端口。跟 <see cref="Inputs"/> 互斥:Inputs.Count > 0 时走多输入路径,InputPort 被忽略。
        /// </summary>
        public DataPort<ReadOnlyMemory<double>> InputPort { get; init; } = null!;

        /// <summary>
        /// §D2.6.4 多输入字典(跟 §D2.X ComputeFeature.Inputs 同语义)。
        /// 蓝图侧 <c>"Inputs.high" / "Inputs.low" / ...</c> nested key 反射焊接到这里。
        /// 字典空 → 走 <see cref="InputPort"/> 单输入路径。
        /// </summary>
        public Dictionary<string, DataPort<ReadOnlyMemory<double>>> Inputs { get; init; } = new();

        /// <summary>
        /// 多输入路径下的形参顺序(数据输入,**不含** state)。
        /// 例 <c>["high","low","close"]</c> → handler 形参 <c>(h, l, c, prev_state)</c>。
        /// 多输入路径必填,空数组 = 走单输入兼容路径。
        /// </summary>
        public string[] InputOrder { get; init; } = Array.Empty<string>();

        /// <summary>输出端口:增量计算结果。</summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<ReadOnlyMemory<double>> OutputPort { get; init; } = null!;

        /// <summary>
        /// 强类型 stateful 委托(C# 业务侧首选,**仅单输入**形态)。
        /// 跟 <see cref="Compute"/> 互斥:两者都设时优先 StatefulCompute。
        /// 多输入业务 lambda 走 <see cref="Compute"/> 字段(Delegate? + DynamicInvoke)。
        /// </summary>
        public Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                    (ReadOnlyMemory<double> output, ReadOnlyMemory<double> nextState)>? StatefulCompute { get; init; }

        /// <summary>
        /// 通用 stateful 委托(单 + 多输入统一)。蓝图 JSON 写 string handler 名,加载阶段被
        /// <c>DynamicChartSchema.ResolveHandlerReferences</c> 翻成实际委托。
        /// 单输入 = <c>Func&lt;ROM, ROM, (ROM, ROM)&gt;</c>;
        /// 多输入 = <c>Func&lt;ROM, ROM, ..., ROM /*state*/, (ROM, ROM)&gt;</c> (N 数据输入 + state)。
        /// 不是 ValueTuple<,> 返回 OnCompose 早退 + warning(对应 BP_PYHANDLER_INCREMENTAL_NOT_STATEFUL DryRun)。
        /// </summary>
        public Delegate? Compute { get; init; }

        // §D2.6.4 关键:state 装 feature 内部字段,不入 board 端口。
        // schema 实例 lifetime 内有效,schema 重建即丢(预期语义)。
        // 单线程 watcher 内 read+write,不需要锁(flow.Watch 串行调度)。
        private ReadOnlyMemory<double> _state = ReadOnlyMemory<double>.Empty;

        protected override void OnComputeCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            if (OutputPort == null) return;

            // 多输入路径:Inputs 字典非空 → InputOrder 必填,DynamicInvoke
            if (Inputs.Count > 0)
            {
                ComposeMultiInput(flow);
                return;
            }

            // 单输入路径(优先强类型 StatefulCompute,fallback Delegate cast)
            ComposeSingleInput(flow);
        }

        private void ComposeSingleInput(IRenderFlow<DataBlackboard> flow)
        {
            if (InputPort == null) return;

            var typed = StatefulCompute;
            if (typed == null && Compute is Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>,
                                                 ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>>> fromDelegate)
            {
                typed = (input, prev) => fromDelegate(input, prev);
            }
            if (typed == null)
            {
                if (Compute != null)
                {
                    Console.WriteLine(
                        $"[IncrementalComputeFeature] Compute 委托类型 {Compute.GetType().Name} " +
                        "不是 Func<ROM, ROM, ValueTuple<ROM, ROM>>,跳过装配。");
                }
                return;
            }

            // ⚠️ 故意保留 flow.Watch(同步)而非 WatchAsync ——
            // IncrementalCompute 是 stateful(_state 字段累积),WatchAsync 后多 task 并发会撞 _state race。
            // stateful 累积场景跑同步 Watch 确保单线程顺序执行;handler 必须是 O(增量) 不能是 O(全量)
            // 重计算(MA/EMA/MACD 这类增量算法天然满足)。
            // 如果业务需要 stateful + heavy compute,需自己在 handler 内加 mutex + 切线程。
            flow.Watch(new object[] { InputPort }, board =>
            {
                using (board.AcquireUpgradeableReadLock())
                {
                    var input = board.Read(InputPort);
                    if (input.Length == 0) return;

                    ReadOnlyMemory<double> output;
                    ReadOnlyMemory<double> nextState;
                    try
                    {
                        (output, nextState) = typed(input, _state);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IncrementalComputeFeature] handler 调用异常: {(ex.InnerException ?? ex).Message}");
                        return;
                    }

                    _state = nextState;

                    using (board.AcquireWriteLock())
                        board.WriteIfChanged(OutputPort, output);
                }
            });
        }

        private void ComposeMultiInput(IRenderFlow<DataBlackboard> flow)
        {
            if (Compute == null)
            {
                Console.WriteLine("[IncrementalComputeFeature] 多输入路径需要 Compute 委托(StatefulCompute 仅支持单输入),跳过装配。");
                return;
            }
            if (InputOrder == null || InputOrder.Length == 0)
            {
                Console.WriteLine("[IncrementalComputeFeature] Inputs 字典非空但 InputOrder 未声明,跳过装配。");
                return;
            }

            // 按 InputOrder 拉对应的数据输入端口(state 不在 InputOrder 里 — 隐式末位形参)
            var orderedPorts = new DataPort<ReadOnlyMemory<double>>[InputOrder.Length];
            for (int i = 0; i < InputOrder.Length; i++)
            {
                if (!Inputs.TryGetValue(InputOrder[i], out var p) || p == null)
                {
                    Console.WriteLine($"[IncrementalComputeFeature] InputOrder['{InputOrder[i]}'] 未在 Inputs 字典中焊接,跳过装配。");
                    return;
                }
                orderedPorts[i] = p;
            }

            // watchKeys = N 个数据输入端口(不含 state — state 是 feature 字段)
            var watchKeys = new object[orderedPorts.Length];
            for (int i = 0; i < orderedPorts.Length; i++) watchKeys[i] = orderedPorts[i];

            // DynamicInvoke 入参:N 个数据 ROM + 1 个 prev_state ROM = N+1 args
            var argsBuf = new object?[orderedPorts.Length + 1];

            flow.Watch(watchKeys, board =>
            {
                using (board.AcquireUpgradeableReadLock())
                {
                    for (int i = 0; i < orderedPorts.Length; i++)
                    {
                        var v = board.Read(orderedPorts[i]);
                        if (v.Length == 0) return;          // 任一输入未就绪 → 整体跳过
                        argsBuf[i] = v;
                    }
                    argsBuf[orderedPorts.Length] = _state;  // 末位:prev_state(首帧 length=0)

                    ReadOnlyMemory<double> output;
                    ReadOnlyMemory<double> nextState;
                    try
                    {
                        var raw = Compute.DynamicInvoke(argsBuf);
                        // 期望 ValueTuple<ROM<double>, ROM<double>>
                        if (raw is ValueTuple<ReadOnlyMemory<double>, ReadOnlyMemory<double>> tuple)
                        {
                            output = tuple.Item1;
                            nextState = tuple.Item2;
                        }
                        else
                        {
                            Console.WriteLine(
                                $"[IncrementalComputeFeature] handler 返回类型 {raw?.GetType().Name ?? "null"} " +
                                "不是 ValueTuple<ROM, ROM>(增量协议要求 (output, next_state) 二元组),跳过。");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IncrementalComputeFeature] handler 调用异常: {(ex.InnerException ?? ex).Message}");
                        return;
                    }

                    _state = nextState;

                    using (board.AcquireWriteLock())
                        board.WriteIfChanged(OutputPort, output);
                }
            });
        }

        protected override void OnComputeProject(FeatureContext ctx)
        {
            // 数据流由 OnComputeCompose 里的 flow.Watch 驱动,OnProject 帧路径不需做事。
        }
    }
}
