using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hevo.Charting.Features
{
    public static class UniversalHeaderFeatureExtensions
    {
        /// <summary>
        /// 装配万能联动头:
        /// 业务侧零 viewport 字眼,helper 内部由 <see cref="UniversalHeaderFeature"/>.OnCompose 自己从
        /// base ChartFeature.Viewport(<see cref="ViewportPorts.GetAttached"/>)取 LogicalLength,
        /// 调用顺序跟 SetupViewport / 其他 helper 完全自由(不依赖装配时机)。
        /// </summary>
        public static EnvironmentBuilder SetupUniversalHeader(this EnvironmentBuilder builder, DataPort<PointerHitState?> hitPort)
        {
            builder.Canvas.Add(new UniversalHeaderFeature { HitStatePort = hitPort });
            return builder;
        }
    }
    internal class HeaderItemCache
    {
        public FieldMeta Meta { get; }
        public TextBlock ValueBlock { get; }
        public Ellipse ColorDot { get; }
        public IChartLayer SourceLayer { get; }
        public int FieldIndex { get; }

        public HeaderItemCache(FieldMeta meta, TextBlock valueBlock, Ellipse colorDot, IChartLayer sourceLayer, int fieldIndex)
        {
            Meta = meta; ValueBlock = valueBlock; ColorDot = colorDot; SourceLayer = sourceLayer; FieldIndex = fieldIndex;
        }
    }

    /// <summary>
    /// 💥 终极版万能联动头：精致排版 + 数据精准映射 + 绝对可靠的热路径刷新。
    /// <para>
    /// <b>典型用法</b>:
    /// <code>
    /// env.SetupUniversalHeader(lengthPort: ports.Length, hitPort: hitPort);
    /// // 之后凡是带 MetaTrait + DoubleSeriesDataTrait 的 Layer 都会自动出现在头部行。
    /// </code>
    /// </para>
    /// <para>
    /// <b>数据流</b>:命中索引 → 遍历所有活跃 Layer 的 MetaTrait/DoubleSeriesDataTrait → 只更新 TextBlock.Text(不重建 UI)。
    /// 指标数量 / 名字 / 颜色变化时,通过 fingerprint 比对触发 RebuildUi 重新创建子控件;之后所有更新都走对象池,0 控件分配。
    /// </para>
    /// </summary>
    public class UniversalHeaderFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        /// <summary>命中状态端口:有命中则显示对应索引的字段值;无命中则回落到末根(实时数据)。</summary>
        public DataPort<PointerHitState?> HitStatePort { get; init; } = null!;

        /// <summary>
        /// 逻辑数据长度端口:用于在数据切换时同步触发头部刷新(主要起 Watch 触发器作用)。
        /// 业务/蓝图初始化时显式喂;留空(默认 null)时 OnCompose 自动 fallback 到
        /// <c>base.Viewport.LogicalLength</c>。
        /// init-only 满足 HEVO007 引脚不可变约束;运行期 fallback 走下面的 _effectiveLengthPort 内部字段。
        /// </summary>
        public DataPort<int>? LogicalLengthPort { get; init; }

        // OnCompose 期决定实际订阅的 length port —— 业务显式喂的优先,否则 fallback Viewport.LogicalLength。
        // 不能直接写 LogicalLengthPort(init 限制),也不该用 set 让蓝图反射误覆写(HEVO007)。
        // 协议占位符 `null!`:框架时序保证 OnCompose 必先于 OnProject 跑过,使用窗口内非 null。
        // 跟 init port 的 `= null!` 同语义 —— 不是撒谎,是"装配阶段写过"的承诺。
        // OnProject 顶层无条件 UsePort 即可,符合 Hook 铁律(00.架构协议 §158)。
        private DataPort<int> _effectiveLengthPort = null!;

        // 头部容器(WrapPanel),由 Decorate 在挂载阶段一次性创建并贴在外层 DockPanel 顶部。
        private WrapPanel _headerContainer = null!;
        // UI 控件池:每个字段对应一组 (TextBlock + Ellipse),热路径只改 Text/Fill,绝不 new 控件。
        private readonly List<HeaderItemCache> _itemCaches = new();
        // 上一次所有 MetaTrait 的复合哈希。指标增删/重命名/换色时 fingerprint 跳变 → RebuildUi。
        private int _lastMetaFingerprint = -1;

        public override UIElement Decorate(UIElement inner)
        {
            var grid = new DockPanel() { LastChildFill = true};

            _headerContainer = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 8, 8, 0),
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top
            };

            grid.Children.Add(_headerContainer);
            DockPanel.SetDock(_headerContainer, Dock.Top);

            grid.Children.Add(inner);
            DockPanel.SetDock(inner, Dock.Top);

            return grid;
        }

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 业务/蓝图没显式传 LogicalLengthPort 时,从 base ChartFeature.Viewport 取。
            // OnCompose 阶段所有 features 已 add 完,PortsFeature.OnAttached 必跑过,Viewport 字段已就位。
            // 业务侧调 SetupUniversalHeader 跟 SetupViewport 顺序自由 —— helper 内部 lookup,不依赖装配时机。
            _effectiveLengthPort = LogicalLengthPort ?? Viewport.LogicalLength;
        }

        protected override void OnProject(FeatureContext ctx)
        {
            // Hook 铁律:UsePort 顶层无条件;_headerContainer / _effectiveLengthPort 由
            // Decorate / OnCompose 时序保证非 null,无需 guard。
            var (hitState, hitChanged) = ctx.UsePort(HitStatePort);
            var (totalLen, lenChanged) = ctx.UsePort(_effectiveLengthPort);

            // 1. 获取绝对可靠的当前图层元数据指纹
            int currentFingerprint = ComputeFingerprint(ctx);

            // 2. 如果指纹变了，说明指标被添加/删除，或者语言/主题变了，重建 WPF UI！
            if (currentFingerprint != _lastMetaFingerprint)
            {
                RebuildUi(ctx);
                _lastMetaFingerprint = currentFingerprint;
            }

            // 3. 热路径刷新：只改 TextBlock 的 Text，绝不 new UI 控件！
            foreach (var cache in _itemCaches)
            {
                var proxy = ctx.For(cache.SourceLayer);
                var doubleData = proxy.Read<DoubleSeriesDataTrait>();
                if (doubleData == null || cache.FieldIndex >= doubleData.FieldValues.Length) continue;

                var span = doubleData.FieldValues[cache.FieldIndex].Span;
                if (span.Length == 0) continue;

                // 鼠标移出时，默认拿屏幕最右侧的最新数据
                int targetIndex = hitState.HasValue && !hitState.Value.IsOutOfBounds
                    ? hitState.Value.LocalIndex
                    : span.Length - 1;

                // 诊断：默认态尾部出现 NaN 时，定位是哪根线、尾部连续缺多少格、最后一根有效值在哪。
                // 回退修复后保留这段，以便复现"hover 一次就变 --"时打印根因。
                if (!hitState.HasValue || hitState.Value.IsOutOfBounds)
                {
                    int probe = span.Length - 1;
                    while (probe >= 0 && double.IsNaN(span[probe])) probe--;
                    int tailNaN = (span.Length - 1) - probe;
                    if (tailNaN > 0)
                    {
                        var name = WpfRenderRegistry.ResolveString(cache.Meta.Name);
                        double lastValid = probe >= 0 ? span[probe] : double.NaN;
                        Debug.WriteLine($"[Header] line=\"{name}\" field={cache.FieldIndex} spanLen={span.Length} tailNaN={tailNaN} lastValidIdx={probe} lastValidVal={lastValid}");
                    }
                }

                if (targetIndex >= 0 && targetIndex < span.Length)
                {
                    double val = span[targetIndex];
                    if (double.IsNaN(val))
                    {
                        cache.ValueBlock.Text = "--";
                    }
                    else
                    {
                        cache.ValueBlock.Text = val.FormatValue(cache.Meta.Format, cache.Meta.Provider);
                    }

                    var dynColor = proxy.Read<DynamicColorTrait>();
                    if (dynColor != null)
                    {
                        cache.ColorDot.BindHevo(Shape.FillProperty, dynColor.Evaluator(cache.FieldIndex, targetIndex));
                    }
                }
                else
                {
                    cache.ValueBlock.Text = "--";
                }
            }
        }

        /// <summary>
        /// 💥 绝对硬核的 0-GC 指纹计算
        /// </summary>
        private int ComputeFingerprint(FeatureContext ctx)
        {
            var hash = new HashCode();
            foreach (var layer in Chart.ActiveLayers)
            {
                var meta = ctx.For(layer).Read<MetaTrait>();
                if (meta == null) continue;

                // 把每一个 FieldMeta 都塞进哈希计算器里
                // FieldMeta 是 record class:编译器自动生成值哈希,HashCode.Add<T> 走泛型不装箱
                for (int i = 0; i < meta.Fields.Length; i++)
                {
                    hash.Add(meta.Fields[i]);
                }
            }
            return hash.ToHashCode();
        }

        private void RebuildUi(FeatureContext ctx)
        {
            _headerContainer.Children.Clear();
            _itemCaches.Clear();

            var secondaryBrush = new SolidColorBrush(Color.FromRgb(160, 160, 160));

            foreach (var layer in Chart.ActiveLayers)
            {
                var meta = ctx.For(layer).Read<MetaTrait>();
                if (meta == null) continue;

                for (int i = 0; i < meta.Fields.Length; i++)
                {
                    var f = meta.Fields[i];

                    var panel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 16, 4),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var dot = new Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    };
                    dot.BindHevo(Shape.FillProperty, f.GetDefaultBrush());

                    var title = new TextBlock
                    {
                        Foreground = secondaryBrush,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    title.BindHevo(TextBlock.TextProperty, f.Name);

                    var colon = new TextBlock
                    {
                        Text = ": ",
                        Foreground = secondaryBrush,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var val = new TextBlock
                    {
                        Text = "--",
                        Foreground = Brushes.White,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(2, 0, 0, 0)
                    };

                    panel.Children.Add(dot);
                    panel.Children.Add(title);
                    panel.Children.Add(colon);
                    panel.Children.Add(val);

                    _headerContainer.Children.Add(panel);
                    _itemCaches.Add(new HeaderItemCache(f, val, dot, layer, i));
                }
            }
        }
    }
}
