using System.Collections.Generic;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// 💥 多 cell 联动 dashboard 的蓝图协议(§D1)。
    /// <para>
    /// 一个 <see cref="Dashboard"/> = N 个 <see cref="DashboardCell"/> 垂直堆叠 + 共享 <see cref="ViewportPorts"/>
    /// (X 轴缩放 / 十字线状态联动)。每个 cell 自身仍持一个 <see cref="ChartBlueprint"/>,沿用
    /// 现有单 cell 协议;dashboard 层只声明"角色 + 高度比 + 横向边距",联动机制完全复用
    /// 框架已有的 <see cref="Hevo.Charting.Linked.LinkedChartContext"/> 端口镜像桥。
    /// </para>
    /// <para>
    /// <b>JSON 形态</b>(MVP,跟 <see cref="ChartBlueprint"/> 同 round-trip 风格):
    /// </para>
    /// <code>
    /// {
    ///   "Cells": [
    ///     { "Id": "main",   "Role": "Master", "HeightRatio": 3, "Blueprint": { /* ChartBlueprint */ } },
    ///     { "Id": "volume", "Role": "Pane",   "HeightRatio": 1, "Blueprint": { /* ChartBlueprint */ } },
    ///     { "Id": "macd",   "Role": "Pane",   "HeightRatio": 1, "Blueprint": { /* ChartBlueprint */ } }
    ///   ],
    ///   "HorizontalLeftPixel":  60,
    ///   "HorizontalRightPixel": 0
    /// }
    /// </code>
    /// <para>
    /// <b>跨 cell 联动</b>:框架现有 <see cref="Hevo.Charting.Linked.LinkedChartContext"/> 默认镜像 4 个共享端口
    /// (UserRange / ActiveRange / LogicalLength / SharedHit),所有 Master/Pane 角色的 cell 自动加入镜像。
    /// 这是 v1 协议:不暴露"哪些 port 被镜像"的精细配置,够用 K线主+量副+MACD 这类典型场景。
    /// 真要业务自定义 port 镜像,通过 DashboardLauncher 时传入扩展过的 LinkedChartContext 子类(逃逸阀)。
    /// </para>
    /// </summary>
    public class Dashboard
    {
        /// <summary>cell 列表。<b>顺序 = 垂直堆叠顺序</b>:[0] 渲染在最上,[N-1] 在最下。</summary>
        public List<DashboardCell> Cells { get; set; } = new();

        /// <summary>所有联动 cell 共享的左侧水平边距(像素),Y 轴标签留白。crosshair 跨 cell 对齐前提:所有 cell 同左右边距。</summary>
        public double HorizontalLeftPixel { get; set; } = 60;

        /// <summary>所有联动 cell 共享的右侧水平边距(像素)。</summary>
        public double HorizontalRightPixel { get; set; } = 0;
    }

    /// <summary>
    /// dashboard 内单个 cell 的描述。Blueprint 跟单 cell 同协议,Role 决定运行时怎么注入到
    /// <see cref="Hevo.Charting.Linked.LinkedChartDashboard"/>(主图独占视口管家 / 副图摘 / Raw 不联动)。
    /// </summary>
    public class DashboardCell
    {
        /// <summary>稳定标识符,用于运行时反查 / 错误诊断。Dashboard 内必须唯一。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>cell 在 dashboard 里的角色——决定联动注入路径。</summary>
        public DashboardCellRole Role { get; set; } = DashboardCellRole.Pane;

        /// <summary>WPF Grid 行高比(GridUnitType.Star)。主图典型 3,副图典型 1。</summary>
        public double HeightRatio { get; set; } = 1;

        /// <summary>cell 内承载的 ChartBlueprint(沿用单 cell 协议,DataSource / Features / Triggers 全套)。</summary>
        public ChartBlueprint Blueprint { get; set; } = new();
    }

    /// <summary>
    /// dashboard 内 cell 的角色枚举。决定 <see cref="Hevo.Charting.Linked.SchemaContext"/> 注入哪一种:
    /// <list type="bullet">
    ///   <item><b>Master</b>:LinkedMaster(共享视口 + 强制水平边距,保留视口管家 / tooltip 在主图)</item>
    ///   <item><b>Pane</b>:LinkedPane(共享视口 + 强制边距 + 摘视口管家 / 摘 tooltip + 挂 ExternalViewportFeature)</item>
    ///   <item><b>Raw</b>:不注入 SchemaContext(完全不联动,纯堆叠;典型场景:dashboard 里堆一张独立的离线图)</item>
    /// </list>
    /// <b>顺序约束</b>:整个 Dashboard.Cells 中 Master 必须最先且只能一个(等价于 LinkedChartDashboard.AddMaster 的运行时检查)。
    /// </summary>
    public enum DashboardCellRole
    {
        /// <summary>主图:Linked、保留视口管家 + tooltip。</summary>
        Master = 0,
        /// <summary>副图:Linked、摘视口管家 + 摘 tooltip。</summary>
        Pane = 1,
        /// <summary>独立 cell(纯堆叠,不联动)。</summary>
        Raw = 2,
    }
}
