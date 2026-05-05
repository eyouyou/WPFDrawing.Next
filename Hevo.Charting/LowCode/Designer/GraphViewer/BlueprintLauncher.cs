using Hevo.Charting.Core;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 把 GraphViewer 当前画布编译出来的 ChartBlueprint 跑起来。
    /// 反射 BlueprintRunner.Run&lt;TItem&gt; 泛型调用,弹一个独立窗口承载 ChartCell。
    /// 调用方失败时不会抛——返回错误字符串供 UI 显示。
    ///
    /// <para>
    /// <b>关于运行时上下文注入</b>:蓝图模型只描述拓扑(类型 / 属性 / 端口),
    /// 描述不出"业务上下文"(典型:<c>TimeShareDataSource.LoadAsync(security)</c> 或 <c>Context = ...</c>)。
    /// 通过 <paramref name="configureDataSource"/> 回调,在 BlueprintRunner 执行**之前**给数据源注入运行时上下文,
    /// 业务侧可以为每种数据源准备一个 demo loader (例如:if (ds is TimeShareDataSource t) await t.LoadAsync(...))。
    /// 不传该回调则数据源保持初始空状态,LogicalLength=0 → ViewportManager 会写 invalid range → 整个图表
    /// 不渲染任何数据,仅看到底色(俗称"黑屏")。
    /// </para>
    /// </summary>
    public static class BlueprintLauncher
    {
        /// <summary>
        /// 启动并返回错误描述(成功时返回 null)。失败时弹窗 owner 用作父级。
        /// <para>
        /// 注入数据源运行时上下文有两条路,任选其一即可:
        /// <list type="bullet">
        ///   <item><b><paramref name="dataSourceContext"/></b>:类型化便捷参数。Launcher 反射查 DataSource 上是否有
        ///         <c>LoadAsync(TContext)</c> 方法(<see cref="ReactiveDataSource{TSource, TContext, TItem}"/> 派生类标配),
        ///         参数类型可赋值 → 自动调用,fire-and-forget。</item>
        ///   <item><b><paramref name="configureDataSource"/></b>:万能回调。<paramref name="dataSourceContext"/> 应用之后
        ///         同步执行,业务可在此自由处理(MinuteCeiling 配置 / 多步 Load 链 / 自定义 SwitchContext 等)。</item>
        /// </list>
        /// 两个参数同时提供时,先按类型化参数走 LoadAsync,再跑回调。
        /// </para>
        /// </summary>
        public static string? Launch(
            ChartBlueprint blueprint,
            Window? owner = null,
            object? dataSourceContext = null,
            Action<object>? configureDataSource = null)
        {
            if (blueprint.DataSource == null || string.IsNullOrEmpty(blueprint.DataSource.TypeName))
                return "蓝图缺少 DataSource 节点。请先在画布上添加一个数据源。";

            // 1. 解析 DataSource 类型
            Type dsType;
            try { dsType = ComponentRegistry.Resolve(blueprint.DataSource.TypeName); }
            catch (Exception ex) { return $"DataSource 类型未登记:{ex.Message}"; }

            // 2. 必须有 public 无参 ctor (低代码运行时唯一可走的实例化路径)
            if (dsType.GetConstructor(Type.EmptyTypes) == null)
                return $"{dsType.Name} 没有公共无参构造函数,低代码场景无法实例化。"
                     + "请用业务侧 BlueprintRunner.Run<...>(cell, blueprint, ds) 自行装配。";

            // 3. 找出 TItem 类型 (走 DataSource<TSource,TItem> 基类链)
            var itemType = NodeFactory.FindDataSourceItemType(dsType);
            if (itemType == null)
                return $"{dsType.Name} 不是 DataSource<TSource, TItem> 的派生类型。";

            // 4. 实例化数据源
            object dsInstance;
            try { dsInstance = Activator.CreateInstance(dsType)!; }
            catch (Exception ex) { return $"实例化 {dsType.Name} 失败:{ex.Message}"; }

            // 4.1 类型化上下文参数 (优先) — 反射约定:找参数类型可吃 dataSourceContext 的 LoadAsync 方法。
            //   ReactiveDataSource<TSource, TContext, TItem>.LoadAsync(TContext) / LoadAsync(TContext, CancellationToken)
            //   两种 overload 都能命中,默认 CancellationToken.None。
            if (dataSourceContext != null)
            {
                var ctxType = dataSourceContext.GetType();
                var loadMethod = dsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "LoadAsync" && m.GetParameters().Length >= 1)
                    .FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(ctxType));
                if (loadMethod != null)
                {
                    var ps = loadMethod.GetParameters();
                    var args = new object?[ps.Length];
                    args[0] = dataSourceContext;
                    for (int i = 1; i < ps.Length; i++)
                    {
                        args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                                  : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                    }
                    try { _ = loadMethod.Invoke(dsInstance, args); }    // fire & forget
                    catch (Exception ex) { return $"调用 {dsType.Name}.LoadAsync 失败:{ex.Message}"; }
                }
                // 没找到匹配的 LoadAsync 不算错 —— 业务可能想用 configureDataSource 走别的路径。
            }

            // 4.2 万能回调,业务自由发挥。
            try { configureDataSource?.Invoke(dsInstance); }
            catch (Exception ex) { return $"configureDataSource 抛异常:{ex.Message}"; }

            // 5. 取 ds.Stream(IWorkflow<DataSnapshot<TItem>>)
            var streamProp = dsType.GetProperty("Stream", BindingFlags.Public | BindingFlags.Instance);
            object? stream = streamProp?.GetValue(dsInstance);
            if (stream == null)
                return $"{dsType.Name}.Stream 取不到值。请确认其继承自 DataSource<,>。";

            // 6. 拉起预览窗口
            var cell = new ChartCell { Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x1F)) };
            var win = new Window
            {
                Title = $"蓝图预览 — {dsType.Name}",
                Width = 1024, Height = 600,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = cell,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
            };

            // 7. 反射调 BlueprintRunner.Run<TItem>(cell, blueprint, dsInstance, stream)
            try
            {
                var runMethod = typeof(BlueprintRunner).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Run" && m.IsGenericMethod && m.GetGenericArguments().Length == 1)
                    .FirstOrDefault();
                if (runMethod == null) return "找不到 BlueprintRunner.Run<TItem>(...) 方法,反射调度失败。";
                var generic = runMethod.MakeGenericMethod(itemType);
                generic.Invoke(null, new object?[] { cell, blueprint, dsInstance, stream });
            }
            catch (TargetInvocationException ex) { return $"BlueprintRunner.Run 抛异常:{ex.InnerException?.Message ?? ex.Message}"; }
            catch (Exception ex) { return $"BlueprintRunner.Run 抛异常:{ex.Message}"; }

            // 8. 窗口关闭时回收 ChartCell 状态
            win.Closed += (_, __) => cell.Shutdown();
            win.Show();
            return null;
        }
    }
}
