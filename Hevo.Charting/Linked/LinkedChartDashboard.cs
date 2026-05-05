using Hevo.Charting.Core;
using System.Windows;
using System.Windows.Controls;

namespace Hevo.Charting.Linked
{
    /// <summary>
    /// 列式联动 dashboard 容器(领域无关):主图 + N 副图垂直堆叠,共享视口与 hit。
    ///
    /// <para>
    /// schema 永远写成独立模式(不知道 dashboard / 联动概念存在);本容器通过
    /// <see cref="SchemaContext"/> 的 attached property 把"主图 / 副图"上下文注入到对应 cell:
    /// <list type="bullet">
    ///   <item><b>主图</b>(<see cref="AddMaster"/>):<see cref="SchemaContext.LinkedMaster"/></item>
    ///   <item><b>副图</b>(<see cref="AddPane"/>):<see cref="SchemaContext.LinkedPane"/>(摘视口管家 + tooltip)</item>
    ///   <item><b>纯堆叠</b>(<see cref="AddRaw"/>):不注入,完全不联动</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 联动落地靠 <see cref="LinkedChartContext"/> 的端口镜像桥:每个 schema 的主数据流 board
    /// 就绪时通过 <see cref="ReactiveSchema.BoardActivated"/> 事件 attach,几个共享 port
    /// 写入时双向同步到所有 board。
    /// </para>
    /// </summary>
    public class LinkedChartDashboard : UserControl
    {
        public LinkedChartContext Context { get; }

        private readonly Grid _grid = new();
        private readonly List<PaneSlot> _slots = new();
        private readonly record struct PaneSlot(ReactiveSchema Schema, ChartCell Cell);

        public LinkedChartDashboard(LinkedChartContext ctx)
        {
            Context = ctx;
            Content = _grid;
        }

        // ─── 装配期 API ────────────────────────────────────────────────────

        /// <summary>加主图。<b>必须最先且只能一次</b>。主图独占视口管家、tooltip、心跳/分页等。</summary>
        public LinkedChartDashboard AddMaster(ReactiveSchema schema, double heightRatio = 3)
        {
            if (_slots.Count > 0)
                throw new InvalidOperationException("Master 必须最先加,且只能一个");
            return AppendSlot(schema, heightRatio, SchemaContext.LinkedMaster(Context));
        }

        /// <summary>加副图。装饰钩子自动摘除视口管家/tooltip 并挂 ExternalViewportFeature 标记。</summary>
        public LinkedChartDashboard AddPane(ReactiveSchema schema, double heightRatio = 1)
            => AppendSlot(schema, heightRatio, SchemaContext.LinkedPane(Context));

        /// <summary>加 raw schema(<b>不联动</b>,纯堆叠)。该 cell 自己的 viewport / hit / tooltip 完全独立。</summary>
        public LinkedChartDashboard AddRaw(ReactiveSchema schema, double heightRatio = 1)
            => AppendSlot(schema, heightRatio, attached: null);

        // ─── 运行期 API ────────────────────────────────────────────────────

        /// <summary>运行期热加副图,挂上立即纳入联动。</summary>
        public void AppendPaneAtRuntime(ReactiveSchema schema, double heightRatio = 1)
            => AppendSlot(schema, heightRatio, SchemaContext.LinkedPane(Context));

        /// <summary>运行期热删副图。<b>主图(行 0)不允许删</b>,删了等于砍掉视口管家。</summary>
        public bool RemovePaneAtRuntime(ReactiveSchema schema)
        {
            int idx = _slots.FindIndex(s => ReferenceEquals(s.Schema, schema));
            if (idx <= 0) return false;   // idx == 0 是主图,禁止移除

            var slot = _slots[idx];
            _grid.Children.Remove(slot.Cell);
            _grid.RowDefinitions.RemoveAt(idx);
            _slots.RemoveAt(idx);

            for (int i = idx; i < _slots.Count; i++)
                Grid.SetRow(_slots[i].Cell, i);

            return true;
        }

        // ─── 内部实现 ──────────────────────────────────────────────────────

        private LinkedChartDashboard AppendSlot(ReactiveSchema schema, double heightRatio, SchemaContext? attached)
        {
            // 必须先 SetAttached + 挂 BoardActivated、再赋 Template:Template 触发 OnApplyTemplate →
            // ComposeAll → ReactiveSchema.InitializeRegistry,后者从 cell 读 attached 上下文,
            // 主数据流跑通后回调 BoardActivated → AttachBoard 把镜像桥订上。
            var cell = new ChartCell();
            if (attached != null)
            {
                SchemaContext.SetAttached(cell, attached);
                schema.BoardActivated += Context.AttachBoard;
            }
            cell.Template = schema;

            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(heightRatio, GridUnitType.Star) });
            Grid.SetRow(cell, _slots.Count);
            _grid.Children.Add(cell);
            _slots.Add(new PaneSlot(schema, cell));

            return this;
        }
    }
}
