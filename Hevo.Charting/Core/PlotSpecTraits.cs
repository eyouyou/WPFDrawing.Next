using System;
using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// 单个散点 spec —— 纯渲染规格,跟数据来源(Python / C# / hardcoded)及交互 metadata 完全无关。
    /// hover 文本 / click 数据走业务侧按 Index 反查自家数据源,框架不绑定 metadata 字段。
    /// </summary>
    /// <param name="X">逻辑 X。Feature 显式 mode 时 publish layer-local <see cref="XAxisTrait"/> 线性映射;inherit 走主图 shared XAxisTrait。</param>
    /// <param name="Y">逻辑 Y(同上)。</param>
    /// <param name="Size">圆半径,像素。典型 1.5~9.0。</param>
    /// <param name="Color">颜色串,<c>"#RRGGBB"</c> / <c>"#AARRGGBB"</c>。Layer 内 brush cache。</param>
    public readonly record struct ScatterPoint(
        float X,
        float Y,
        float Size,
        string Color);

    /// <summary>
    /// 单个箭头 marker spec —— 纯渲染规格。
    /// </summary>
    /// <param name="LogicalX">逻辑 X(K 线索引或时间)。layer 走 XAxisTrait/ScaleStrategyTrait 投影。</param>
    /// <param name="LogicalY">逻辑 Y(典型 close 价 / 信号位置)。</param>
    /// <param name="Direction">方向:<c>"up"</c> / <c>"down"</c> / <c>"left"</c> / <c>"right"</c>。</param>
    /// <param name="Color">颜色串,同 <see cref="ScatterPoint.Color"/>。</param>
    /// <param name="Size">三角半边长,像素。典型 8.0。</param>
    public readonly record struct ArrowMarker(
        double LogicalX,
        double LogicalY,
        string Direction,
        string Color,
        float Size);

    /// <summary>scatter trait —— ScatterPlotLayer 据此画 N 个 ellipse。</summary>
    public sealed record ScatterSpecTrait(ReadOnlyMemory<ScatterPoint> Points) : IVisualTrait;

    /// <summary>arrow markers trait —— ArrowMarkerLayer 据此画 N 个三角箭头。</summary>
    public sealed record ArrowSpecTrait(ReadOnlyMemory<ArrowMarker> Markers) : IVisualTrait;

    /// <summary>
    /// 单个文字 marker spec —— 纯渲染规格。
    /// </summary>
    /// <param name="LogicalX">逻辑 X(K 线索引或时间)。layer 走 XAxisTrait/ScaleStrategyTrait 投影。</param>
    /// <param name="LogicalY">逻辑 Y(典型 close 价 / 信号位置)。</param>
    /// <param name="Text">显示文本(典型 "BUY" / "SELL" / "x1.5" 等短标签)。</param>
    /// <param name="Color">颜色串,同 <see cref="ScatterPoint.Color"/>。</param>
    /// <param name="FontSize">字号,像素。典型 11.0。</param>
    /// <param name="Anchor">锚点相对逻辑点的位置:<c>"above"</c>(默认,文本底中对齐逻辑点上方) /
    /// <c>"below"</c>(顶中对齐下方)/ <c>"center"</c>(中心对齐) / <c>"left"</c>(右中对齐左侧)/
    /// <c>"right"</c>(左中对齐右侧)。</param>
    public readonly record struct TextMarker(
        double LogicalX,
        double LogicalY,
        string Text,
        string Color,
        float FontSize,
        string Anchor);

    /// <summary>text markers trait —— TextMarkerLayer 据此画 N 个字符串标签。</summary>
    public sealed record TextSpecTrait(ReadOnlyMemory<TextMarker> Markers) : IVisualTrait;
}
