namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// Composite fan-in upstream refs 的 codec。
    /// <list type="bullet">
    ///   <item>Encode:state 里 <c>ToPortId == "Upstreams"</c> 的边 → <see cref="DataSourceModel.UpstreamRefs"/></item>
    ///   <item>Decode:<see cref="DataSourceModel.UpstreamRefs"/> → 边(FromPortId=Stream, ToPortId=Upstreams)</item>
    /// </list>
    /// <para>
    /// <b>互斥契约</b>:认领且仅认领 <c>ToPortId == NodeFactory.UpstreamsPortId</c> 的边 / <c>dsm.UpstreamRefs</c> 字段。
    /// </para>
    /// </summary>
    internal sealed class UpstreamRefCodec : IEdgeKindCodec
    {
        public string Name => nameof(UpstreamRefCodec);

        public void Encode(IEdgeEncodeContext ctx)
        {
            foreach (var node in ctx.State.Nodes)
            {
                if (node.Kind != NodeKind.DataSource) continue;
                if (!ctx.DataSourceModelByNodeId.TryGetValue(node.Id, out var dsm)) continue;
                if (!BlueprintTypeAlias.MatchesAlias(dsm.TypeName, typeof(Hevo.Charting.WorkFlow.Composite<>))
                    && !node.InputPorts.Any(p => p.Id == NodeFactory.UpstreamsPortId)) continue;

                var upstreamRefs = ctx.State.Edges
                    .Where(e => e.ToNodeId == node.Id
                                && string.Equals(e.ToPortId, NodeFactory.UpstreamsPortId, StringComparison.Ordinal))
                    .Select(e => e.FromNodeId)
                    .Distinct()
                    .ToList();

                if (upstreamRefs.Count > 0)
                    dsm.UpstreamRefs = upstreamRefs;
            }
        }

        public void Decode(IEdgeDecodeContext ctx)
        {
            foreach (var dsm in ctx.Input.DataSources)
            {
                if (dsm.UpstreamRefs == null || dsm.UpstreamRefs.Count == 0) continue;
                foreach (var id in dsm.UpstreamRefs)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    ctx.EmitEdge(new Edge(
                        Id:         Guid.NewGuid().ToString("N").Substring(0, 8),
                        FromNodeId: id,       FromPortId: NodeFactory.StreamPortId,
                        ToNodeId:   dsm.Id,   ToPortId:   NodeFactory.UpstreamsPortId));
                }
            }
        }
    }
}
