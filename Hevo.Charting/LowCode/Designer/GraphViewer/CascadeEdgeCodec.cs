namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// DS-to-DS 上下文驱动边(<see cref="EdgeKind.Cascade"/>)的 codec。
    /// <list type="bullet">
    ///   <item>Encode:state 里 Kind=Cascade 的边 → <see cref="ChartBlueprint.Cascades"/></item>
    ///   <item>Decode:<see cref="ChartBlueprint.Cascades"/> → Kind=Cascade 的边(FromPortId="Stream", ToPortId="Context")</item>
    /// </list>
    /// <para>
    /// <b>互斥契约</b>:认领且仅认领 <c>Edge.Kind == Cascade</c> 的边 / <c>bp.Cascades</c> 字段。
    /// </para>
    /// </summary>
    internal sealed class CascadeEdgeCodec : IEdgeKindCodec
    {
        public string Name => nameof(CascadeEdgeCodec);

        public void Encode(IEdgeEncodeContext ctx)
        {
            foreach (var e in ctx.State.Edges)
            {
                if (e.Kind != EdgeKind.Cascade) continue;
                if (!ctx.DataSourceModelByNodeId.ContainsKey(e.FromNodeId) || !ctx.DataSourceModelByNodeId.ContainsKey(e.ToNodeId))
                {
                    Console.WriteLine($"[Hevo 蓝图警告] Cascade 边 {e.FromNodeId}->{e.ToNodeId} 端节点不是 DataSource,序列化跳过。");
                    continue;
                }
                ctx.Output.Cascades.Add(new CascadeEdge
                {
                    FromDataSourceId = e.FromNodeId,
                    ToDataSourceId   = e.ToNodeId,
                    ContextDriver    = e.Driver ?? string.Empty,
                    Trigger          = "Stream",
                });
            }
        }

        public void Decode(IEdgeDecodeContext ctx)
        {
            foreach (var c in ctx.Input.Cascades)
            {
                if (string.IsNullOrEmpty(c.FromDataSourceId) || string.IsNullOrEmpty(c.ToDataSourceId)) continue;
                ctx.EmitEdge(new Edge(
                    Id:         Guid.NewGuid().ToString("N").Substring(0, 8),
                    FromNodeId: c.FromDataSourceId, FromPortId: "Stream",
                    ToNodeId:   c.ToDataSourceId,   ToPortId:   "Context",
                    Kind:       EdgeKind.Cascade,
                    Driver:     c.ContextDriver));
            }
        }
    }
}
