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
    /// 蓝图层"计算节点":输入 → 委托 → 输出。委托可以是 Python(经 PythonHandlerRegistry 注册)、
    /// C# 方法(<c>[BlueprintHandler]</c>)或业务直接代码注入。
    ///
    /// <para>
    /// <b>单输入(主流形态)</b>:<see cref="InputPort"/>(<see cref="ReadOnlyMemory{Double}"/>) →
    /// <see cref="OutputPort"/>(<see cref="ReadOnlyMemory{Double}"/>),Compute 是
    /// <c>Func&lt;ROM&lt;double&gt;, ROM&lt;double&gt;&gt;</c>。
    /// </para>
    ///
    /// <para>
    /// <b>多输入(§D2.X)</b>:<see cref="Inputs"/> 字典里 N 根 ROM&lt;double&gt; 端口
    /// (典型 ATR 的 high/low/close),<see cref="InputOrder"/> 声明形参顺序,Compute 是
    /// <c>Func&lt;ROM, ROM, ROM, ROM&gt;</c>(N 参 + 1 返回)。蓝图协议:
    /// </para>
    /// <code>
    /// {
    ///   "TypeName": "ComputeFeature",
    ///   "Properties": {
    ///     "Compute": "atr_14",
    ///     "InputOrder": ["high","low","close"]    // ← 跟 Python signature 形参顺序对齐
    ///   },
    ///   "InputBindings": {
    ///     "Inputs.high":  "RealTime_High",        // ← nested key,DefineFeatures 反射拆 ParentField + ChildKey
    ///     "Inputs.low":   "RealTime_Low",
    ///     "Inputs.close": "RealTime_Close"
    ///   },
    ///   "OutputBindings": {
    ///     "OutputPort":   "Indicator_ATR"
    ///   }
    /// }
    /// </code>
    ///
    /// <para>
    /// <b>类型组合</b>:本节点固定 <c>ROM&lt;double&gt;</c> 输入 / 输出(指标主流)。其他
    /// 类型组合(int 系列 / 标量信号 / OHLCV record 等)派子类替换字段类型即可。
    /// </para>
    ///
    /// <para>
    /// <b>异常处理</b>:Compute 委托抛异常时记日志后 silent skip 当帧,不毒化主数据流。
    /// Python handler 的 traceback 已由 PythonNet 端 Translate 进 <see cref="Exception.Message"/>。
    /// </para>
    /// </summary>
    public sealed class ComputeFeature : ComputeNodeFeature
    {
        /// <summary>
        /// 单输入端口 —— 数据源 / 上游 Compute 推过来的 ROM&lt;double&gt;。
        /// 跟 <see cref="Inputs"/> 互斥:Inputs.Count > 0 时走多输入路径,InputPort 被忽略。
        /// </summary>
        public DataPort<ReadOnlyMemory<double>> InputPort { get; init; } = null!;

        /// <summary>
        /// §D2.X 多输入端口字典 —— 蓝图侧 <c>"Inputs.high" / "Inputs.low" / ...</c> nested key
        /// 焊接到这里。<see cref="InputOrder"/> 决定调 Compute 委托时的形参顺序。
        /// 字典空 → 走 <see cref="InputPort"/> 单输入路径(向后兼容)。
        /// </summary>
        public Dictionary<string, DataPort<ReadOnlyMemory<double>>> Inputs { get; init; } = new();

        /// <summary>
        /// §D2.X Compute 委托的形参顺序,例 <c>["high","low","close"]</c>,跟 Python
        /// <c>@register(inputs=['high','low','close'])</c> 的 inputs 参数对齐。
        /// 多输入路径必填。空数组 = 走单输入兼容路径。
        /// </summary>
        public string[] InputOrder { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 单输出端口 —— 跟 <see cref="Outputs"/> 互斥:Outputs.Count > 0 时走多输出路径,本字段被忽略。
        /// </summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<ReadOnlyMemory<double>> OutputPort { get; init; } = null!;

        /// <summary>
        /// 多输出端口字典 —— 蓝图 <c>"Outputs.time" / "Outputs.close" / ...</c> nested key 焊接到这里。
        /// Compute 委托返 <c>IDictionary&lt;string, object?&gt;</c>(value 是 ROM&lt;double&gt; 或 double[]),
        /// 按 Outputs 字典里的 key 一一对应写到对应端口。空字典 → 走 <see cref="OutputPort"/> 单输出路径。
        /// <para>
        /// 典型场景:K 线列拆分(<c>Func&lt;ROM&lt;Block&gt;, dict&gt;</c> 输出 Time/Open/High/Low/Close 5 根 ROM&lt;double&gt;)。
        /// </para>
        /// </summary>
        public Dictionary<string, DataPort<ReadOnlyMemory<double>>> Outputs { get; init; } = new();

        /// <summary>
        /// 计算委托。蓝图 JSON 写 string handler 名,加载阶段被
        /// <c>DynamicChartSchema.ResolveHandlerReferences</c> 翻成实际委托。
        /// <list type="bullet">
        ///   <item>单输入 + 单输出:<c>Func&lt;ROM, ROM&gt;</c></item>
        ///   <item>多输入 + 单输出:<c>Func&lt;ROM, ROM, ..., ROM&gt;</c>(N 参 + 1 返回)</item>
        ///   <item>单/多输入 + 多输出:返回 <c>IDictionary&lt;string, object?&gt;</c>,key 对齐 <see cref="Outputs"/> 字典</item>
        /// </list>
        /// 业务侧也可以直接代码注入(测试 / mock 用)。
        /// </summary>
        public Delegate? Compute { get; init; }

        protected override void OnComputeCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            if (Compute == null)
            {
                // 蓝图作者接了端口但 Compute 委托没解析上来 —— 配置错误,fail fast。
                // 没接端口的纯空 Compute 实例不算配置错,继续走 return(等价于无操作的占位 feature)。
                bool wired = InputPort != null || Inputs.Count > 0 || OutputPort != null || Outputs.Count > 0;
                if (wired)
                {
                    throw new InvalidOperationException(
                        "ComputeFeature.Compute 委托未解析(端口已接但 handler 是 null),Output 永远不会更新。" +
                        "看上游 DynamicChartSchema.ResolveHandlerReferences 是否报过 'handler 未注册' / 类型不兼容警告 —— " +
                        "确认 BlueprintHandlerRegistry 里 RegisterDelegate / RegisterPythonFunction 是否成功。");
                }
                return;
            }
            bool multiOutput = Outputs.Count > 0;
            if (!multiOutput && OutputPort == null) return;

            // §D2.X 多输入路径:Inputs 非空时,InputOrder 必填。按声明顺序拉值,DynamicInvoke。
            if (Inputs.Count > 0)
            {
                // 配置错:Inputs 字典非空但 InputOrder 漏声明,handler 不知道按啥顺序拿参 —— throw fail fast。
                if (InputOrder == null || InputOrder.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"ComputeFeature.Inputs 非空(声明了 {Inputs.Count} 个输入)但 InputOrder 未声明,装配无法继续。" +
                        "InputOrder 决定 handler DynamicInvoke 的参数顺序,蓝图必填。" +
                        $"已声明 Inputs keys: [{string.Join(", ", Inputs.Keys)}]。");
                }
                var orderedPorts = new DataPort<ReadOnlyMemory<double>>[InputOrder.Length];
                for (int i = 0; i < InputOrder.Length; i++)
                {
                    if (!Inputs.TryGetValue(InputOrder[i], out var p) || p == null)
                    {
                        // 配置错:InputOrder 引用了 Inputs 字典里不存在的 key,蓝图作者拼错或漏接。
                        throw new InvalidOperationException(
                            $"ComputeFeature.InputOrder[{i}]='{InputOrder[i]}' 未在 Inputs 字典中焊接(端口缺 / null)。" +
                            $"Inputs 实际 keys: [{string.Join(", ", Inputs.Keys)}]。蓝图侧检查端口连线是否完整。");
                    }
                    orderedPorts[i] = p;
                }
                var watchKeys = new object[orderedPorts.Length];
                for (int i = 0; i < orderedPorts.Length; i++) watchKeys[i] = orderedPorts[i];

                // 协议规范 §B:Compute handler 属于"耗时指标计算",必须走 WatchAsync 后台线程池 +
                // Compute-then-Commit 三段式锁(瞬间读锁捞 → 锁外算 → 瞬间写锁提交)。
                // argsBuf 从 lambda 外共享改 lambda 内 local —— WatchAsync 后台多 task 可能并发,共享会撞写。
                flow.WatchAsync(watchKeys, board =>
                {
                    // ① 瞬间读锁捞 inputs
                    var argsBuf = new object?[orderedPorts.Length];
                    using (board.AcquireReadLock())
                    {
                        for (int i = 0; i < orderedPorts.Length; i++)
                        {
                            var v = board.Read(orderedPorts[i]);
                            if (v.Length == 0) return;          // 任一输入未就绪 → 整体跳过
                            argsBuf[i] = v;
                        }
                    }

                    // ② 锁外裸奔算指标(handler 在后台线程跑,不阻塞 UI / 其他 reader)
                    object? raw;
                    try { raw = Compute.DynamicInvoke(argsBuf); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning(
                            $"[ComputeFeature] handler 调用异常: {(ex.InnerException ?? ex).Message}");
                        return;
                    }

                    // ③ WriteOutput 内部自己 AcquireWriteLock,μs 级提交
                    WriteOutput(board, raw, multiOutput);
                });
                return;
            }

            // 单输入兼容路径
            if (InputPort == null) return;
            flow.WatchAsync(new object[] { InputPort }, board =>
            {
                // ① 瞬间读锁捞 input
                ReadOnlyMemory<double> input;
                using (board.AcquireReadLock())
                {
                    input = board.Read(InputPort);
                    if (input.Length == 0) return;
                }

                // ② 锁外算
                object? raw;
                try
                {
                    if (!multiOutput && Compute is Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>> typed)
                        raw = (object)typed(input);
                    else
                        raw = Compute.DynamicInvoke(input);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ComputeFeature] handler 调用异常: {(ex.InnerException ?? ex).Message}");
                    return;
                }

                // ③ 写锁提交
                WriteOutput(board, raw, multiOutput);
            });
        }

        // 把 handler 返回值按 Outputs 字典(多输出)或 OutputPort(单输出)路由到 board。
        private void WriteOutput(DataBlackboard board, object? raw, bool multiOutput)
        {
            if (raw == null) return;

            if (multiOutput)
            {
                if (raw is not IDictionary<string, object?> dict)
                {
                    Console.WriteLine($"[ComputeFeature] 多输出模式 handler 应返 IDictionary<string,object?>,实际 {raw.GetType().Name},跳过。");
                    return;
                }
                using (board.AcquireWriteLock())
                {
                    foreach (var kv in Outputs)
                    {
                        if (!dict.TryGetValue(kv.Key, out var v) || v == null) continue;
                        ReadOnlyMemory<double> rom = v switch
                        {
                            ReadOnlyMemory<double> m => m,
                            double[] a              => a,
                            _                       => ReadOnlyMemory<double>.Empty,
                        };
                        if (rom.Length > 0) board.WriteIfChanged(kv.Value, rom);
                    }
                }
                return;
            }

            // 单输出
            if (raw is not ReadOnlyMemory<double> rom1)
            {
                if (raw is double[] arr) rom1 = arr;
                else
                {
                    Console.WriteLine($"[ComputeFeature] handler 返回类型 {raw.GetType().Name} 不是 ROM<double>,跳过。");
                    return;
                }
            }
            using (board.AcquireWriteLock())
                board.WriteIfChanged(OutputPort, rom1);
        }

        protected override void OnComputeProject(FeatureContext ctx)
        {
            // 数据流由 OnComputeCompose 里的 flow.Watch 驱动 —— OnProject 帧路径不需做事。
        }
    }
}
