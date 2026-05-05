using Hevo.Charting.Core;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Hevo.Charting
{
    // 定义中间件委托：(数据, 下一步函数) -> void
    public delegate void NextDelegate<T>(T data);

    /// <summary>
    /// 💥 响应式工作流 (Workflow) 核心算子扩展库
    /// 提供 0-GC、线程安全的数据流编排能力，涵盖：中间件、流控、变换、背压调度与终端渲染。
    /// </summary>
    public static class WorkflowExtensions
    {
        // =================================================================
        // 💥 0. 初始状态与管线包裹 (Initialization & Wrapping)
        // =================================================================

        internal class StartWithWorkflow<T> : IWorkflow<T>
        {
            private readonly IWorkflow<T> _source;
            private readonly T _initialValue;

            public StartWithWorkflow(IWorkflow<T> source, T initialValue)
            {
                _source = source;
                _initialValue = initialValue;
            }

            public IDisposable Subscribe(Action<T> onNext, Action<Exception>? onError = null)
            {
                // 💥 绝杀核心：在这个委托被挂上管线的瞬间，立刻当面把种子数据喂给下游！
                onNext(_initialValue);

                // 然后再接通原本的数据源，继续监听真实的异步推送
                return _source.Subscribe(onNext, onError);
            }
        }

        /// <summary>
        /// 💥 终极死锁破局者 (StartWith)：在订阅瞬间强行下发一个初始快照。
        /// 彻底解决 CombineLatest 等并发算子因单边源尚未返回数据而造成的“白屏死锁”问题。
        /// </summary>
        public static IWorkflow<T> StartWith<T>(this IWorkflow<T> source, T initialValue)
        {
            return new StartWithWorkflow<T>(source, initialValue);
        }

        /// <summary>
        /// 渲染流拓扑变换器：允许业务层对已被绑定到图表的 IRenderFlow 插入额外的常规算子，
        /// 并在变换后将其重新打包回安全的 IRenderFlow 上下文中。
        /// </summary>
        public static IRenderFlow<TOut> Map<TIn, TOut>(
            this IRenderFlow<TIn> renderFlow,
            Func<IWorkflow<TIn>, IWorkflow<TOut>> transform)
        {
            // 1. 获取内部纯净的 Workflow
            // 2. 执行业务层提供的任意变换逻辑 (Select, Where, Window, etc.)
            // 3. 将结果重新 Wrap 回当前 Chart 作用域，维持生命周期约束
            return renderFlow.Wrap(transform(renderFlow));
        }

        // =================================================================
        // 1. 中间件核心 (The Middleware / Interceptor)
        // =================================================================

        /// <summary>
        /// 管道拦截器：允许在数据流向下游前进行拦截、修改、或者短路丢弃。
        /// 机制类似于 ASP.NET Core 的 Middleware 管道。
        /// </summary>
        /// <param name="middleware">中间件逻辑：只有显式调用 next(data) 才会放行数据</param>
        public static IWorkflow<T> Use<T>(this IWorkflow<T> source, Action<T, NextDelegate<T>> middleware)
        {
            return new WorkflowEngine<T>((next, error) =>
            {
                NextDelegate<T> downstream = (data) => next(data);
                return source.Subscribe(data =>
                {
                    try { middleware(data, downstream); }
                    catch (Exception ex) { error(ex); }
                }, error);
            });
        }

        /// <summary>
        /// 纯副作用算子：只执行动作，绝不改变数据流，且必定原样放行给下游。
        /// 适用于：状态同步、缓存更新等外围旁路操作。
        /// </summary>
        public static IWorkflow<T> Do<T>(this IWorkflow<T> source, Action<T> action)
        {
            return new WorkflowEngine<T>((next, error) =>
            {
                return source.Subscribe(data =>
                {
                    try
                    {
                        action(data); // 触发副作用
                        next(data);   // 原样传给下游
                    }
                    catch (Exception ex) { error(ex); }
                }, error);
            });
        }

        // =================================================================
        // 2. 数据变换 (Transformations)
        // =================================================================

        /// <summary>
        /// 投影算子 (Map/Select)：将流中的数据 f(x) 转换为 y。
        /// 适用于：数据结构降级、数学公式计算。
        /// </summary>
        public static IWorkflow<TNext> Select<T, TNext>(this IWorkflow<T> source, Func<T, TNext> selector)
        {
            return new WorkflowEngine<TNext>((next, error) =>
            {
                return source.Subscribe(
                    data =>
                    {
                        try { next(selector(data)); }
                        catch (Exception ex) { error(ex); }
                    },
                    error
                );
            });
        }

        /// <summary>
        /// 滚动累加器 (Scan)：维护内部状态并持续输出最新的累加结果。
        /// 适用于：WebSocket 增量 K线组装、持续计算移动平均(MA)。
        /// </summary>
        public static IWorkflow<TState> Scan<TSource, TState>(
            this IWorkflow<TSource> source,
            TState seed,
            Func<TState, TSource, TState> accumulator)
        {
            return new WorkflowEngine<TState>((next, error) =>
            {
                TState currentState = seed;
                object gate = new object(); // 确保多线程并发推送时状态累加的绝对安全

                return source.Subscribe(data =>
                {
                    lock (gate)
                    {
                        try
                        {
                            currentState = accumulator(currentState, data);
                            next(currentState);
                        }
                        catch (Exception ex) { error(ex); }
                    }
                }, error);
            });
        }

        /// <summary>
        /// 宽松成对输出 (Pairwise)：将当前值与上一次的值打包成元组 (Old, New) 发送。
        /// 注意：当第一帧数据到达时，Old 为 default。
        /// 适用于：计算 Delta 增量。
        /// </summary>
        public static IWorkflow<(T? Prev, T Current)> Pairwise<T>(this IWorkflow<T> source)
        {
            return new WorkflowEngine<(T?, T)>((next, error) =>
            {
                T? prev = default;
                object gate = new object();

                return source.Subscribe(current =>
                {
                    lock (gate)
                    {
                        next((prev, current));
                        prev = current;
                    }
                }, error);
            });
        }

        /// <summary>
        /// 严格成对输出 (StrictPairwise)：只有历史状态存在时，才向下游发送对子。
        /// 会强制丢弃流中的第 1 个绝对初始帧。
        /// 适用于：判断 K线涨跌色（必须要有昨收才能判断今天）。
        /// </summary>
        public static IWorkflow<(T Prev, T Current)> StrictPairwise<T>(this IWorkflow<T> source)
        {
            return new WorkflowEngine<(T, T)>((next, error) =>
            {
                T? prev = default;
                bool hasPrev = false;
                object gate = new object();

                return source.Subscribe(current =>
                {
                    lock (gate)
                    {
                        if (hasPrev)
                        {
                            // 凑齐对子了才往下游发，确保发出去的 Prev 绝对不为 null
                            next((prev!, current));
                        }
                        prev = current;
                        hasPrev = true;
                    }
                }, error);
            });
        }

        // =================================================================
        // 3. 过滤与去重 (Filtering)
        // =================================================================

        /// <summary>
        /// 条件过滤算子 (Where)：丢弃不满足断言条件的数据包。
        /// </summary>
        public static IWorkflow<T> Where<T>(this IWorkflow<T> source, Func<T, bool> predicate)
        {
            return source.Use((data, next) =>
            {
                if (predicate(data)) next(data);
            });
        }

        /// <summary>
        /// 连续去重算子 (DistinctUntilChanged)：如果新值与缓存的旧值相等，则静默丢弃。
        /// 💥 性能防线：阻止由于 Viewport 等状态未发生实质性物理变更而引发的无意义全局重绘。
        /// </summary>
        public static IWorkflow<T> DistinctUntilChanged<T>(this IWorkflow<T> source)
        {
            return new WorkflowEngine<T>((next, error) =>
            {
                T? lastValue = default;
                bool hasValue = false;
                var comparer = EqualityComparer<T>.Default;
                object gate = new object();

                return source.Subscribe(data =>
                {
                    lock (gate)
                    {
                        if (!hasValue || !comparer.Equals(lastValue, data))
                        {
                            lastValue = data;
                            hasValue = true;
                            next(data);
                        }
                    }
                }, error);
            });
        }

        // =================================================================
        // 4. 异步背压与流切换 (Async Flow & Backpressure)
        // =================================================================

        /// <summary>
        /// ⚔️ 喜新厌旧背压控制 (SwitchMap 语义)。
        /// 适用场景：历史数据拖拽加载、输入框查询、快速切换股票请求。
        /// 特点：新请求信号一旦到达，底层立刻利用 <see cref="CancellationToken"/> 斩断上一个还在空中的请求任务。永远只认最新的信号！
        /// </summary>
        public static IWorkflow<TNext> FetchLatest<T, TNext>(
            this IWorkflow<T> source,
            Func<T, CancellationToken, Task<TNext>> fetcher)
        {
            return new WorkflowEngine<TNext>((next, error) =>
            {
                CancellationTokenSource? currentCts = null;

                var sub = source.Subscribe(async data =>
                {
                    var newCts = new CancellationTokenSource();
                    // 💥 极速无锁替换：抢占式夺取控制权
                    var oldCts = Interlocked.Exchange(ref currentCts, newCts);
                    oldCts?.Cancel(); // 发送取消信号，防止脏数据回写

                    var token = newCts.Token;
                    try
                    {
                        var result = await fetcher(data, token);
                        // 最后防线：确保在 await 期间没被后来者干掉
                        if (!token.IsCancellationRequested) next(result);
                    }
                    catch (OperationCanceledException) { /* 优雅吃掉取消异常 */ }
                    catch (Exception ex) { error(ex); }
                }, error);

                // Phase 11 / §H.10：用 CompositeDisposable 保留上游 sub 的 IPausable 能力，
                // Schema.Suspend 可以一路传到根源 Interval 真正停止定时器。
                return new CompositeDisposable(sub, new DisposableAction(() =>
                {
                    var finalCts = Interlocked.Exchange(ref currentCts, null);
                    if (finalCts != null)
                    {
                        finalCts.Cancel();
                        finalCts.Dispose();
                    }
                }));
            });
        }

        /// <summary>
        /// 🛡️ 绝对防守背压控制 (ExhaustMap 语义)。
        /// 适用场景：定时器超长任务防堆叠、防止重要按钮高频连击。
        /// 特点：如果当前请求还在处理中，新来的任何信号都会被【直接静默丢弃】！
        /// </summary>
        public static IWorkflow<TNext> FetchExclusive<T, TNext>(
            this IWorkflow<T> source,
            Func<T, CancellationToken, Task<TNext>> fetcher)
        {
            return new WorkflowEngine<TNext>((next, error) =>
            {
                int isBusy = 0;

                var sub = source.Subscribe(async data =>
                {
                    // 💥 CAS 无锁防重入屏障：1 表示忙。抢不到锁直接丢弃，绝不排队！
                    if (Interlocked.CompareExchange(ref isBusy, 1, 0) != 0) return;

                    try
                    {
                        var result = await fetcher(data, CancellationToken.None);
                        next(result);
                    }
                    catch (Exception ex) { error(ex); }
                    finally
                    {
                        // 任务完成，释放屏障
                        Interlocked.Exchange(ref isBusy, 0);
                    }
                }, error);

                return sub;
            });
        }

        /// <summary>
        /// 动态源切换 (Switch 语义)。
        /// 当上游发出新信号时，释放对旧工作流的订阅，并切换到基于新信号生成的新工作流上。
        /// 适用于：股票代码变更后，退订旧的 WebSocket 流，并接上新的 WebSocket 流。
        /// </summary>
        public static IWorkflow<TStream> Follow<T, TStream>(
            this IWorkflow<T> source,
            Func<T, IWorkflow<TStream>> streamFactory)
        {
            return new WorkflowEngine<TStream>((next, error) =>
            {
                IDisposable? currentStreamSub = null;
                object gate = new object();

                var upstreamSub = source.Subscribe(data =>
                {
                    lock (gate)
                    {
                        currentStreamSub?.Dispose(); // 掐断旧流
                        var newStream = streamFactory(data);
                        currentStreamSub = newStream.Subscribe(next, error); // 接驳新流
                    }
                }, error);

                // Phase 11 / §H.10：CompositeDisposable 保留 upstreamSub 的 IPausable 能力
                return new CompositeDisposable(upstreamSub, new DisposableAction(() =>
                {
                    lock (gate) currentStreamSub?.Dispose();
                }));
            });
        }

        // =================================================================
        // 5. 时间窗口控制 (Timing)
        // =================================================================

        /// <summary>
        /// 节流算子 (Throttle)：在给定的时间间隔内，只允许通过 1 个信号，多余的直接抛弃。
        /// 💥 用于极端降频：将 MouseMove / TouchMove 强行限制在指定的 FPS (如 16ms = 60帧)。
        /// </summary>
        public static IWorkflow<T> Throttle<T>(this IWorkflow<T> source, TimeSpan interval)
        {
            // 用 Stopwatch.GetTimestamp() 替代 DateTime.Now：
            //   DateTime.Now 走 OS 时间 + TimeZone offset 计算,单次 ~100ns 级别;
            //   Stopwatch.GetTimestamp() 直接 QPC 调用,~10ns,对每帧 mouse move 命中显著。
            long intervalTicks = (long)((double)interval.Ticks * System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond);

            return new WorkflowEngine<T>((next, error) =>
            {
                long lastEmitTicks = long.MinValue / 2; // 留足空间避免首次差值 underflow
                object gate = new object();

                return source.Subscribe(data =>
                {
                    lock (gate)
                    {
                        long now = System.Diagnostics.Stopwatch.GetTimestamp();
                        if ((now - lastEmitTicks) >= intervalTicks)
                        {
                            lastEmitTicks = now;
                            next(data);
                        }
                    }
                }, error);
            });
        }

        /// <summary>
        /// 防抖算子 (Debounce)：必须在上游保持静默达到指定时间后，才放行最后一个信号。如果期间再来信号，倒计时清零重置。
        /// 适用于：窗口拖拽调整大小 (Resize) 结束、搜索框打字停顿后统一发起网络请求。
        /// </summary>
        // 实现方式:用单例 System.Threading.Timer + Change() 重置语义。
        //   旧实现每个上游事件都 new CancellationTokenSource + Task.Delay + ContinueWith + 闭包(~3-5 alloc/事件),
        //   resize / mouse drag 一秒灌进 60 个事件等于 180-300 个对象,直接进 Gen0 GC 噪音。
        //   新实现 setup 阶段 new 一个 Timer,每次新数据只调 Timer.Change(delay, infinite) 重新计时,
        //   per-emit 零分配。延迟到达后 Timer 在 ThreadPool 触发,经 syncContext 回主线程发射。
        public static IWorkflow<T> Debounce<T>(this IWorkflow<T> source, TimeSpan delay)
        {
            return new WorkflowEngine<T>((next, error) =>
            {
                object gate = new object();
                T pendingData = default!;
                bool hasPending = false;

                // 💥 线程调度保护核心：捕获调用管线装配时的同步上下文 (如 WPF 的 Dispatcher)
                var syncContext = SynchronizationContext.Current;

                // SendOrPostCallback 在 Subscribe scope 一次性 new,Post 时不再 per-fire 闭包分配。
                // state 槽位塞 boxed T(引用类型 T 不装箱;值类型 T 装箱仅在 Debounce 实际 fire 时一次,
                // 远少于上游高频事件)。
                SendOrPostCallback postCallback = state =>
                {
                    try { next((T)state!); }
                    catch (Exception ex) { error(ex); }
                };

                Timer? timer = null;
                timer = new Timer(_ =>
                {
                    T toEmit;
                    lock (gate)
                    {
                        // 取走 + 清零;Dispose 后侥幸触发的 stray callback 看到 hasPending=false 安全退出。
                        if (!hasPending) return;
                        toEmit = pendingData;
                        hasPending = false;
                        pendingData = default!;
                    }

                    if (syncContext != null) syncContext.Post(postCallback, toEmit);
                    else
                    {
                        try { next(toEmit); }
                        catch (Exception ex) { error(ex); }
                    }
                }, null, Timeout.Infinite, Timeout.Infinite);

                var sub = source.Subscribe(data =>
                {
                    lock (gate)
                    {
                        pendingData = data;
                        hasPending = true;
                        // Change() 重置已 armed 的定时器到新的 delay,内部状态机原地更新,无堆分配。
                        timer.Change(delay, Timeout.InfiniteTimeSpan);
                    }
                }, error);

                // Phase 11 / §H.10：CompositeDisposable 保留 sub 的 IPausable 能力
                return new CompositeDisposable(sub, new DisposableAction(() =>
                {
                    lock (gate)
                    {
                        timer.Dispose();
                        hasPending = false;
                        pendingData = default!;
                    }
                }));
            });
        }

        // =================================================================
        // 6. 调试与监控 (Diagnostics)
        // =================================================================

        /// <summary>
        /// 窥探算子：不阻断管线，仅仅将当前经过的数据提取出来。
        /// 适用于：在复杂管线中间打断点、输出 Log 日志。
        /// </summary>
        public static IWorkflow<T> Inspect<T>(this IWorkflow<T> source, Action<T> inspector)
        {
            return source.Use((data, next) =>
            {
                inspector(data);
                next(data);
            });
        }

        /// <summary>
        /// 性能测速算子：测量数据包经过其下方整个后续管线所消耗的时间。
        /// 仅当耗时超过 2ms 时才会输出警告日志。
        /// </summary>
        public static IWorkflow<T> Measure<T>(this IWorkflow<T> source, string label)
        {
            return source.Use((data, next) =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                next(data);
                sw.Stop();

                if (sw.ElapsedMilliseconds > 2)
                {
                    System.Diagnostics.Debug.WriteLine($"[Perf Warning] {label}: {sw.ElapsedMilliseconds}ms");
                }
            });
        }

        // =================================================================
        // 💥 7. 异构多流缝合算子 (Combine & Merge)
        // =================================================================

        /// <summary>
        /// 💥 异构流并集核心 (CombineLatest)
        /// 监听两个流，当任意一个流发出新值时，将其与另一个流当前的“最新快照”组合下发。
        /// 极其适合金融图表中的：多数据源时序对齐（如基准收益叠图）、双引脚并发计算（利用最高+最低算振幅）。
        /// </summary>
        public static IWorkflow<TResult> CombineLatest<T1, T2, TResult>(
            this IWorkflow<T1> source1,
            IWorkflow<T2> source2,
            Func<T1, T2, TResult> resultSelector)
        {
            return new WorkflowEngine<TResult>((next, error) =>
            {
                // 💥 线程安全大锁：防止两个网络请求几乎在同一微妙到达导致的读写争用崩溃
                object gate = new object();

                T1? val1 = default;
                bool hasVal1 = false;

                T2? val2 = default;
                bool hasVal2 = false;

                var sub1 = source1.Subscribe(x =>
                {
                    lock (gate)
                    {
                        val1 = x;
                        hasVal1 = true;
                        // 铁律：必须等两边流都有了初始值，才允许扣动发射扳机
                        if (hasVal2)
                        {
                            try { next(resultSelector(val1!, val2!)); }
                            catch (Exception ex) { error(ex); }
                        }
                    }
                }, error);

                var sub2 = source2.Subscribe(x =>
                {
                    lock (gate)
                    {
                        val2 = x;
                        hasVal2 = true;
                        if (hasVal1)
                        {
                            try { next(resultSelector(val1!, val2!)); }
                            catch (Exception ex) { error(ex); }
                        }
                    }
                }, error);

                // Phase 11 / §H.10：CompositeDisposable 做多上游 fan-out，IPausable 自动传递
                return new CompositeDisposable(sub1, sub2);
            });
        }

        public static IWorkflow<TResult> CombineLatest<T1, T2, T3, TResult>(
            this IWorkflow<T1> source1,
            IWorkflow<T2> source2,
            IWorkflow<T3> source3,
            Func<T1, T2, T3, TResult> resultSelector)
        {
            return new WorkflowEngine<TResult>((next, error) =>
            {
                object gate = new object();
                T1? v1 = default; bool h1 = false;
                T2? v2 = default; bool h2 = false;
                T3? v3 = default; bool h3 = false;

                void TryEmit()
                {
                    if (h1 && h2 && h3)
                        try { next(resultSelector(v1!, v2!, v3!)); } catch (Exception ex) { error(ex); }
                }

                var s1 = source1.Subscribe(x => { lock (gate) { v1 = x; h1 = true; TryEmit(); } }, error);
                var s2 = source2.Subscribe(x => { lock (gate) { v2 = x; h2 = true; TryEmit(); } }, error);
                var s3 = source3.Subscribe(x => { lock (gate) { v3 = x; h3 = true; TryEmit(); } }, error);

                return new CompositeDisposable(s1, s2, s3);
            });
        }

        public static IWorkflow<TResult> CombineLatest<T1, T2, T3, T4, TResult>(
            this IWorkflow<T1> source1,
            IWorkflow<T2> source2,
            IWorkflow<T3> source3,
            IWorkflow<T4> source4,
            Func<T1, T2, T3, T4, TResult> resultSelector)
        {
            return new WorkflowEngine<TResult>((next, error) =>
            {
                object gate = new object();
                T1? v1 = default; bool h1 = false;
                T2? v2 = default; bool h2 = false;
                T3? v3 = default; bool h3 = false;
                T4? v4 = default; bool h4 = false;

                void TryEmit()
                {
                    if (h1 && h2 && h3 && h4)
                        try { next(resultSelector(v1!, v2!, v3!, v4!)); } catch (Exception ex) { error(ex); }
                }

                var s1 = source1.Subscribe(x => { lock (gate) { v1 = x; h1 = true; TryEmit(); } }, error);
                var s2 = source2.Subscribe(x => { lock (gate) { v2 = x; h2 = true; TryEmit(); } }, error);
                var s3 = source3.Subscribe(x => { lock (gate) { v3 = x; h3 = true; TryEmit(); } }, error);
                var s4 = source4.Subscribe(x => { lock (gate) { v4 = x; h4 = true; TryEmit(); } }, error);

                return new CompositeDisposable(s1, s2, s3, s4);
            });
        }

        /// <summary>
        /// 💥 终极生命周期附着算子
        /// 将不受管线控制的外部资源释放动作，强行挂载到响应式流的退订事件上！
        /// 一旦这根数据流被图表切断，顺手连带执行自定义的销毁逻辑。
        /// </summary>
        public static IWorkflow<T> DoOnDispose<T>(this IWorkflow<T> source, Action onDispose)
        {
            return new WorkflowEngine<T>((onNext, onError) =>
            {
                var upstreamSub = source.Subscribe(onNext, onError);

                // Phase 11 / §H.10：CompositeDisposable 保留 upstreamSub 的 IPausable
                return new CompositeDisposable(upstreamSub, new DisposableAction(onDispose));
            });
        }

        // =================================================================
        // 8. 绘制终点 (Terminal / Sink)
        // =================================================================

        // 为未来的并行渲染引擎预留的基础互斥设施
        private static readonly ConditionalWeakTable<ChartCell, SemaphoreSlim> _cellLocks = new();

        /// <summary>
        /// 💥 图表绘制终点：纯粹的数据卸货路由器
        /// 负责将上游已经算好的拓扑数据执行业务赋值，并强行向图表大坝发起 VSync 重绘请求。
        /// </summary>
        /// <param name="source">已锚定宿主图表的渲染流</param>
        /// <param name="configure">状态落盘与引脚赋值逻辑</param>
        public static IDisposable Plot<TData>(
                    this IRenderFlow<TData> source,
                    Action<TData> configure)
        {
            var chart = source.Chart;

            return source.Subscribe(data =>
            {
                // UI 防御：如果宿主进程即将关闭，直接掐死本次渲染
                if (Application.Current == null || chart.Dispatcher.HasShutdownStarted) return;

                // 1. 💥 执行状态交接 (仅赋值，绝不可在此处涉及底层绘图对象)
                try
                {
                    configure(data);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Plot Configure Error] {ex}");
                }

                // 2. 💥 呼叫大坝：统一申请下一帧物理重绘！
                // 不管你当前身处 WebSocket 的后台线程，还是触发自鼠标事件的主线程，
                // 底层的 RequestUpdate 内部自带线程编排与渲染阀门挂载，安全且 0 阻塞！
                chart.RequestUpdate(_ => { });

            },
            ex => System.Diagnostics.Debug.WriteLine($"[Workflow Terminal Error] {ex}"));
        }
    }
}