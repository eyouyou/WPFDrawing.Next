using Hevo.Charting.Abstractions;
using Hevo.Charting.Buildin;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using System.Reflection;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer.Wrappers
{
    // ==========================================
    //  泛型 → double / 构造参数 → init 属性 的低代码 wrapper。
    //  低代码.md §6 列出的两类边界情况:
    //    • 泛型 Feature (CrosshairFeature<TX> / TooltipWidgetFeature<TX>):
    //        BuiltinRegistration 会过滤掉泛型定义,但派一个 closed 子类(typeof TX = double) 即可登记。
    //    • 含构造参数的 Feature (AxisFeature(ITickProvider, name)):
    //        派一个无参子类,在 ctor 里默认构造 TickProvider,
    //        把"用户应该可配的字段(format / placement)"提升为 init 属性,OnCompose 反射回填 init-only 默认。
    //  这些 wrapper 由 BuiltinRegistration 自动收录,picker 里就直接能选。
    // ==========================================

    /// <summary>
    /// <see cref="CrosshairFeature{TX}"/> 的 closed-over-double 子类,蓝图可见。
    /// X 轴原始数据通常是时间索引(双精度浮点表达,允许小数索引),故 double 是最通用的 TX。
    /// </summary>
    public class CrosshairDoubleFeature : CrosshairFeature<double> { }

    /// <summary>
    /// <see cref="TooltipWidgetFeature{TX}"/> 的 closed-over-double 子类,蓝图可见。
    /// </summary>
    public class TooltipDoubleWidgetFeature : TooltipWidgetFeature<double> { }

    // ⚠️ AxisLowCodeFeature 已删除 ——
    //    AxisFeature 自身已经支持公共无参 ctor + Format / Name / TickProvider / Placement init 属性,
    //    蓝图直接用框架原 AxisFeature 即可,不需要 wrapper。

    // ⚠️ AutoScaleLowCodeFeature 已删除 ——
    //    DynamicChartSchema.DefineFeatures 现在直接支持 DataPort<T>[] 数组属性的 CSV 多绑,
    //    UniversalAutoScaleFeature.ValuePorts 在 GraphViewer 里就是一根标了 [] 的扇入端口,
    //    可同时接受多根连线。代价 = ChartBlueprint.cs 里 ~30 行通用解析逻辑,
    //    收益 = 任何 DataPort<T>[] 属性免 wrapper、自动可视化连线。

    // 💡 Link 系列 wrappers (跟传统 .Inject / .ForwardTo / .ProjectExtent / .LinkStream DSL 一一对应)
    //    单独搬到 LinkWrappers.cs,在画布上以专门的"桥接 feature"形式呈现,等价 DSL 一行 = 蓝图一根连线。

    // ⚠️ GridLayoutLowCode / PlotAreaDecorLowCode 已删除 ——
    //    编辑器 (NodeEditorWindow) 现在直接认 ChartLength / IHevoBrush 类型,
    //    不需要为这种"纯 shape massage"的 feature 写 wrapper。
}
