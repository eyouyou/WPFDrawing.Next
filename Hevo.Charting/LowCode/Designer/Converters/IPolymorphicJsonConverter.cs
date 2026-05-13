using System;
using System.Collections.Generic;

namespace Hevo.Charting.LowCode.Designer.Converters
{
    /// <summary>
    /// 多态 JSON 转换器的"自我描述"契约 —— 让蓝图编辑器(<c>NodeEditorWindow</c>)
    /// 拿到 converter 处理的目标接口 + 所有支持的 kind 列表 + 每种 kind 的默认实例工厂,
    /// 据此为该属性类型自动渲染一个下拉框(kind 选择)+ 嵌套子编辑器(选中 kind 的字段递归编辑)。
    ///
    /// <para>
    /// <b>为什么不靠 <c>NodeEditorWindow.EnumerateInterfaceInstances</c> 扫静态字段?</b>
    /// 那条老路径要求每个 impl 自挂 <c>public static Instance</c>,对**无状态实现**(如
    /// <c>SmartAdaptiveZoomStrategy</c>)成立,对**带参实现**(如 <c>FixedWindowZoomStrategy.WindowBars</c>
    /// / <c>ThresholdBrushResolver.Threshold</c> / <c>HevoLiteralString.Text</c>)单例语义不成立 ——
    /// 各业务实例参数不同,根本没有"代表性单例"可挂。
    /// </para>
    ///
    /// <para>
    /// <b>设计</b>:converter 同时是 JSON 序列化器又是 kind 注册表。一个 converter 实例对应
    /// 一个接口/抽象类的全部多态实现。<see cref="BlueprintJsonOptions.Default"/> 已经把所有
    /// converter 集中注册了,编辑器只需 <c>OfType&lt;IPolymorphicJsonConverter&gt;()</c> 过滤即可。
    /// </para>
    ///
    /// <para>
    /// <b>典型用法</b>(<c>ChartInteractionFeature.ZoomStrategy</c> 字段编辑):
    /// 1. 用户在编辑器点 <c>ChartInteractionFeature</c> 节点 → 看到 <c>ZoomStrategy</c> 属性
    /// 2. <c>NodeEditorWindow</c> 发现 <see cref="IZoomStrategy"/> 有 polymorphic converter → 渲染 kind 下拉
    /// 3. 下拉列出 8 个选项(6 无参 + 2 带参),用户选 <c>FixedWindow</c>
    /// 4. picker 调 <see cref="PolymorphicKind.Factory"/> 拿默认 <c>new FixedWindowZoomStrategy()</c>
    /// 5. 反射枚举该 instance 的 editable 属性(<c>WindowBars</c> / <c>Alignment</c>)
    ///    → 递归调 <c>NodeEditorWindow.MakeEditorByType</c> 给每个属性渲染子编辑器
    /// 6. 用户改 <c>WindowBars=200</c> → reader 整合 → Confirm 写回 ChartInteractionFeature.Properties.ZoomStrategy
    /// </para>
    /// </summary>
    public interface IPolymorphicJsonConverter
    {
        /// <summary>本 converter 服务的目标类型 —— 接口或抽象类。</summary>
        Type TargetType { get; }

        /// <summary>
        /// 所有支持的多态实现 kind。编辑器按此列表填下拉,匹配当前值类型时按
        /// <see cref="PolymorphicKind.ImplType"/> 比较(<c>IsInstanceOfType</c>)选中初始项。
        /// 顺序就是下拉显示顺序 —— 把"最常用 / 默认推荐"项放前面。
        /// </summary>
        IReadOnlyList<PolymorphicKind> Kinds { get; }
    }

    /// <summary>
    /// 多态 kind 选项的描述 —— 一行 = converter 支持的一种实现。
    /// </summary>
    /// <param name="Label">下拉里显示的人类可读名(典型 = 类型名,如 <c>"FixedWindow"</c>)。</param>
    /// <param name="ImplType">该 kind 对应的具体实现类型,用于 picker 按当前值类型匹配选中项。</param>
    /// <param name="Factory">
    /// 默认实例工厂。用户在下拉里**改选**到此 kind 时,picker 调一次拿到种子 instance;
    /// 无状态实现典型返回 <c>X.Instance</c> 单例,带参实现典型 <c>new X()</c> 给出"合理默认"
    /// 让用户在下方子编辑器里继续改字段。
    /// </param>
    public sealed record PolymorphicKind(string Label, Type ImplType, Func<object> Factory);
}
