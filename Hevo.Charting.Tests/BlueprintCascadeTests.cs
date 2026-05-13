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
    /// Phase 2 — Cascade 协议端到端回归。
    /// 验证项:
    /// 1. ChartBlueprint.GetEffectiveDataSources 兼容老 DataSource 单数字段
    /// 2. [ContextDriver] 通过 AutoDiscoverStatic / AutoDiscoverType 注册到同一 BlueprintHandlerRegistry
    /// 3. WireCascades:上游 publish 触发 driver 投影 + 下游 SwitchContextErasedAsync
    /// 4. 装配期校验:driver 返回类型不匹配下游 TContext → 立即抛 InvalidOperationException
    /// 5. 装配期校验:From/To DS 未在 instances 字典 / driver 未注册 → 抛
    /// 6. Trigger != "Stream" → NotSupportedException
    /// 7. Disposable 释放后 stream publish 不再触发 driver
    /// </summary>
    public sealed class BlueprintCascadeTests
    {
        // ───── 测试用 DS + Driver ─────

        public sealed record UpstreamContext(string Tag);
        public sealed record DownstreamContext(string Tag, int Multiplier);
        public sealed record Item(int Value);

        public sealed class UpstreamDs : ReactiveDataSource<UpstreamDs, UpstreamContext, Item>
        {
            protected override Task<int> OnFetchAsync(UpstreamContext? context, CancellationToken token)
            {
                // 拉一个 item,buffer 替换。
                UpdateBuffer(b => { b.Clear(); b.Add(new Item(context?.Tag.Length ?? 0)); });
                return Task.FromResult(1);
            }

            public override int LogicalLength => _readSnapshot.Length;
        }

        public sealed class DownstreamDs : ReactiveDataSource<DownstreamDs, DownstreamContext, Item>
        {
            // 测试断言:每次 SwitchContextErased 调用,把 context 记录下来。
            public List<DownstreamContext> SwitchedContexts { get; } = new();

            protected override Task<int> OnFetchAsync(DownstreamContext? context, CancellationToken token)
            {
                if (context != null) SwitchedContexts.Add(context);
                UpdateBuffer(b => b.Clear());
                return Task.FromResult(1);
            }

            public override int LogicalLength => _readSnapshot.Length;
        }

        public static class CascadeDrivers
        {
            // 正确 driver:返回 DownstreamContext(匹配下游 TContext)。
            [ContextDriver("upstream_to_downstream")]
            public static DownstreamContext UpstreamToDownstream(UpstreamDs ds, DataSnapshot<Item> snap)
            {
                int mul = snap.Count > 0 ? snap.Items[0].Value : 0;
                return new DownstreamContext(Tag: ds.Context?.Tag ?? "?", Multiplier: mul);
            }

            // 错误 driver:返回 string,不匹配 DownstreamContext。装配期应 fail-fast。
            [ContextDriver("wrong_return_type")]
            public static string WrongReturnType(UpstreamDs ds, DataSnapshot<Item> snap) => "oops";
        }

        // ───── 用例 ─────

        // 节点化协议(2026-05):取消旧单数 DataSource 字段 + GetEffectiveDataSources 兼容 lift,
        // DataSources 是唯一 canonical 路径。原 GetEffectiveDataSources_* 三个测试已无意义,删除。

        [Fact]
        public async Task WireCascades_StreamPublish_TriggersDownstreamSwitchContext()
        {
            var upstream = new UpstreamDs();
            var downstream = new DownstreamDs();

            var registry = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(CascadeDrivers));
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "up",   TypeName = nameof(UpstreamDs) },
                    new DataSourceModel { Id = "down", TypeName = nameof(DownstreamDs) },
                },
                Cascades =
                {
                    new CascadeEdge
                    {
                        FromDataSourceId = "up",
                        ToDataSourceId   = "down",
                        ContextDriver    = "upstream_to_downstream",
                        Trigger          = "Stream",
                    },
                },
            };

            using var scope = new ScopeContext();
            var instances = new Dictionary<string, object>
            {
                ["up"]   = upstream,
                ["down"] = downstream,
            };

            var subs = BlueprintCascadeWiring.WireCascades(bp, instances, registry, scope);
            Assert.Single(subs);

            // 上游 publish:LoadAsync 触发 OnFetchAsync → UpdateBuffer → Publish → Stream emit。
            // Stream subscribe 在 cascade wiring 里;driver 投影 + downstream.SwitchContextErasedAsync 执行。
            await upstream.LoadAsync(new UpstreamContext("HELLO"), CancellationToken.None);

            // SwitchContextErasedAsync 把 ctx fan out 到 downstream._requestBus,Push 后异步 OnFetchAsync 才记录。
            // 给 OnFetchAsync 一点时间跑完 (FetchLatest pipeline 跨 Task)。
            await WaitForAsync(() => downstream.SwitchedContexts.Count > 0, TimeSpan.FromSeconds(2));

            Assert.NotEmpty(downstream.SwitchedContexts);
            var first = downstream.SwitchedContexts[0];
            Assert.Equal("HELLO", first.Tag);
            Assert.Equal(5, first.Multiplier);   // upstream OnFetchAsync 写 Item(Tag.Length=5)

            // Dispose:再 publish 不应触发 driver。
            foreach (var d in subs) d.Dispose();
            int countBefore = downstream.SwitchedContexts.Count;
            await upstream.LoadAsync(new UpstreamContext("WORLD"), CancellationToken.None);
            await Task.Delay(200);   // 给 stream 一点时间(若没 dispose,会被订阅捕获)
            Assert.Equal(countBefore, downstream.SwitchedContexts.Count);
        }

        [Fact]
        public void WireCascades_DriverReturnTypeMismatch_FailsAtAssembly()
        {
            var upstream = new UpstreamDs();
            var downstream = new DownstreamDs();
            var registry = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(CascadeDrivers));

            var bp = new ChartBlueprint
            {
                Cascades =
                {
                    new CascadeEdge
                    {
                        FromDataSourceId = "up",
                        ToDataSourceId   = "down",
                        ContextDriver    = "wrong_return_type",   // 返回 string,期望 DownstreamContext
                        Trigger          = "Stream",
                    },
                },
            };
            var instances = new Dictionary<string, object> { ["up"] = upstream, ["down"] = downstream };
            using var scope = new ScopeContext();

            var ex = Assert.Throws<InvalidOperationException>(
                () => BlueprintCascadeWiring.WireCascades(bp, instances, registry, scope));
            Assert.Contains("不匹配下游 TContext", ex.Message);
        }

        [Fact]
        public void WireCascades_MissingDriver_Throws()
        {
            var upstream = new UpstreamDs();
            var downstream = new DownstreamDs();
            var registry = new BlueprintHandlerRegistry();   // 没注册任何 driver

            var bp = new ChartBlueprint
            {
                Cascades =
                {
                    new CascadeEdge
                    {
                        FromDataSourceId = "up",
                        ToDataSourceId   = "down",
                        ContextDriver    = "nonexistent",
                        Trigger          = "Stream",
                    },
                },
            };
            var instances = new Dictionary<string, object> { ["up"] = upstream, ["down"] = downstream };
            using var scope = new ScopeContext();

            var ex = Assert.Throws<InvalidOperationException>(
                () => BlueprintCascadeWiring.WireCascades(bp, instances, registry, scope));
            Assert.Contains("ContextDriver 'nonexistent'", ex.Message);
        }

        [Fact]
        public void WireCascades_MissingDataSource_Throws()
        {
            var registry = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(CascadeDrivers));
            var bp = new ChartBlueprint
            {
                Cascades =
                {
                    new CascadeEdge
                    {
                        FromDataSourceId = "up",
                        ToDataSourceId   = "down",
                        ContextDriver    = "upstream_to_downstream",
                    },
                },
            };
            var instances = new Dictionary<string, object>();   // 字典空
            using var scope = new ScopeContext();

            var ex = Assert.Throws<InvalidOperationException>(
                () => BlueprintCascadeWiring.WireCascades(bp, instances, registry, scope));
            Assert.Contains("'up'", ex.Message);
        }

        [Fact]
        public void WireCascades_NonStreamTrigger_NotSupported()
        {
            var upstream = new UpstreamDs();
            var downstream = new DownstreamDs();
            var registry = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(CascadeDrivers));
            var bp = new ChartBlueprint
            {
                Cascades =
                {
                    new CascadeEdge
                    {
                        FromDataSourceId = "up",
                        ToDataSourceId   = "down",
                        ContextDriver    = "upstream_to_downstream",
                        Trigger          = "scatter.SelectionChanged",   // Phase 2.x feature event,主线未支持
                    },
                },
            };
            var instances = new Dictionary<string, object> { ["up"] = upstream, ["down"] = downstream };
            using var scope = new ScopeContext();

            Assert.Throws<NotSupportedException>(
                () => BlueprintCascadeWiring.WireCascades(bp, instances, registry, scope));
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
