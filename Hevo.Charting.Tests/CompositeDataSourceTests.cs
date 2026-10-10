using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hevo.Charting;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// <see cref="CompositeDataSource{TSource,TItem}"/> 测试 —— 纯合并器(Attach + Merge)语义验证 +
    /// 蓝图 node-wrap 装配回归(BlueprintRunner 反射 Attach DataSourceModel.UpstreamRefs)。
    /// <list type="number">
    ///   <item>程序化用法:<c>new MyMerger(); Attach(upA.Stream); Attach(upB.Stream)</c> ——
    ///         Composite 不感知"蓝图"概念,业务可纯 C# 使用</item>
    ///   <item>蓝图用法:DataSourceModel.UpstreamRefs 一等节点引用上游 DS,framework 装配期反射 Attach</item>
    ///   <item>Deferred load:BuildSchemaDeferred 后 LoadAsync 不 fire,StartLoading 才 fire</item>
    ///   <item>WhenAll gate:多上游凑齐后才首次 Publish,凑齐后退化 WhenAny</item>
    ///   <item>MergeHandler:JSON 配 handler 名 → framework auto-discover 后 Merge 走 handler</item>
    /// </list>
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class CompositeDataSourceTests
    {
        public CompositeDataSourceTests()
        {
            ComponentRegistry.Register<ShardA>();
            ComponentRegistry.Register<ShardB>();
            ComponentRegistry.Register<UnionMergeDs>();
        }

        // ───── 业务侧 fake DSs ─────

        public sealed record TestRow(string Symbol, double Price);

        public sealed class ShardA : ReactiveDataSource<ShardA, string, TestRow>
        {
            protected override Task<int> OnFetchAsync(string? context, CancellationToken token)
            {
                UpdateBuffer(b =>
                {
                    b.Clear();
                    if (!string.IsNullOrEmpty(context))
                    {
                        b.Add(new TestRow($"A.{context}.1", 1.0));
                        b.Add(new TestRow($"A.{context}.2", 2.0));
                    }
                });
                return Task.FromResult(2);
            }
            public override int LogicalLength => _readSnapshot.Length;
        }

        public sealed class ShardB : ReactiveDataSource<ShardB, string, TestRow>
        {
            protected override Task<int> OnFetchAsync(string? context, CancellationToken token)
            {
                UpdateBuffer(b =>
                {
                    b.Clear();
                    if (!string.IsNullOrEmpty(context))
                    {
                        b.Add(new TestRow($"B.{context}.1", 10.0));
                    }
                });
                return Task.FromResult(1);
            }
            public override int LogicalLength => _readSnapshot.Length;
        }

        /// <summary>
        /// 业务合并策略:append + dedupe by slot —— 单测专用 fixture。
        /// 每个上游 publish 替换自己的 slot,不同 slot 共存。
        /// </summary>
        public sealed class UnionMergeDs : CompositeDataSource<UnionMergeDs, TestRow>
        {
            public List<int> MergeCalls { get; } = new();

            protected override void Merge(List<TestRow> buffer, int slotIndex, DataSnapshot<TestRow> upstreamSnapshot)
            {
                MergeCalls.Add(slotIndex);
                var prefix = $"{slotIndex}|";
                buffer.RemoveAll(r => r.Symbol.StartsWith(prefix));
                var span = upstreamSnapshot.Items.AsSpan().Slice(0, upstreamSnapshot.Count);
                for (int i = 0; i < span.Length; i++) buffer.Add(span[i] with { Symbol = prefix + span[i].Symbol });
            }
        }

        // ============================================================
        // §1 纯程序化用法(完全脱离蓝图)
        // ============================================================

        [Fact]
        public async Task Composite_ProgrammaticAttach_MergesUpstreamStreams()
        {
            // CompositeDataSource 纯领域 API:无 lookup、无 LoadAsync 反射、无 spec 列表 —— 业务直接 Attach Stream。
            var shA = new ShardA();
            var shB = new ShardB();
            var merger = new UnionMergeDs();

            merger.Attach(shA.Stream);   // slot 0
            merger.Attach(shB.Stream);   // slot 1

            await shA.LoadAsync("a");
            await shB.LoadAsync("b");

            await WaitForAsync(() => merger.MergeCalls.Count >= 2, TimeSpan.FromSeconds(2));
            Assert.Equal(2, merger.MergeCalls.Count);
            Assert.Equal(2, merger.UpstreamCount);
            Assert.Equal(3, merger.LogicalLength);   // ShardA 2 行 + ShardB 1 行
        }

        [Fact]
        public async Task Composite_AttachDispose_StopsReceivingFurtherPublishes()
        {
            var shA = new ShardA();
            var merger = new UnionMergeDs();
            var sub = merger.Attach(shA.Stream);

            await shA.LoadAsync("first");
            await WaitForAsync(() => merger.MergeCalls.Count >= 1, TimeSpan.FromSeconds(2));
            Assert.Single(merger.MergeCalls);

            sub.Dispose();   // 切断订阅
            await shA.LoadAsync("second");
            await Task.Delay(50);
            Assert.Single(merger.MergeCalls);   // 第二次 publish 不再触发 Merge
        }

        // ============================================================
        // §2 Composite<TItem> 内置 sealed 类 —— MergeMode / MergeHandler
        // ============================================================

        public static class TestRowMergeHandlers
        {
            public static int InvokeCount;

            [BlueprintHandler("testrow_union_dedupe")]
            public static void UnionDedupe(List<TestRow> buffer, int slotIndex, DataSnapshot<TestRow> snap)
            {
                InvokeCount++;
                var prefix = $"{slotIndex}|";
                buffer.RemoveAll(r => r.Symbol.StartsWith(prefix));
                var span = snap.Items.AsSpan().Slice(0, snap.Count);
                for (int i = 0; i < span.Length; i++) buffer.Add(span[i] with { Symbol = prefix + span[i].Symbol });
            }
        }

        [Fact]
        public async Task Composite_DefaultReplaceBySlot_NoHandler()
        {
            var shA = new ShardA();
            var shB = new ShardB();
            var composite = new Composite<TestRow>();

            composite.Attach(shA.Stream);
            composite.Attach(shB.Stream);

            await shA.LoadAsync("a");
            await shB.LoadAsync("b");

            await WaitForAsync(() => composite.LogicalLength >= 3, TimeSpan.FromSeconds(2));
            Assert.Equal(3, composite.LogicalLength);

            // 拼接顺序 = slot 0 先 + slot 1 后
            var snap = composite.GetSnapshot();
            var symbols = new List<string>();
            for (int i = 0; i < snap.Count; i++) symbols.Add(snap.Items[i].Symbol);
            Assert.Contains("A.a.1", symbols);
            Assert.Contains("A.a.2", symbols);
            Assert.Contains("B.b.1", symbols);
            Assert.True(symbols.IndexOf("A.a.1") < symbols.IndexOf("B.b.1"));   // slot 0 在 slot 1 之前
        }

        [Fact]
        public async Task Composite_WhenAll_GatesFirstPublishUntilAllUpstreamsEmit()
        {
            var shA = new ShardA();
            var shB = new ShardB();
            var composite = new Composite<TestRow> { MergeMode = MergeMode.WhenAll };

            composite.Attach(shA.Stream);
            composite.Attach(shB.Stream);

            int publishCount = 0;
            using var sub = composite.Stream.Subscribe(_ => Interlocked.Increment(ref publishCount));

            await shA.LoadAsync("a");
            await Task.Delay(20);
            Assert.Equal(0, publishCount);
            Assert.Equal(0, composite.LogicalLength);   // 半态不暴露

            await shB.LoadAsync("b");
            await WaitForAsync(() => publishCount >= 1, TimeSpan.FromSeconds(1));
            Assert.Equal(1, publishCount);
            Assert.Equal(3, composite.LogicalLength);

            // 凑齐后退化 WhenAny:任一上游再 publish 都立刻透传
            await shA.LoadAsync("a2");
            await WaitForAsync(() => publishCount >= 2, TimeSpan.FromSeconds(1));
            Assert.Equal(2, publishCount);
        }

        [Fact]
        public async Task Composite_WhenAny_PublishesOnEachUpstreamEmitFromStart()
        {
            var shA = new ShardA();
            var shB = new ShardB();
            var composite = new Composite<TestRow>();   // MergeMode 默认 WhenAny

            composite.Attach(shA.Stream);
            composite.Attach(shB.Stream);

            int publishCount = 0;
            using var sub = composite.Stream.Subscribe(_ => Interlocked.Increment(ref publishCount));

            await shA.LoadAsync("a");
            await WaitForAsync(() => publishCount >= 1, TimeSpan.FromSeconds(1));
            Assert.Equal(1, publishCount);   // 不 gate,第 1 路就 publish

            await shB.LoadAsync("b");
            await WaitForAsync(() => publishCount >= 2, TimeSpan.FromSeconds(1));
            Assert.Equal(2, publishCount);
        }

        // ============================================================
        // §3 蓝图 node-wrap 装配(BlueprintRunner 反射 Attach UpstreamRefs)
        // ============================================================

        [Fact]
        public async Task BlueprintRunner_NodeWrap_AttachesUpstreamRefsAndFiresLoadAsyncOnStartLoading()
        {
            // node-wrap 模式:上游 DS 是一等节点(独立 DataSourceModel),composite 通过 UpstreamRefs 引用它们。
            // BlueprintRunner.BuildSchemaDeferred 装配后订阅就位,StartLoading 才 fire LoadAsync。
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "sh", TypeName = nameof(ShardA), DefaultContext = "a" },
                    new DataSourceModel { Id = "sz", TypeName = nameof(ShardB), DefaultContext = "b" },
                    new DataSourceModel
                    {
                        Id = "merged",
                        TypeName = nameof(UnionMergeDs),
                        UpstreamRefs = new List<string> { "sh", "sz" },
                    },
                },
            };

            var deferred = BlueprintRunner.BuildSchemaDeferred(bp);
            try
            {
                var leaf = (UnionMergeDs)BlueprintRunnerInternals.GetLeaf(deferred);

                // Phase-1:订阅就位,没数据
                await Task.Delay(50);
                Assert.Empty(leaf.MergeCalls);
                Assert.Equal(0, leaf.LogicalLength);

                // Phase-2:LoadAsync fire
                await BlueprintRunner.StartLoading(deferred);
                await WaitForAsync(() => leaf.MergeCalls.Count >= 2, TimeSpan.FromSeconds(3));
                Assert.Equal(3, leaf.LogicalLength);   // ShardA 2 行 + ShardB 1 行
                Assert.Contains(0, leaf.MergeCalls);
                Assert.Contains(1, leaf.MergeCalls);
            }
            finally { DisposeSchemaInternals(deferred.Schema); }
        }

        [Fact]
        public async Task BlueprintRunner_BuildSchemaSync_StillWorks_ForBackwardCompatCaller()
        {
            // BuildSchema 同步路径 = BuildSchemaDeferred + sync wait StartLoading,旧调用方零改动。
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "sh", TypeName = nameof(ShardA), DefaultContext = "a" },
                    new DataSourceModel { Id = "sz", TypeName = nameof(ShardB), DefaultContext = "b" },
                    new DataSourceModel
                    {
                        Id = "merged",
                        TypeName = nameof(UnionMergeDs),
                        UpstreamRefs = new List<string> { "sh", "sz" },
                    },
                },
            };

            var schema = BlueprintRunner.BuildSchema(bp);
            try
            {
                var leaf = (UnionMergeDs)GetLeafInstance(schema);
                await WaitForAsync(() => leaf.MergeCalls.Count >= 2, TimeSpan.FromSeconds(3));
                Assert.Equal(3, leaf.LogicalLength);
            }
            finally { DisposeSchemaInternals(schema); }
        }

        // ============================================================
        // §4 Composite<TItem> sentinel 蓝图(零业务类定义)
        // ============================================================

        [Fact]
        public async Task BlueprintRunner_BuiltinCompositeSentinel_NoBusinessClass()
        {
            TestRowMergeHandlers.InvokeCount = 0;
            var handlers = new BlueprintHandlerRegistry().AutoDiscoverStatic(typeof(TestRowMergeHandlers));

            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "sh", TypeName = nameof(ShardA), DefaultContext = "a" },
                    new DataSourceModel { Id = "sz", TypeName = nameof(ShardB), DefaultContext = "b" },
                    new DataSourceModel
                    {
                        Id = "merged",
                        TypeName = "Composite",
                        UpstreamRefs = new List<string> { "sh", "sz" },
                        Properties = new Dictionary<string, object?>
                        {
                            ["MergeHandler"] = "testrow_union_dedupe",
                        },
                    },
                },
            };

            var schema = BlueprintRunner.BuildSchema(bp, instances: null, handlers: handlers);
            try
            {
                var leaf = GetLeafInstance(schema);
                Assert.IsType<Composite<TestRow>>(leaf);

                await WaitForAsync(() => TestRowMergeHandlers.InvokeCount >= 2, TimeSpan.FromSeconds(3));

                var composite = (Composite<TestRow>)leaf;
                Assert.Equal(2, TestRowMergeHandlers.InvokeCount);
                Assert.Equal(3, composite.LogicalLength);
            }
            finally { DisposeSchemaInternals(schema); }
        }

        [Fact]
        public async Task BlueprintRunner_BuiltinComposite_DefaultReplaceBySlot_NoHandler()
        {
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "sh", TypeName = nameof(ShardA), DefaultContext = "a" },
                    new DataSourceModel { Id = "sz", TypeName = nameof(ShardB), DefaultContext = "b" },
                    new DataSourceModel
                    {
                        Id = "merged",
                        TypeName = "Composite",
                        UpstreamRefs = new List<string> { "sh", "sz" },
                    },
                },
            };

            var schema = BlueprintRunner.BuildSchema(bp);
            try
            {
                var composite = (Composite<TestRow>)GetLeafInstance(schema);
                await WaitForAsync(() => composite.LogicalLength >= 3, TimeSpan.FromSeconds(3));
                Assert.Equal(3, composite.LogicalLength);

                var snap = composite.GetSnapshot();
                var symbols = new List<string>();
                for (int i = 0; i < snap.Count; i++) symbols.Add(snap.Items[i].Symbol);
                Assert.Contains("A.a.1", symbols);
                Assert.Contains("A.a.2", symbols);
                Assert.Contains("B.b.1", symbols);
                Assert.True(symbols.IndexOf("A.a.1") < symbols.IndexOf("B.b.1"));
            }
            finally { DisposeSchemaInternals(schema); }
        }

        // ============================================================
        // §5 蓝图 JSON 反序列化(MergeMode "WhenAll" enum + UpstreamRefs string[])
        // ============================================================

        [Fact]
        public async Task BlueprintRunner_CompositeMergeMode_WhenAll_FromJsonString_SinglePublish()
        {
            // JSON 路径:UpstreamRefs 字符串数组 + Properties["MergeMode"] = "WhenAll"。
            // 验证 InjectProperties JsonElement→enum 拆包路径 + framework 反射 Attach 正确性。
            string json = $$"""
            {
                "DataSources": [
                    { "Id": "sh", "TypeName": "ShardA", "DefaultContext": "a" },
                    { "Id": "sz", "TypeName": "ShardB", "DefaultContext": "b" },
                    {
                        "Id": "merged",
                        "TypeName": "Composite",
                        "UpstreamRefs": ["sh", "sz"],
                        "Properties": { "MergeMode": "WhenAll" }
                    }
                ]
            }
            """;
            var bp = System.Text.Json.JsonSerializer.Deserialize<ChartBlueprint>(
                json, Hevo.Charting.LowCode.Designer.Converters.BlueprintJsonOptions.Default)!;

            var schema = BlueprintRunner.BuildSchema(bp);
            try
            {
                var composite = (Composite<TestRow>)GetLeafInstance(schema);
                Assert.Equal(MergeMode.WhenAll, composite.MergeMode);   // 枚举注入到位
                Assert.Equal(2, composite.UpstreamCount);               // 两路上游都 attached

                await WaitForAsync(() => composite.LogicalLength >= 3, TimeSpan.FromSeconds(3));
                Assert.Equal(3, composite.LogicalLength);
            }
            finally { DisposeSchemaInternals(schema); }
        }

        // ============================================================
        // §6 Deferred-load + FirstFrameReady
        // ============================================================

        [Fact]
        public async Task BlueprintRunner_StartLoading_Idempotent()
        {
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "sh", TypeName = nameof(ShardA), DefaultContext = "a" },
                },
            };
            var deferred = BlueprintRunner.BuildSchemaDeferred(bp);
            try
            {
                await BlueprintRunner.StartLoading(deferred);
                Assert.True(deferred.Started);
                var second = BlueprintRunner.StartLoading(deferred);
                Assert.Same(Task.CompletedTask, second);
            }
            finally { DisposeSchemaInternals(deferred.Schema); }
        }

        [Fact]
        public async Task DeferredSchema_FirstFrameReady_CompletesOnFirstLeafPublish()
        {
            // FirstFrameReady = framework 内部订阅 leaf Stream 首次 publish 信号,Task complete。
            // 业务侧拿这个 Task 等"首屏到了"再翻 IsLoading=false。
            var bp = new ChartBlueprint
            {
                DataSources =
                {
                    new DataSourceModel { Id = "sh", TypeName = nameof(ShardA), DefaultContext = "a" },
                },
            };
            var deferred = BlueprintRunner.BuildSchemaDeferred(bp);
            try
            {
                Assert.False(deferred.FirstFrameReady.IsCompleted);   // 没 LoadAsync 之前 leaf 没 publish

                await BlueprintRunner.StartLoading(deferred);
                await deferred.FirstFrameReady.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(deferred.FirstFrameReady.IsCompletedSuccessfully);
            }
            finally { DisposeSchemaInternals(deferred.Schema); }
        }

        // ============================================================
        // helpers
        // ============================================================

        // DynamicChartSchema<TItem> 反射 _dataSourceInstance(测试访问 private state,生产代码不该这样)。
        private static object GetLeafInstance(object schema)
        {
            var field = schema.GetType().GetField("_dataSourceInstance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException("DynamicChartSchema._dataSourceInstance 反射不到。");
            return field.GetValue(schema)!;
        }

        // 测试用反射强拆 _disposables,生产不该走。
        private static void DisposeSchemaInternals(object schema)
        {
            var field = typeof(Hevo.Charting.Core.ReactiveSchema).GetField(
                "_disposables", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(schema) is List<IDisposable> list)
            {
                foreach (var d in list) try { d?.Dispose(); } catch { }
                list.Clear();
            }
        }

        private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (predicate()) return;
                await Task.Delay(20);
            }
        }

        /// <summary>测试 helper:反射访问 DeferredSchema.LeafInstance(internal 字段)。</summary>
        private static class BlueprintRunnerInternals
        {
            public static object GetLeaf(DeferredSchema deferred)
            {
                var prop = typeof(DeferredSchema).GetProperty("LeafInstance",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?? throw new InvalidOperationException("DeferredSchema.LeafInstance 反射不到。");
                return prop.GetValue(deferred)!;
            }
        }
    }
}
