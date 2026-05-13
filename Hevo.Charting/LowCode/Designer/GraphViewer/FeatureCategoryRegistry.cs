using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 跟 <see cref="FeatureCanvasExtensions"/> 里的 4 类 builder 一一对应:
    /// Environment / Axes / Series / Interaction。
    /// 加上 DataSource / Trait 两个非 Feature 类别,共 6 个泳道。
    /// 蓝图编辑器据此排版、配色、过滤 picker。
    /// </summary>
    public enum FeatureCategory
    {
        /// <summary>归类不明 (业务自定义未登记 / 不属于任何标准建模域)。</summary>
        Unknown,
        /// <summary>数据源节点 (<c>DataSource&lt;,&gt;</c> 派生)。蓝图侧 1 张图通常 1 个。</summary>
        DataSource,
        /// <summary>视觉特质 / 全局样式播种 (<see cref="Hevo.Charting.Abstractions.IVisualTrait"/>)。</summary>
        Trait,
        /// <summary>环境域:布局 / 视口 / 自适应缩放 / 装饰底色 / 头部 (对应 <see cref="EnvironmentBuilder"/>)。</summary>
        Environment,
        /// <summary>坐标轴域 (对应 <see cref="AxesBuilder"/>)。</summary>
        Axes,
        /// <summary>数据序列渲染域:线 / 柱 / K 线 / Plot / Compute (对应 <see cref="SeriesBuilder"/>)。
        /// Compute 节点跟 LowCodeDemo / KLineDetailBlueprint 的约定一样归入 Series 泳道,跟下游 LineSeries
        /// 同行水平排开 —— 数据流从左到右(Compute → LineSeries),不另起一行避免视觉碎片化。</summary>
        Series,
        /// <summary>交互行为域:十字光标 / Tooltip / 滚轮缩放 (对应 <see cref="InteractionBuilder"/>)。</summary>
        Interaction,
    }

    /// <summary>
    /// 类别 → 颜色 / 文案 的统一查询入口。Picker、画布泳道、节点底色都走这里。
    /// </summary>
    public static class FeatureCategoryStyle
    {
        public static string DisplayName(FeatureCategory c) => c switch
        {
            FeatureCategory.DataSource  => "数据源",
            FeatureCategory.Trait       => "Trait",
            FeatureCategory.Environment => "Environment 环境",
            FeatureCategory.Axes        => "Axes 坐标轴",
            FeatureCategory.Series      => "Series 序列",
            FeatureCategory.Interaction => "Interaction 交互",
            _ => "Unknown",
        };

        /// <summary>泳道半透明底色 (画布上每条 category 横幅用此色铺底)。</summary>
        public static Color ZoneFill(FeatureCategory c) => c switch
        {
            FeatureCategory.DataSource  => Color.FromArgb(0x18, 0x4F, 0xC3, 0xF7),
            FeatureCategory.Trait       => Color.FromArgb(0x18, 0xCE, 0x93, 0xD8),
            FeatureCategory.Environment => Color.FromArgb(0x18, 0x90, 0xA4, 0xAE),
            FeatureCategory.Axes        => Color.FromArgb(0x18, 0xFF, 0xB7, 0x4D),
            FeatureCategory.Series      => Color.FromArgb(0x18, 0x66, 0xBB, 0x6A),
            FeatureCategory.Interaction => Color.FromArgb(0x18, 0xEF, 0x53, 0x50),
            _ => Color.FromArgb(0x18, 0x55, 0x5C, 0x66),
        };

        /// <summary>类别 label 文字色。</summary>
        public static Color ZoneLabel(FeatureCategory c) => c switch
        {
            FeatureCategory.DataSource  => Color.FromRgb(0x4F, 0xC3, 0xF7),
            FeatureCategory.Trait       => Color.FromRgb(0xCE, 0x93, 0xD8),
            FeatureCategory.Environment => Color.FromRgb(0xB0, 0xBE, 0xC5),
            FeatureCategory.Axes        => Color.FromRgb(0xFF, 0xB7, 0x4D),
            FeatureCategory.Series      => Color.FromRgb(0x66, 0xBB, 0x6A),
            FeatureCategory.Interaction => Color.FromRgb(0xEF, 0x53, 0x50),
            _ => Color.FromRgb(0x9E, 0xA8, 0xB0),
        };
    }

    /// <summary>
    /// 类型 → 类别的覆盖表 (沿继承链回溯查)。
    /// 默认登记一份框架标准映射 (<see cref="GraphViewerBootstrap"/>),
    /// 业务自定义 Feature 启动时:<c>FeatureCategoryRegistry.Register&lt;MyFeature&gt;(FeatureCategory.Series)</c>。
    /// </summary>
    public static class FeatureCategoryRegistry
    {
        private static readonly Dictionary<Type, FeatureCategory> _map = new();

        public static void Register(Type type, FeatureCategory category) => _map[type] = category;
        public static void Register<T>(FeatureCategory category) => _map[typeof(T)] = category;

        /// <summary>查类别:registry → 沿继承链回溯 → 由 <see cref="NodeFactory.Classify"/> 推断 (DataSource / Trait / Feature 兜底)。</summary>
        public static FeatureCategory Resolve(Type type)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                if (_map.TryGetValue(t, out var c)) return c;
            }
            // 兜底:走 NodeFactory.Classify 推个粗类别
            return NodeFactory.Classify(type) switch
            {
                NodeFactory.Kind.DataSource => FeatureCategory.DataSource,
                NodeFactory.Kind.Trait => FeatureCategory.Trait,
                NodeFactory.Kind.Feature => FeatureCategory.Unknown, // 未登记的 Feature 归 Unknown
                _ => FeatureCategory.Unknown,
            };
        }
    }
}
