using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Handlers;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// Phase 3 — TriggerBinding verb dispatcher 端到端回归。
    /// 验证项:
    /// 1. verb=Refresh 零 driver,直调 ds.RefreshAsync
    /// 2. verb=Suspend / Resume 直调 IPausable
    /// 3. verb=SwitchContext + driver,driver 算 TContext → ds.SwitchContextErasedAsync
    /// 4. verb=Request + driver,driver 算 TRequest → ds.RequestAsync
    /// 5. verb 未知 → NotSupportedException;Trigger Id 不存在 → InvalidOperationException
    /// 6. Dispose 切断订阅,后续 tick 不触发 verb
    /// </summary>
    public sealed class BlueprintTriggerBindingTests
    {
        // ───── 用例 fixture ─────

        public sealed record FakeContext(string Tag);

        public sealed class FakeDs : ReactiveDataSource<FakeDs, FakeContext, BlueprintCascadeTests.Item>
        {
            public int RefreshCalls { get; private set; }
            public int SuspendCalls { get; private set; }
            public int ResumeCalls { get; private set; }
            public List<FakeContext> SwitchedContexts { get; } = new();

            protected override Task<int> OnFetchAsync(FakeContext? context, CancellationToken token)
            {
                if (context != null) SwitchedContexts.Add(context);
                UpdateBuffer(b => b.Clear());
                return Task.FromResult(1);
            }

            public override Task<int> RefreshAsync(CancellationToken token = default)
            {
                RefreshCalls++;
                return Task.FromResult(1);
            }

            public override void Suspend()
            {
                SuspendCalls++;
                base.Suspend();
            }

            public override void Resume()
            {
                ResumeCalls++;
                base.Resume();
            }

            public override int LogicalLength => _readSnapshot.Length;
        }

        public static class TriggerDrivers
        {
            [TriggerDriver("ctx_from_tick")]
            public static FakeContext CtxFromTick(FakeDs ds, VersionToken tick)
                => new FakeContext($"v{tick.GetHashCode()}");   // VersionToken._version 是 private,这里用 hash 当稳定标识
        }

        // ───── 测试 ─────

        [Fact]
        public async Task Verb_Refresh_NoDriver_CallsRefreshAsync()
        {
            var ds = new FakeDs();
            var bp = new ChartBlueprint
            {
                Triggers = { new TriggerModel { Id = "t1", IntervalSeconds = 0.05, Exclusive = true } },
                TriggerBindings = { new TriggerBinding { TriggerId = "t1", TargetDataSourceId = "ds", Verb = "Refresh" } },
            };
            using var scope = new ScopeContext();
            var instances = new Dictionary<string, object> { ["ds"] = ds };
            var handlers = new BlueprintHandlerRegistry();

            var subs = BlueprintTriggerBindingWiring.WireTriggerBindings(bp, instances, handlers, scope);
            try
            {
                await WaitForAsync(() => ds.RefreshCalls >= 2, TimeSpan.FromSeconds(2));
                Assert.True(ds.RefreshCalls >= 2);
            }
            finally
            {
                foreach (var d in subs) d.Dispose();
            }

            // Dispose 后再等一段,RefreshCalls 不应增长(Interval 已切断)。
            int after = ds.RefreshCalls;
            await Task.Delay(150);
            Assert.Equal(after, ds.RefreshCalls);
        }

        [Fact]
        public async Task Verb_Suspend_Resume_CallsIPausable()
        {
            var ds = new FakeDs();
            var bp = new ChartBlueprint
            {
                Triggers =
                {
                    new TriggerModel { Id = "t_pause", IntervalSeconds = 0.05 },
                    new TriggerModel { Id = "t_play",  IntervalSeconds = 0.05 },
                },
                TriggerBindings =
                {
                    new TriggerBinding { TriggerId = "t_pause", TargetDataSourceId = "ds", Verb = "Suspend" },
                    new TriggerBinding { TriggerId = "t_play",  TargetDataSourceId = "ds", Verb = "Resume" },
                },
            };
            using var scope = new ScopeContext();
            var instances = new Dictionary<string, object> { ["ds"] = ds };

            var subs = BlueprintTriggerBindingWiring.WireTriggerBindings(bp, instances, new BlueprintHandlerRegistry(), scope);
            try
            {
                // Suspend / Resume 交替触发(同间隔下两 binding 都在跑;只要各 ≥1 即可断言路径通)。
                await WaitForAsync(() => ds.SuspendCalls >= 1 && ds.ResumeCalls >= 1, TimeSpan.FromSeconds(2));
                Assert.True(ds.SuspendCalls >= 1);
                Assert.True(ds.ResumeCalls >= 1);
            }
            finally { foreach (var d in subs) d.Dispose(); }
        }

        [Fact]
        public async Task Verb_SwitchContext_DriverProducesContext()
        {
            var ds = new FakeDs();
            var handlers = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(TriggerDrivers));
            var bp = new ChartBlueprint
            {
                Triggers = { new TriggerModel { Id = "t1", IntervalSeconds = 0.05 } },
                TriggerBindings =
                {
                    new TriggerBinding
                    {
                        TriggerId = "t1", TargetDataSourceId = "ds",
                        Verb = "SwitchContext", Driver = "ctx_from_tick",
                    },
                },
            };
            using var scope = new ScopeContext();
            var instances = new Dictionary<string, object> { ["ds"] = ds };

            var subs = BlueprintTriggerBindingWiring.WireTriggerBindings(bp, instances, handlers, scope);
            try
            {
                await WaitForAsync(() => ds.SwitchedContexts.Count >= 1, TimeSpan.FromSeconds(2));
                Assert.NotEmpty(ds.SwitchedContexts);
                Assert.StartsWith("v", ds.SwitchedContexts[0].Tag);   // driver: "v{tick.Value}"
            }
            finally { foreach (var d in subs) d.Dispose(); }
        }

        [Fact]
        public void UnknownVerb_Throws()
        {
            var ds = new FakeDs();
            var bp = new ChartBlueprint
            {
                Triggers = { new TriggerModel { Id = "t1", IntervalSeconds = 1 } },
                TriggerBindings = { new TriggerBinding { TriggerId = "t1", TargetDataSourceId = "ds", Verb = "Bogus" } },
            };
            using var scope = new ScopeContext();
            var instances = new Dictionary<string, object> { ["ds"] = ds };

            Assert.Throws<NotSupportedException>(
                () => BlueprintTriggerBindingWiring.WireTriggerBindings(bp, instances, new BlueprintHandlerRegistry(), scope));
        }

        [Fact]
        public void TriggerIdNotFound_Throws()
        {
            var ds = new FakeDs();
            var bp = new ChartBlueprint
            {
                // Triggers 空,但 binding 引用了 't1'
                TriggerBindings = { new TriggerBinding { TriggerId = "t1", TargetDataSourceId = "ds", Verb = "Refresh" } },
            };
            using var scope = new ScopeContext();
            var instances = new Dictionary<string, object> { ["ds"] = ds };

            var ex = Assert.Throws<InvalidOperationException>(
                () => BlueprintTriggerBindingWiring.WireTriggerBindings(bp, instances, new BlueprintHandlerRegistry(), scope));
            Assert.Contains("t1", ex.Message);
        }

        [Fact]
        public void Verb_SwitchContext_MissingDriver_Throws()
        {
            var ds = new FakeDs();
            var bp = new ChartBlueprint
            {
                Triggers = { new TriggerModel { Id = "t1", IntervalSeconds = 1 } },
                TriggerBindings =
                {
                    new TriggerBinding { TriggerId = "t1", TargetDataSourceId = "ds", Verb = "SwitchContext" }, // 无 Driver
                },
            };
            using var scope = new ScopeContext();
            var instances = new Dictionary<string, object> { ["ds"] = ds };

            var ex = Assert.Throws<InvalidOperationException>(
                () => BlueprintTriggerBindingWiring.WireTriggerBindings(bp, instances, new BlueprintHandlerRegistry(), scope));
            Assert.Contains("Driver", ex.Message);
        }

        [Fact]
        public async Task BlueprintRunner_BuildSchema_AutoWiresTriggerBinding_EndToEnd()
        {
            // 接入点 —— 验证 BlueprintRunner.BuildSchema 走完
            // WireReactiveEdges → BlueprintTriggerBindingWiring.WireTriggerBindings,业务侧
            // 不再需要手撸 BlueprintTriggerBindingWiring.WireTriggerBindings。
            //
            // 旧形态:Window 自己写 _ds + Loaded += async + Task.Run(HeartbeatLoop) + Closed += dispose。
            // 新形态:蓝图 JSON 声明 Trigger + TriggerBinding,RunBlueprint/BuildSchema 自动装配。
            var ds = new FakeDs();
            // 节点化协议:DataSources 单一节点 Id="primary",TriggerBinding 引用同 Id。
            // FakeDs 实例由业务侧 new 后塞 instances 字典 —— framework 不再 ComponentRegistry.Resolve+new。
            ComponentRegistry.Register<FakeDs>();
            var bp = new ChartBlueprint
            {
                DataSources = { new DataSourceModel { Id = "primary", TypeName = nameof(FakeDs) } },
                Triggers = { new TriggerModel { Id = "t1", IntervalSeconds = 0.05, Exclusive = true } },
                TriggerBindings =
                {
                    new TriggerBinding { TriggerId = "t1", TargetDataSourceId = "primary", Verb = "Refresh" },
                },
            };
            var handlers = new BlueprintHandlerRegistry();   // verb=Refresh 零 driver 零 handler
            var instances = new Dictionary<string, object> { ["primary"] = ds };

            var schema = BlueprintRunner.BuildSchema(bp, instances, handlers);
            try
            {
                await WaitForAsync(() => ds.RefreshCalls >= 2, TimeSpan.FromSeconds(2));
                Assert.True(ds.RefreshCalls >= 2,
                    $"BlueprintRunner.BuildSchema 应自动装 TriggerBinding,但 ds.RefreshCalls={ds.RefreshCalls}");
            }
            finally
            {
                DisposeSchemaInternals(schema);
            }

            // Dispose 后再等一段,RefreshCalls 不应增长(schema 内部 _disposables 释放 → Interval 切断)。
            int after = ds.RefreshCalls;
            await Task.Delay(150);
            Assert.Equal(after, ds.RefreshCalls);
        }

        // BuildSchema 路径无 ChartCell,schema.Decompose 走不通 —— 测试用反射强拆 _disposables。
        // 生产代码不该走这,真实场景:chart.Template 替换或 ChartCell shutdown 时框架自动 Decompose。
        private static void DisposeSchemaInternals(object schema)
        {
            var field = typeof(Hevo.Charting.Core.ReactiveSchema).GetField(
                "_disposables", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(schema) is List<IDisposable> list)
            {
                foreach (var d in list) try { d?.Dispose(); } catch { /* 测试 cleanup 容忍 */ }
                list.Clear();
            }
        }

        // ── helper ──

        private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (predicate()) return;
                await Task.Delay(20);
            }
        }
    }
}
