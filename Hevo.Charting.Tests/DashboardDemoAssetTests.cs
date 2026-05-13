using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// §D2.X dashboard 资产回归 —— 锁定 <c>Hevo.Drawing.LowCodeDemo/Assets/default_kline_dashboard.json</c>
    /// 这份 demo JSON 在反序列化后两条 UI 不变式必须成立:
    /// <list type="number">
    ///   <item>
    ///     <b>每个 cell 都装了 <see cref="UniversalHeaderFeature"/></b> ——
    ///     蓝图 Features 列表里要找得到。historic bug:strategy cell 漏装,
    ///     运行期 Decorate 不跑,header 容器都不存在(用户报"第三个图没有 header")。
    ///   </item>
    ///   <item>
    ///     <b>volume cell 的 BarSeriesFeature.Meta.Name 反序列化后非空</b> ——
    ///     JSON 写成 <c>{ "kind": "literal", "text": "成交量" }</c> 经过
    ///     <see cref="HevoStringJsonConverter"/> 走 literal 路径,得到
    ///     <see cref="HevoLiteralString"/> 且文本非空。historic bug:写成 <c>{}</c>
    ///     被 fallback 成 null,header 行只有圆点 / 冒号 / 数值,中间 title 一片空。
    ///   </item>
    /// </list>
    /// <para>
    /// 测试不跑 <c>DashboardLauncher.DryRun</c> —— 那条路径要求 MockKLineDataSource
    /// 已注册,而该类住在 Hevo.Drawing.LowCodeDemo(WPF + Python.NET 重依赖),
    /// 拖进测试项目得不偿失。这里只做 JSON 结构层回归,精准覆盖两条修复。
    /// </para>
    /// </summary>
    public sealed class DashboardDemoAssetTests
    {
        private static readonly JsonSerializerOptions _json = BlueprintJsonOptions.Default;

        /// <summary>
        /// 从测试 bin 目录向上找到 repo 根(<c>Hevo.Drawing.slnx</c> 作为锚点),
        /// 再拼出 demo dashboard JSON 的路径。CI 跟本地都得对。
        /// </summary>
        private static string LocateDashboardJson()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Hevo.Drawing.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var path = Path.Combine(dir!.FullName,
                "Hevo.Drawing.LowCodeDemo", "Assets", "default_kline_dashboard.json");
            Assert.True(File.Exists(path), $"找不到 demo dashboard JSON:{path}");
            return path;
        }

        private static Dashboard LoadDashboard()
        {
            var json = File.ReadAllText(LocateDashboardJson());
            var d = JsonSerializer.Deserialize<Dashboard>(json, _json);
            Assert.NotNull(d);
            return d!;
        }

        [Fact]
        public void Dashboard_HasExpectedThreeCells()
        {
            // 防呆:如果有人删 / 加 cell,后面针对每个 cell 的断言可能会假阳性绿。
            var d = LoadDashboard();
            Assert.Equal(3, d.Cells.Count);
            Assert.Equal(new[] { "main", "volume", "strategy" },
                d.Cells.Select(c => c.Id).ToArray());
        }

        [Fact]
        public void EveryCell_HasUniversalHeaderFeature()
        {
            // 三个 cell 都该装 UniversalHeaderFeature —— 历史 strategy cell 漏装。
            var d = LoadDashboard();
            foreach (var cell in d.Cells)
            {
                var hasHeader = cell.Blueprint.Features.Any(
                    f => f.TypeName == nameof(UniversalHeaderFeature));
                Assert.True(hasHeader,
                    $"Cell '{cell.Id}' 蓝图里没有 UniversalHeaderFeature —— Header 行不会渲染。");
            }
        }

        [Fact]
        public void EveryUniversalHeader_BindsToSharedLinkedHitPort()
        {
            // 联动 header 的核心:HitStatePort 必须焊到 dashboard:linkedHit 这条共享端口,
            // 否则鼠标在主图 hover、副图 header 不跟随更新(各 cell 各跑各的 hit 状态)。
            var d = LoadDashboard();
            foreach (var cell in d.Cells)
            {
                var header = cell.Blueprint.Features.FirstOrDefault(
                    f => f.TypeName == nameof(UniversalHeaderFeature));
                Assert.NotNull(header);
                Assert.True(header!.InputBindings.TryGetValue("HitStatePort", out var hit),
                    $"Cell '{cell.Id}' 的 UniversalHeaderFeature 没焊 HitStatePort。");
                var hitId = hit?.ToString();
                Assert.Equal("dashboard:linkedHit", hitId);
            }
        }

        [Fact]
        public void VolumeCell_BarSeriesMetaName_DeserializesToNonEmptyLiteralString()
        {
            // 历史 bug:Meta.Name = `{}` → HevoStringJsonConverter 空对象兜底 → null
            // → header 行 title TextBlock 字面空白。修复:写成 { kind:"literal", text:"成交量" }。
            //
            // 反序列化路径:FeatureModel.Properties["Meta"] 是 JsonElement(蓝图运行期
            // 才走 SmartActivator 实例化成 FieldMeta);这里直接读 JsonElement,验证 kind+text
            // 是不是真的能被下游 HevoStringJsonConverter 走 literal 分支接住。
            var d = LoadDashboard();
            var volume = d.Cells.Single(c => c.Id == "volume");
            var bar = volume.Blueprint.Features.FirstOrDefault(
                f => f.TypeName == nameof(BarSeriesFeature));
            Assert.NotNull(bar);
            Assert.True(bar!.Properties.TryGetValue("Meta", out var metaObj),
                "BarSeriesFeature 缺 Meta 属性。");
            var metaElem = Assert.IsType<JsonElement>(metaObj);
            Assert.Equal(JsonValueKind.Object, metaElem.ValueKind);
            Assert.True(metaElem.TryGetProperty("Name", out var nameElem),
                "BarSeriesFeature.Meta 缺 Name 子键。");
            Assert.Equal(JsonValueKind.Object, nameElem.ValueKind);
            Assert.True(nameElem.TryGetProperty("kind", out var kindElem),
                "Meta.Name 必须含 \"kind\" 判别符 —— 历史空 {} 写法会让 header title 空白。");
            Assert.Equal("literal", kindElem.GetString());
            Assert.True(nameElem.TryGetProperty("text", out var textElem));
            Assert.False(string.IsNullOrWhiteSpace(textElem.GetString()),
                "Meta.Name.text 不能空 —— 否则 header 行 title 空白。");
        }

        [Fact]
        public void StrategyCell_HasLineSeriesAndPlotForBbBreakout()
        {
            // 防呆:Header 修复假设 strategy cell 至少有 LineSeriesFeature(收盘)+ PlotFeature(bb_breakout),
            // 它们发出 MetaTrait 后才会出现在 header 里。如果有人无意中删了这俩,header 即便
            // 装上去也是空容器,跟原 bug 现象一样,但这里能提前在测试层捕捉。
            var d = LoadDashboard();
            var strategy = d.Cells.Single(c => c.Id == "strategy");
            Assert.Contains(strategy.Blueprint.Features,
                f => f.TypeName == nameof(LineSeriesFeature));
            Assert.Contains(strategy.Blueprint.Features,
                f => f.TypeName == nameof(PlotFeature));
        }
    }
}
