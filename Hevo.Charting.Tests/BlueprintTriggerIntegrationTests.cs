using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 协议扩展 §K 端到端集成测试 —— Triggers + AutoDiscover + Delegate-prop handler 注入,一条龙。
    /// 模拟 K线 迁移后的形态:Window 装配 3 行 (蓝图 + 模块 + Run),不写业务。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintTriggerIntegrationTests
    {
        public BlueprintTriggerIntegrationTests()
        {
            ComponentRegistry.Register<TestDataSource>();
        }

        /// <summary>
        /// "K线-shaped" handler 模块 —— 模拟业务侧的 KLineHandlers,用 [BlueprintHandler] 一次性暴露所有委托。
        /// 真实 K线 模块跟它结构相同,只是依赖 KLineDataSource 而非 TestDataSource。
        /// </summary>
        public sealed class FakeKLineHandlers
        {
            public int HeartbeatCalls { get; private set; }
            public int PagingCalls { get; private set; }

            [BlueprintHandler("on_heartbeat")]
            public Task<bool> OnHeartbeatAsync(VersionToken tick, CancellationToken token)
            {
                HeartbeatCalls++;
                return Task.FromResult(true);
            }

            [BlueprintHandler("on_require_paging_data")]
            public Task<bool> OnRequirePagingDataAsync(int anchorIndex, int count)
            {
                PagingCalls++;
                return Task.FromResult(true);
            }

            [BlueprintHandler("format_future_time")]
            public string FormatFutureTimeLabel(int logicalIndex) => $"future-{logicalIndex}";
        }

        [Fact]
        public void AutoDiscover_RegistersAllKLineHandlers()
        {
            var module = new FakeKLineHandlers();
            var registry = new BlueprintHandlerRegistry().AutoDiscover(module);

            Assert.True(registry.Contains("on_heartbeat"));
            Assert.True(registry.Contains("on_require_paging_data"));
            Assert.True(registry.Contains("format_future_time"));
            // 心跳 handler 必须能被 TryGetFetch 解析(Trigger 装配会用)
            Assert.NotNull(registry.TryGetFetch("on_heartbeat"));
        }

        [Fact]
        public void DryRun_KLineShapedBlueprint_WithHandlers_Clean()
        {
            // 蓝图含 Trigger + ChartInteractionFeature.OnRequireDataAsync (Delegate-prop) +
            //          CrosshairFeature.FutureXLabelFormatter (Delegate-prop)。
            // 业务侧通过 AutoDiscover 把 module 提供的 handler 注册进 registry。
            // DryRun 应识别所有 handler 引用都已注册,0 BP_HANDLER_NOT_REGISTERED / BP_TRIGGER_HANDLER_MISSING。
            var module = new FakeKLineHandlers();
            var handlers = new BlueprintHandlerRegistry().AutoDiscover(module);

            var bp = MakeKLineShapedBlueprint();
            var result = BlueprintLauncher.DryRun(bp, handlers);

            Assert.Null(result.Error);
            // Property delegate 引用 / Trigger handler 引用 — 全部应通过校验
            Assert.DoesNotContain(result.Diagnostics, d =>
                d.Code == "BP_HANDLER_NOT_REGISTERED" ||
                d.Code == "BP_TRIGGER_HANDLER_MISSING");
        }

        [Fact]
        public void DryRun_TriggerHandlerMissing_ReportsWarning()
        {
            // 无 handlers / 缺一个 → 应报 BP_TRIGGER_HANDLER_MISSING
            var bp = MakeKLineShapedBlueprint();
            var result = BlueprintLauncher.DryRun(bp, handlers: null);

            Assert.Contains(result.Diagnostics, d =>
                d.Code == "BP_TRIGGER_HANDLER_MISSING" &&
                d.FeatureTypeName == "on_heartbeat");
        }

        [Fact]
        public void DryRun_DelegatePropertyHandlerMissing_ReportsWarning()
        {
            var bp = MakeKLineShapedBlueprint();
            // 注册了 trigger handler 但没注册 paging / formatter
            var partial = new BlueprintHandlerRegistry()
                .RegisterFetch("on_heartbeat", (_, _) => Task.FromResult(true));

            var result = BlueprintLauncher.DryRun(bp, partial);

            Assert.Contains(result.Diagnostics, d =>
                d.Code == "BP_HANDLER_NOT_REGISTERED" &&
                d.FeatureTypeName == nameof(DataPagingFeature) &&
                d.PortName == nameof(DataPagingFeature.OnRequireDataAsync));
        }

        /// <summary>
        /// 模拟 TimeShareHandlers 模块:为 ScheduleDomainAxisFeature 提供 LogicalLength + IndexToTime
        /// 两个 §K Delegate handler。Func&lt;int&gt; 跟 Func&lt;int, DateTime&gt; 两个签名都被 AutoDiscover 反射推断出来。
        /// </summary>
        public sealed class FakeScheduleAxisHandlers
        {
            [BlueprintHandler("ts_logical_length")]
            public int GetLogicalLength() => 240;

            [BlueprintHandler("ts_index_to_time")]
            public DateTime IndexToTime(int idx) => new DateTime(2026, 1, 1, 9, 30, 0).AddMinutes(idx);
        }

        [Fact]
        public void DryRun_ScheduleDomainAxis_WithBothHandlers_Clean()
        {
            // 校验 ScheduleDomainAxisFeature 的两个 Delegate 属性引用都能由 §K AutoDiscover 解析
            // —— 任一缺失,DryRun 应报 BP_HANDLER_NOT_REGISTERED;两个都注册时,无 handler 相关 warning。
            var handlers = new BlueprintHandlerRegistry().AutoDiscover(new FakeScheduleAxisHandlers());
            var bp = MakeScheduleDomainAxisBlueprint();

            var result = BlueprintLauncher.DryRun(bp, handlers);

            Assert.Null(result.Error);
            Assert.DoesNotContain(result.Diagnostics, d =>
                d.Code == "BP_HANDLER_NOT_REGISTERED" &&
                d.FeatureTypeName == "ScheduleDomainAxisFeature");
        }

        [Fact]
        public void DryRun_ScheduleDomainAxis_HandlersMissing_ReportsBothWarnings()
        {
            // 完全不传 handlers,DryRun 应报两个 BP_HANDLER_NOT_REGISTERED
            // (LogicalLengthProvider + IndexToTimeProvider 各一)。这是"显式提醒,运行时该字段会被 skip"。
            var bp = MakeScheduleDomainAxisBlueprint();
            var result = BlueprintLauncher.DryRun(bp, handlers: null);

            Assert.Contains(result.Diagnostics, d =>
                d.Code == "BP_HANDLER_NOT_REGISTERED" &&
                d.FeatureTypeName == "ScheduleDomainAxisFeature" &&
                d.PortName == "LogicalLengthProvider");
            Assert.Contains(result.Diagnostics, d =>
                d.Code == "BP_HANDLER_NOT_REGISTERED" &&
                d.FeatureTypeName == "ScheduleDomainAxisFeature" &&
                d.PortName == "IndexToTimeProvider");
        }

        /// <summary>
        /// ScheduleDomainAxisFeature 形态蓝图:骨架最小化,只焦化在两个 Delegate handler 引用 +
        /// RangePort 接 VP_ActiveRange。AxisStyle / GridStyle 在 in-process 工厂场景下走 trait 实例,
        /// 测试为简化省略(走 init 默认值,DryRun 不校验 Properties 内容只校验 handler refs 与端口绑定)。
        /// </summary>
        private static ChartBlueprint MakeScheduleDomainAxisBlueprint() => new()
        {
            DataSource = new DataSourceModel { TypeName = nameof(TestDataSource) },
            Features =
            {
                new FeatureModel
                {
                    TypeName = "ScheduleDomainAxisFeature",
                    Properties = new Dictionary<string, object?>
                    {
                        ["LogicalLengthProvider"] = "ts_logical_length",
                        ["IndexToTimeProvider"]   = "ts_index_to_time",
                    },
                    PortBindings = new Dictionary<string, object?>
                    {
                        ["RangePort"] = "VP_ActiveRange",
                    },
                },
            },
        };

        [Fact]
        public void DryRun_TimeShareShapedBlueprint_NoTriggerNoHandlers_Clean()
        {
            // 分时形态:无 Triggers (push-based via SubscribePush)、无 Delegate-prop handler。
            // DryRun 应不出 BP_HANDLER_NOT_REGISTERED / BP_TRIGGER_HANDLER_MISSING / BP_TRIGGER_*
            // — 跟 K线 形态形成对比,验证"声明式心跳"协议对纯推送业务零负担。
            var bp = MakeTimeShareShapedBlueprint();
            var result = BlueprintLauncher.DryRun(bp, handlers: null);

            Assert.Null(result.Error);
            Assert.DoesNotContain(result.Diagnostics, d =>
                d.Code == "BP_HANDLER_NOT_REGISTERED" ||
                d.Code == "BP_TRIGGER_HANDLER_MISSING" ||
                d.Code == "BP_TRIGGER_UNKNOWN_KIND" ||
                d.Code == "BP_TRIGGER_BAD_INTERVAL");
        }

        /// <summary>
        /// 分时形态蓝图骨架:DataSource (scalar+vector mappings) + UniversalAutoScale + LineSeries。
        /// 关键差异:无 Triggers (推送驱动),无 Delegate-prop (无业务闭包)。
        /// 用 TestDataSource (Value=double + Time=DateTime) 模拟分时 Time/Price 字段。
        /// </summary>
        private static ChartBlueprint MakeTimeShareShapedBlueprint() => new()
        {
            DataSource = new DataSourceModel
            {
                TypeName = nameof(TestDataSource),
                ScalarMappings = new Dictionary<string, string>
                {
                    ["LogicalLength"] = "VP_LogicalLength",
                },
                VectorMappings = new Dictionary<string, string>
                {
                    ["Value"] = "ts_price",
                    ["Time"]  = "ts_time",
                },
            },
            // No Triggers: TimeShare uses ReactiveDataSource push subscription
            Features =
            {
                new FeatureModel
                {
                    TypeName = nameof(UniversalAutoScaleFeature),
                    Properties = new Dictionary<string, object?> { ["PaddingRatio"] = 0.05 },
                    PortBindings = new Dictionary<string, object?>
                    {
                        ["YRangePort"]  = "ts_yrange",
                        ["ValuePorts"] = new List<string> { "ts_price" },
                    },
                },
                new FeatureModel
                {
                    TypeName = nameof(LineSeriesFeature),
                    PortBindings = new Dictionary<string, object?>
                    {
                        ["DataPort"]   = "ts_price",
                        ["YRangePort"] = "ts_yrange",
                    },
                },
            },
        };

        /// <summary>
        /// 模拟 K线 蓝图骨架:DataSource + Trigger(心跳) + ChartInteractionFeature(分页 handler) +
        /// CrosshairDoubleFeature(formatter handler)。结构跟真实 K线 蓝图同形态,只是简化了 axes/series
        /// 部分以隔离协议测试关注点。
        /// </summary>
        private static ChartBlueprint MakeKLineShapedBlueprint() => new()
        {
            DataSource = new DataSourceModel { TypeName = nameof(TestDataSource) },
            Triggers =
            {
                new TriggerModel { Kind = "Interval", IntervalSeconds = 1.0, Handler = "on_heartbeat" },
            },
            Features =
            {
                new FeatureModel
                {
                    // OnRequireDataAsync 实际在 DataPagingFeature 上,EnableStandardPointer
                    // 内部把它跟 ChartInteractionFeature 配对装入 (FeatureCanvasExtensions.cs:433)。
                    // 蓝图侧业务直接配 DataPagingFeature 即可。
                    TypeName = nameof(DataPagingFeature),
                    Properties = new Dictionary<string, object?>
                    {
                        ["OnRequireDataAsync"] = "on_require_paging_data",  // 字符串 → handler 名
                    },
                },
                new FeatureModel
                {
                    TypeName = "CrosshairDoubleFeature",
                    Properties = new Dictionary<string, object?>
                    {
                        ["FutureXLabelFormatter"] = "format_future_time",
                    },
                    PortBindings = new Dictionary<string, object?>
                    {
                        ["HitStatePort"] = "hit_state",
                    },
                },
            },
        };
    }
}
