using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 根据 ComponentRegistry 里登记的 .NET Type 反射出一个 GraphViewer 节点。
    /// 端口自动派生:
    ///   • Feature        → 扫 public DataPort&lt;T&gt; 属性 = Input 端口 (蓝图侧 PortBindings 的目标)
    ///   • DataSource     → 自身 scalar 属性 (string/数值/日期等) + TItem 的 Port-able 属性 = Output 端口
    ///   • IVisualTrait   → 无端口,Properties 默认带 Preset = "Default"(如 trait 类型上有此 static 字段)
    /// </summary>
    public static class NodeFactory
    {
        public enum Kind { DataSource, Trait, Feature, Unknown }

        // 💥 反射结果终身缓存:Type / PropertyInfo / Port (record) 都不可变,程序集生命周期内永不失效。
        // 编辑器里反复 CreateNode / ListByKind 的反射开销从 O(N×props) 收敛成首次扫描后 O(1)。
        // ConcurrentDictionary 防 picker 异步预览 + 主线程 CreateNode 同时撞上。
        private static readonly ConcurrentDictionary<Type, Kind> _classifyCache = new();
        private static readonly ConcurrentDictionary<Type, (Port[] inputs, Port[] outputs)> _featurePortCache = new();
        private static readonly ConcurrentDictionary<Type, (Port[] scalars, Port[] vectors)> _dataSourcePortCache = new();
        // Trait 默认 props 的不可变快照模板,CreateNode 内拷给目标 dict (调用方需要可写 dict 编辑节点)。
        private static readonly ConcurrentDictionary<Type, KeyValuePair<string, object?>[]> _traitDefaultsCache = new();

        /// <summary>
        /// 预热反射缓存。Bootstrap 阶段批量调一次,把 ComponentRegistry 里所有已知类型的 shape 一次性扫好,
        /// 业务首屏 CreateNode 跟 picker 滚动都不再触发反射。
        /// 重复调用安全(GetOrAdd 命中现有 entry 即跳过)。
        /// </summary>
        public static void PrewarmCache(IEnumerable<Type> types)
        {
            foreach (var t in types)
            {
                var kind = Classify(t);
                switch (kind)
                {
                    case Kind.Feature: ScanFeaturePorts(t); break;
                    case Kind.Trait: GetTraitDefaultsSnapshot(t); break;
                    case Kind.DataSource: ScanDataSourceOutputs(t); break;
                }
            }
        }

        /// <summary>判断一个类型属于 GraphViewer 哪种节点。</summary>
        public static Kind Classify(Type type)
            => _classifyCache.GetOrAdd(type, ClassifyCore);

        private static Kind ClassifyCore(Type type)
        {
            // 判定到 Feature 基类:GridLayoutFeature 等内置 feature 已从 ChartFeature 降级 Feature,
            // 蓝图编辑器仍按 Feature 节点显示。
            if (typeof(Feature).IsAssignableFrom(type)) return Kind.Feature;
            if (typeof(IVisualTrait).IsAssignableFrom(type)) return Kind.Trait;
            if (FindDataSourceItemType(type) != null) return Kind.DataSource;
            return Kind.Unknown;
        }

        /// <summary>
        /// 列出 ComponentRegistry 里某种 Kind 的所有类型 (字典序)。
        /// </summary>
        public static IReadOnlyList<Type> ListByKind(Kind kind)
        {
            var seen = new HashSet<Type>();
            var result = new List<Type>();
            foreach (var kv in ComponentRegistry.ListAll())
            {
                if (Classify(kv.Value) == kind && seen.Add(kv.Value)) result.Add(kv.Value);
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>把 .NET Type 物化为 GraphViewer 节点。位置由调用方决定。</summary>
        public static Node CreateNode(Type type, HevoPoint position)
        {
            var kind = Classify(type);

            // 蓝图 TypeName 优先取 [BlueprintTypeAlias],缺省回落 Type.Name。
            // 动机:开放泛型(Composite<TItem> 这种)反射 Type.Name = "Composite`1" 带反射格式后缀,
            // 协议层期望的是稳定字面字符串。alias 标在类型定义上,通用工厂层不再特判具体类型。
            var typeName = BlueprintTypeAlias.GetAlias(type) ?? type.Name;

            Port[] inputs = Array.Empty<Port>();
            Port[] outputs = Array.Empty<Port>();
            Dictionary<string, object?> props = new();

            switch (kind)
            {
                case Kind.Feature:
                    (inputs, outputs) = ScanFeaturePorts(type);
                    break;
                case Kind.Trait:
                    SeedTraitDefaults(type, props);
                    break;
                case Kind.DataSource:
                {
                    var (scalars, vectors) = ScanDataSourceOutputs(type);
                    var itemType = FindDataSourceItemType(type);

                    // 合成 sentinel "Stream" output port —— 给 Composite 之类的"上游 Stream 订阅"边 UX 用。
                    // 它不是真实字段端口(GraphSerializer 不写到 PortBindings),只参与画布连线。
                    // dataType=DataSnapshot<TItem>,跟 IWorkflow<DataSnapshot<TItem>>.Subscribe 接的 snapshot 对齐。
                    var portsBuilder = new List<Port>(scalars.Length + vectors.Length + 1);
                    portsBuilder.AddRange(scalars);
                    portsBuilder.AddRange(vectors);
                    if (itemType != null)
                    {
                        portsBuilder.Add(new Port(
                            Id: StreamPortId,
                            Name: StreamPortId,
                            DataTypeName: $"DataSnapshot<{PrettyTypeName(itemType)}>",
                            IsInput: false));
                    }
                    outputs = portsBuilder.ToArray();

                    // 合成 sentinel "Context" input port —— 给 Cascade 边(上游 DS.Stream → 本 DS.Context)
                    // 的视觉连线提供落脚。GraphSerializer 不写到 PortBindings,只在画布上参与连线。
                    // Composite<TItem>:在 Context 之外再追加一根 fan-in input port "Upstreams" —— 编辑器从 N 个
                    // 上游 DS.Stream 拖线进来,序列化时 GraphSerializer 翻译成 DataSourceModel.UpstreamRefs。
                    var inputsBuilder = new List<Port>(2)
                    {
                        new Port(
                            Id: ContextPortId,
                            Name: ContextPortId,
                            DataTypeName: "TContext",
                            IsInput: true),
                    };
                    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Hevo.Charting.WorkFlow.Composite<>))
                    {
                        var ti = type.GetGenericArguments()[0];
                        inputsBuilder.Add(new Port(
                            Id: UpstreamsPortId,
                            Name: UpstreamsPortId,
                            DataTypeName: $"DataSnapshot<{PrettyTypeName(ti)}>",
                            IsInput: true,
                            IsArray: true));
                    }
                    inputs = inputsBuilder.ToArray();

                    // 把字段→portId 的初始映射放进 Properties,为 GraphSerializer 写出 PortBindings 用。
                    // Stream 合成端口不进 mappings —— 没有真实字段对应。
                    var scalarMap = new Dictionary<string, string>();
                    foreach (var p in scalars) scalarMap[p.Id] = $"{type.Name}_{p.Id}";
                    var vectorMap = new Dictionary<string, string>();
                    foreach (var p in vectors) vectorMap[p.Id] = $"{type.Name}_{p.Id}";
                    props["ScalarMappings"] = scalarMap;
                    props["VectorMappings"] = vectorMap;
                    break;
                }
            }

            float headerH = 24f;
            int rows = Math.Max(inputs.Length, outputs.Length);
            float h = headerH + Math.Max(1, rows) * 22f + 12f;
            var size = new HevoPoint(200f, h);
            var graphKind = kind switch
            {
                Kind.DataSource => NodeKind.DataSource,
                Kind.Trait => NodeKind.Trait,
                Kind.Feature => NodeKind.Feature,
                _ => NodeKind.Feature,
            };
            var category = FeatureCategoryRegistry.Resolve(type);
            return new Node(
                Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                TypeName: typeName,
                Title: typeName,
                Kind: graphKind,
                Position: position,
                Size: size,
                InputPorts: inputs,
                OutputPorts: outputs,
                Properties: props,
                Category: category
            );
        }

        // ==========================================
        //  Viewport 节点 — schema 顶层 ViewportPorts 的可视化代理
        // ==========================================

        /// <summary>
        /// schema 顶层 Viewport 端口的 well-known DisplayName 集 (跟 ViewportPorts.cs 的 ctor 名字对齐)。
        /// GraphSerializer 把 edge 端点对应到这些 id,DynamicChartSchema 把这些 id 映射到 schema 实际的 Viewport 端口实例。
        /// </summary>
        public const string ViewportLogicalLengthId = "VP_LogicalLength";
        public const string ViewportUserRangeId     = "VP_UserRange";
        public const string ViewportActiveRangeId   = "VP_ActiveRange";

        /// <summary>
        /// DS 节点合成的"整条 Stream"输出端口名 —— 给 Composite Upstreams 这类"订阅上游整 snapshot" UX 用,
        /// 不对应真实字段。GraphSerializer 不把它写到 DataSourceModel.OutputBindings(那是字段映射,不放合成端口)。
        /// </summary>
        public const string StreamPortId = "Stream";

        /// <summary>
        /// Composite&lt;TItem&gt; 节点合成的 fan-in 输入端口名 —— 编辑器从 N 个上游 DS.Stream 拖线进来,
        /// GraphSerializer 把所有连进来的 edge 翻译成 <see cref="DataSourceModel.UpstreamRefs"/> 字符串数组。
        /// </summary>
        public const string UpstreamsPortId = "Upstreams";

        /// <summary>
        /// DS 节点合成的 Cascade 接入端口名 —— 上游 DS.Stream 通过 ContextDriver 投影成
        /// 下游 TContext 后,在画布上从上游 Stream → 本 Context 拉一根 <see cref="EdgeKind.Cascade"/> 边。
        /// 跟 <see cref="StreamPortId"/> 一样是纯视觉 sentinel,不对应任何 .NET 字段:GraphSerializer
        /// 不写它到 PortBindings,GraphLayers 渲染 Cascade 边时 FindPort 能找到本端口才能画线。
        /// </summary>
        public const string ContextPortId = "Context";

        /// <summary>
        /// 构造一个 Viewport 节点。它代表 schema 顶层的 <see cref="Hevo.Charting.Core.ViewportPorts"/>,
        /// 不对应任何 .NET 类型 —— 蓝图运行时通过 <see cref="DynamicChartSchema{TItem}"/> 把端口
        /// 重定向到 schema 实际持有的 ViewportPorts 实例。
        /// </summary>
        public static Node CreateViewportNode(HevoPoint position)
        {
            var inputs = new[]
            {
                new Port(Id: "LogicalLength", Name: "LogicalLength", DataTypeName: "int",       IsInput: true,
                         Description: "数据总规模 (bar 数)。从 DataSource.LogicalLength 写入,ViewportManager 据此 clamp。"),
                new Port(Id: "UserRange",     Name: "UserRange",     DataTypeName: "RealRange", IsInput: true,
                         Description: "用户意图范围 (Pan/Zoom 写入)。一般业务交互层自动写,蓝图侧很少手动接。"),
            };
            var outputs = new[]
            {
                new Port(Id: "ActiveRange",   Name: "ActiveRange",   DataTypeName: "RealRange", IsInput: false,
                         Description: "ViewportManager 钳制后的合法 range。X 轴 / 部分 series 读这一根。"),
                new Port(Id: "LogicalLength", Name: "LogicalLength", DataTypeName: "int",       IsInput: false,
                         Description: "Logical 长度的 readback 端,谁需要可读。"),
            };
            return new Node(
                Id: Guid.NewGuid().ToString("N").Substring(0, 8),
                TypeName: "Viewport",
                Title: "Viewport",
                Kind: NodeKind.Viewport,
                Position: position,
                Size: new HevoPoint(220f, 24f + 4 * 22f + 12f),
                InputPorts: inputs,
                OutputPorts: outputs,
                Properties: new(),
                Category: FeatureCategory.Environment);
        }

        /// <summary>
        /// 把 Viewport 节点的端口名映射到 well-known schema port id。
        /// </summary>
        public static string? ViewportWellKnownId(string portIdOnViewportNode) => portIdOnViewportNode switch
        {
            "LogicalLength" => ViewportLogicalLengthId,
            "UserRange"     => ViewportUserRangeId,
            "ActiveRange"   => ViewportActiveRangeId,
            _ => null,
        };

        // ==========================================
        //  反射工具
        // ==========================================
        /// <summary>
        /// 扫 Feature 上的 <see cref="DataPort{T}"/> 属性,按 <see cref="PortDirectionAttribute"/> 分流入/出。
        /// 不带标记的默认 Input(覆盖 95% 的 series feature 场景)。
        /// 数组型(<c>DataPort&lt;T&gt;[]</c>)由 PortBindings 数组形态焊接 (优化方案 §8):
        /// PortBindings value 接受 string / List&lt;string&gt; / 老 CSV,统一由 PortBindingValue 解析。
        /// 结果走 <see cref="_featurePortCache"/> 终身缓存,reflection 只跑一次。
        /// </summary>
        private static (Port[] inputs, Port[] outputs) ScanFeaturePorts(Type featureType)
            => _featurePortCache.GetOrAdd(featureType, ScanFeaturePortsCore);

        private static (Port[] inputs, Port[] outputs) ScanFeaturePortsCore(Type featureType)
        {
            var inputs = new List<Port>();
            var outputs = new List<Port>();
            foreach (var p in featureType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Type? dataType = null;
                bool isArray = false;

                // shape 1: DataPort<T>
                if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DataPort<>))
                {
                    dataType = p.PropertyType.GetGenericArguments()[0];
                }
                // shape 2: DataPort<T>[]  (扇入端口)
                else if (p.PropertyType.IsArray)
                {
                    var elem = p.PropertyType.GetElementType();
                    if (elem != null && elem.IsGenericType && elem.GetGenericTypeDefinition() == typeof(DataPort<>))
                    {
                        dataType = elem.GetGenericArguments()[0];
                        isArray = true;
                    }
                }
                // shape 3: Dictionary<string, DataPort<T>>(§D2.X 多输入指标)——
                //   默认 ScanFeaturePorts 不展开,因为字典 key 集合是运行时由 @register inputs 元数据决定。
                //   picker 创建节点时调 ExpandInputSlots(node, fieldName, names) 一次性补 N 个 child Port,
                //   Port.Id 形态 "Inputs.high" / "Inputs.low" 跟蓝图侧 nested PortBindings 对齐。
                else if (p.PropertyType.IsGenericType
                         && p.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    var dictArgs = p.PropertyType.GetGenericArguments();
                    if (dictArgs[0] == typeof(string)
                        && dictArgs[1].IsGenericType
                        && dictArgs[1].GetGenericTypeDefinition() == typeof(DataPort<>))
                    {
                        // skip — picker 走 ExpandInputSlots 单独处理。
                        continue;
                    }
                }
                if (dataType == null) continue;

                bool isInput = PortMetadataRegistry.ResolveDirection(featureType, p) == PortDirection.Input;
                var port = new Port(
                    Id: p.Name,
                    Name: p.Name,
                    DataTypeName: isArray ? $"{PrettyTypeName(dataType)}[]" : PrettyTypeName(dataType),
                    IsInput: isInput,
                    Description: PortMetadataRegistry.ResolveDescription(featureType, p),
                    IsArray: isArray);
                (isInput ? inputs : outputs).Add(port);
            }
            return (inputs.ToArray(), outputs.ToArray());
        }

        /// <summary>
        /// §D2.X 反射 Feature 类型上所有 <c>Dictionary&lt;string, DataPort&lt;T&gt;&gt;</c> 字段。
        /// 给 picker UX 用 —— 检测一个 Feature 是否支持多输入,以及它的 dict 字段名 + value 端口数据类型。
        /// 返回元组列表 <c>(字段名, 端口数据类型 T)</c>;非 dict 字段 / value 不是 <see cref="DataPort{T}"/> 的不返。
        /// </summary>
        public static IReadOnlyList<(string FieldName, Type PortDataType)> ListInputDictFields(Type featureType)
        {
            var result = new List<(string, Type)>();
            foreach (var pi in featureType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var pt = pi.PropertyType;
                if (!pt.IsGenericType || pt.GetGenericTypeDefinition() != typeof(Dictionary<,>)) continue;
                var args = pt.GetGenericArguments();
                if (args[0] != typeof(string)) continue;
                if (!args[1].IsGenericType || args[1].GetGenericTypeDefinition() != typeof(DataPort<>)) continue;
                result.Add((pi.Name, args[1].GetGenericArguments()[0]));
            }
            return result;
        }

        /// <summary>
        /// §D2.X 统一 handler 选择入口 —— picker / NodeEditor 选完 handler 后调用,
        /// 一次完成 Properties 注入 + Inputs 端口槽物化(单/多输入分支处理):
        ///
        /// <list type="number">
        ///   <item>把 <paramref name="handlerName"/> 写到 Feature 上首个 <see cref="Delegate"/> 类属性
        ///         (ComputeFeature.Compute / HandlerFeature.Handler / PlotFeature.Indicator)对应的
        ///         Properties 槽,加载时 ResolveHandlerReferences 翻译为实际委托。</item>
        ///   <item>PlotFeature 同步把 IndicatorName 也设成 <paramref name="handlerName"/>(驱动
        ///         IndicatorMetadataRegistry 解析 series specs)。</item>
        ///   <item>Feature 上有 <c>Dictionary&lt;string, DataPort&lt;T&gt;&gt;</c> 字段(多输入支持)时:
        ///         <list type="bullet">
        ///           <item><paramref name="inputNames"/> 非空 → <see cref="ResetInputSlots"/> 物化 N 个 child Port + InputOrder</item>
        ///           <item><paramref name="inputNames"/> null/空 → 摘掉所有 Inputs.* child Port + 清 InputOrder
        ///                  (用户从多输入 handler 切到单输入 handler 时的清理路径)</item>
        ///         </list>
        ///   </item>
        /// </list>
        ///
        /// <para>
        /// Feature 没有 Delegate 属性 → 静默返回原 node。Feature 有 Delegate 但无 dict 字段(纯单输入)→
        /// 只更新 Properties,不动 InputPorts。
        /// </para>
        /// </summary>
        public static Node ApplyHandlerSelection(Node node, Type featureType, string handlerName, IReadOnlyList<string>? inputNames)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (featureType == null) throw new ArgumentNullException(nameof(featureType));
            if (string.IsNullOrEmpty(handlerName)) throw new ArgumentNullException(nameof(handlerName));

            var newProps = new Dictionary<string, object?>(node.Properties);

            // 1. Delegate 属性(只取首个 —— 三个 Feature 各自只有一个 Delegate 属性)
            var delegateProp = featureType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => typeof(Delegate).IsAssignableFrom(p.PropertyType));
            if (delegateProp != null) newProps[delegateProp.Name] = handlerName;

            // 2. PlotFeature.IndicatorName(string)同步
            var indicatorNameProp = featureType.GetProperty("IndicatorName",
                BindingFlags.Public | BindingFlags.Instance);
            if (indicatorNameProp != null && indicatorNameProp.PropertyType == typeof(string))
                newProps["IndicatorName"] = handlerName;

            var dictFields = ListInputDictFields(featureType);
            if (dictFields.Count == 0)
            {
                // 纯单输入 Feature(目前没有,但反射兜底)
                return node with { Properties = newProps };
            }

            var (fieldName, portDataType) = dictFields[0];

            if (inputNames == null || inputNames.Count == 0)
            {
                // 单输入 handler 选到含 Inputs dict 的 Feature(典型:用户编辑器里把 atr_14 切成 ma_close_20)
                // 摘掉所有 "{fieldName}.*" child input ports + 清 InputOrder。剩下的常规 InputPort 单字段保留。
                var prefix = fieldName + ".";
                var keptInputs = node.InputPorts.Where(p => !p.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                newProps.Remove("InputOrder");
                int rows = Math.Max(keptInputs.Length, node.OutputPorts.Count);
                float h = 24f + Math.Max(1, rows) * 22f + 12f;
                return node with
                {
                    InputPorts = keptInputs,
                    Properties = newProps,
                    Size = new HevoPoint(node.Size.X, h),
                };
            }

            // 多输入 handler:Properties 先合并,再走 ResetInputSlots(InputPorts/InputOrder/Size 一并刷)
            var withProps = node with { Properties = newProps };
            return ResetInputSlots(withProps, fieldName, inputNames, portDataType);
        }

        /// <summary>
        /// §D2.X 多输入指标 NodeEditor 重新配置入口:换一组 input names。
        /// <para>
        /// 1. 摘掉 <paramref name="node"/>.InputPorts 里所有 <c>Id 以 "{fieldName}." 开头</c>的 child port
        /// 2. <see cref="ExpandInputSlots"/> 用新的 <paramref name="newInputNames"/> 展开
        /// 3. Properties.InputOrder 同步更新
        /// </para>
        /// <para>
        /// <b>注意</b>:被摘掉的端口对应的 Edge 由调用方负责清理(典型是 NodeEditorWindow 的宿主 ——
        /// LowCodeDemoView.OnNodeEditRequested / DashboardWorkspace.OpenEditor —— 在 Apply 时按
        /// "ToPortId 不在新 InputPorts 集合里" 过滤掉残边)。NodeFactory 不持有 GraphState 全图,这是分层。
        /// </para>
        /// </summary>
        public static Node ResetInputSlots(Node node, string dictFieldName, IReadOnlyList<string> newInputNames, Type portDataType)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(dictFieldName)) throw new ArgumentNullException(nameof(dictFieldName));
            if (newInputNames == null) throw new ArgumentNullException(nameof(newInputNames));

            var prefix = dictFieldName + ".";
            var keptInputs = node.InputPorts
                .Where(p => !p.Id.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();

            var props = new Dictionary<string, object?>(node.Properties);
            props.Remove("InputOrder");   // 清掉旧 order;ExpandInputSlots 会按 newInputNames 重填

            var stripped = node with
            {
                InputPorts = keptInputs,
                Properties = props,
            };
            return ExpandInputSlots(stripped, dictFieldName, newInputNames, portDataType);
        }

        /// <summary>
        /// §D2.X 多输入指标:把 Feature 上某 <c>Dictionary&lt;string, DataPort&lt;T&gt;&gt;</c> 字段
        /// 展开为 N 个 child Port(<c>Port.Id = "{dictFieldName}.{name}"</c>),追加到 <paramref name="node"/>.InputPorts。
        ///
        /// <para>
        /// 典型用法:LowCodeDemo 的 picker 加 ComputeFeature(关联 Python 多输入指标 'atr_14' /
        /// inputs=['high','low','close'])后调:
        /// </para>
        /// <code>
        /// var node = NodeFactory.CreateNode(typeof(ComputeFeature), pos);
        /// node = NodeFactory.ExpandInputSlots(node, "Inputs",
        ///                                     new[] { "high", "low", "close" },
        ///                                     typeof(ReadOnlyMemory&lt;double&gt;));
        /// </code>
        ///
        /// <para>
        /// Properties 字典里一并写 <c>InputOrder</c>,Feature 实例化时按声明顺序调 Compute 委托。
        /// </para>
        /// </summary>
        public static Node ExpandInputSlots(Node node, string dictFieldName, IReadOnlyList<string> inputNames, Type portDataType)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(dictFieldName)) throw new ArgumentNullException(nameof(dictFieldName));
            if (inputNames == null) throw new ArgumentNullException(nameof(inputNames));

            var typeName = PrettyTypeName(portDataType);
            var newInputs = new List<Port>(node.InputPorts);
            var existingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.InputPorts) existingIds.Add(p.Id);

            foreach (var name in inputNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var portId = $"{dictFieldName}.{name}";
                if (!existingIds.Add(portId)) continue;   // 重复展开幂等
                newInputs.Add(new Port(
                    Id: portId,
                    Name: name,
                    DataTypeName: typeName,
                    IsInput: true,
                    Description: $"{dictFieldName} 字典槽 — {name}"));
            }

            // 同步 InputOrder 到 Properties(Feature 加载时按此调 Compute 委托)。
            // 字段已存在的 InputOrder 优先保留(用户改过),否则按 inputNames 默认填。
            var props = new Dictionary<string, object?>(node.Properties);
            if (!props.ContainsKey("InputOrder"))
                props["InputOrder"] = inputNames.ToArray();

            // 重新算 Size 高度容下新 ports
            int rows = Math.Max(newInputs.Count, node.OutputPorts.Count);
            float h = 24f + Math.Max(1, rows) * 22f + 12f;
            var newSize = new HevoPoint(node.Size.X, h);

            return node with
            {
                InputPorts = newInputs.ToArray(),
                Properties = props,
                Size = newSize,
            };
        }

        /// <summary>
        /// 跟 <see cref="ExpandInputSlots"/> 对称的输出方向版本 —— 给标了
        /// <c>[PortDirection(PortDirection.Output)]</c> 的 <c>Dictionary&lt;string, DataPort&lt;T&gt;&gt;</c> 字段
        /// (典型:<c>PlotFeature.SeriesOutputs</c>)展开 N 个 child 输出 Port。
        ///
        /// <para>
        /// 跟 Input 版本的差异:① <c>IsInput = false</c>;② 不写 <c>Properties["InputOrder"]</c>
        /// (那是给 multi-input 调 Compute 委托用的形参顺序,跟输出 fan-out 无关)。
        /// </para>
        /// </summary>
        public static Node ExpandOutputSlots(Node node, string dictFieldName, IReadOnlyList<string> outputNames, Type portDataType)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(dictFieldName)) throw new ArgumentNullException(nameof(dictFieldName));
            if (outputNames == null) throw new ArgumentNullException(nameof(outputNames));

            var typeName = PrettyTypeName(portDataType);
            var newOutputs = new List<Port>(node.OutputPorts);
            var existingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.OutputPorts) existingIds.Add(p.Id);

            foreach (var name in outputNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var portId = $"{dictFieldName}.{name}";
                if (!existingIds.Add(portId)) continue;
                newOutputs.Add(new Port(
                    Id: portId,
                    Name: name,
                    DataTypeName: typeName,
                    IsInput: false,
                    Description: $"{dictFieldName} 字典槽 — {name}"));
            }

            int rows = Math.Max(node.InputPorts.Count, newOutputs.Count);
            float h = 24f + Math.Max(1, rows) * 22f + 12f;
            var newSize = new HevoPoint(node.Size.X, h);

            return node with
            {
                OutputPorts = newOutputs.ToArray(),
                Size = newSize,
            };
        }

        /// <summary>
        /// 沿基类链找 DataSource&lt;TSource, TItem&gt;,返回 TItem。
        /// 找不到说明不是低代码可识别的数据源。
        /// </summary>
        public static Type? FindDataSourceItemType(Type type)
        {
            // 优先匹配 BufferedDataSource<TSource, TItem> —— 99% 业务 DS 都派生自它,
            // 第 2 泛型参数即业务 TItem(行 POCO,Times/Opens/... 这种行字段反射成 vector 端口)。
            // 不优先匹配 DataSource<,> 是因为 BufferedDataSource 继承的是 DataSource<TSource, DataSnapshot<TItem>>,
            // T = DataSnapshot<TItem> 不是业务行类型,反射 DataSnapshot 自家字段(Items/Count 这些)的 vector 端口
            // 没业务意义,蓝图里 VectorMappings["Value"] 之类的也对不上。
            var t = type.BaseType;
            while (t != null && t != typeof(object))
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(BufferedDataSource<,>))
                    return t.GetGenericArguments()[1];
                t = t.BaseType;
            }
            // 兜底:直接派生 DataSource<TSource, T>(不走 BufferedDataSource)的小众场景,
            // 把 T 当 TItem 用(如果它本身是 DataSnapshot<U> 就解一层壳,否则原样返回)。
            t = type.BaseType;
            while (t != null && t != typeof(object))
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DataSource<,>))
                {
                    var arg = t.GetGenericArguments()[1];
                    if (arg.IsGenericType && arg.GetGenericTypeDefinition() == typeof(DataSnapshot<>))
                        return arg.GetGenericArguments()[0];
                    return arg;
                }
                t = t.BaseType;
            }
            return null;
        }

        private static (Port[] scalars, Port[] vectors) ScanDataSourceOutputs(Type dsType)
            => _dataSourcePortCache.GetOrAdd(dsType, ScanDataSourceOutputsCore);

        private static (Port[] scalars, Port[] vectors) ScanDataSourceOutputsCore(Type dsType)
        {
            var scalars = new List<Port>();
            var vectors = new List<Port>();

            // 1. 自身的标量属性 (基本类型 / 字符串 / DateTime)
            foreach (var p in dsType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!IsScalarType(p.PropertyType)) continue;
                if (!p.CanRead) continue;
                scalars.Add(new Port(
                    Id: p.Name,
                    Name: p.Name,
                    DataTypeName: PrettyTypeName(p.PropertyType),
                    IsInput: false));
            }

            // 2. TItem 的字段做向量输出 (列流: ReadOnlyMemory<TValue>)
            //    两种 TItem 形态都识别:
            //    ① 行式 POCO(TestDataSource 这类):字段是标量 double/DateTime,pipeline 装配期按列累积成 ROM<X> port。
            //    ② SoA 块式(KLineDataBlock 这类):字段直接是 ReadOnlyMemory<X>,custom pipeline policy 装配期
            //       原样塞 ROM<X> port。NodeFactory 反射时把内部元素类型 X 抽出来,vector port 的 DataTypeName
            //       跟形态 ① 对齐(都是 ROM<X>),GraphDeserializer 才能把 globalId 反查回 DS 节点的 OutputPort。
            //    没识别的话:KLine 蓝图反序列化时 "Closes" 这类 port 缺失 → globalIdToOutput 漏注册 →
            //    UniversalAutoScaleFeature.ValuePorts 边丢失 → 运行期 OnCompose listenPorts.AddRange(null) 抛
            //    ArgumentNullException(collection)。
            var itemType = FindDataSourceItemType(dsType);
            if (itemType != null)
            {
                foreach (var p in itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead) continue;
                    Type? valueType = null;
                    if (IsScalarType(p.PropertyType))
                    {
                        valueType = p.PropertyType;
                    }
                    else if (p.PropertyType.IsGenericType
                             && p.PropertyType.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>))
                    {
                        var inner = p.PropertyType.GetGenericArguments()[0];
                        if (IsScalarType(inner)) valueType = inner;
                    }
                    if (valueType == null) continue;
                    var memoryName = $"ReadOnlyMemory<{PrettyTypeName(valueType)}>";
                    vectors.Add(new Port(
                        Id: p.Name,
                        Name: p.Name,
                        DataTypeName: memoryName,
                        IsInput: false));
                }
            }
            return (scalars.ToArray(), vectors.ToArray());
        }

        private static bool IsScalarType(Type t)
        {
            if (t == typeof(string)) return true;
            if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan)) return true;
            if (t.IsPrimitive) return true;       // bool/byte/short/int/long/float/double/char etc.
            if (t == typeof(decimal)) return true;
            if (Nullable.GetUnderlyingType(t) is { } u) return IsScalarType(u);
            return false;
        }

        private static bool HasStaticPreset(Type type, string name)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
            return type.GetField(name, F) != null || type.GetProperty(name, F) != null;
        }

        /// <summary>
        /// 为新建的 Trait 节点预填初始 Properties:
        /// 1. 类型有 public 无参 ctor → 留空,SmartActivator 走 InjectProperties 路径用 init 默认值。
        /// 2. positional record 这种只有主构造的(典型 ScaleStrategyTrait(IScale, IScale)),
        ///    若类上有 "Default" 静态预设,把预设实例的 ctor 参数值拆出来塞 Properties,
        ///    让编辑器一打开就有可改的初始值,运行时走 ctor 注入而不是 Preset 路径。
        /// 3. 既无无参 ctor 也无 Default → 退而求其次塞 Preset 字符串(老路径,保留兼容)。
        ///
        /// 走 <see cref="_traitDefaultsCache"/> 缓存:同 type 反射只跑一次,后续 CreateNode 直接拷快照。
        /// 浅拷贝足够 —— 模板里的 value 都是 immutable 标量(string / Color / 数值),节点之间 sharing 也安全。
        /// </summary>
        private static void SeedTraitDefaults(Type type, Dictionary<string, object?> props)
        {
            var snapshot = GetTraitDefaultsSnapshot(type);
            for (int i = 0; i < snapshot.Length; i++)
                props[snapshot[i].Key] = snapshot[i].Value;
        }

        private static KeyValuePair<string, object?>[] GetTraitDefaultsSnapshot(Type type)
            => _traitDefaultsCache.GetOrAdd(type, BuildTraitDefaultsSnapshot);

        private static KeyValuePair<string, object?>[] BuildTraitDefaultsSnapshot(Type type)
        {
            if (type.GetConstructor(Type.EmptyTypes) != null) return Array.Empty<KeyValuePair<string, object?>>();
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
            object? defaultPreset =
                (type.GetField("Default", F)?.GetValue(null))
                ?? (type.GetProperty("Default", F)?.GetValue(null));
            if (defaultPreset == null)
            {
                if (HasStaticPreset(type, "Default"))
                    return new[] { new KeyValuePair<string, object?>("Preset", "Default") };
                return Array.Empty<KeyValuePair<string, object?>>();
            }
            var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor == null) return new[] { new KeyValuePair<string, object?>("Preset", "Default") };

            var list = new List<KeyValuePair<string, object?>>();
            foreach (var p in ctor.GetParameters())
            {
                if (string.IsNullOrEmpty(p.Name)) continue;
                var prop = type.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null) continue;
                list.Add(new KeyValuePair<string, object?>(p.Name, prop.GetValue(defaultPreset)));
            }
            return list.ToArray();
        }

        // C# 关键字别名表 —— 让端口的 DataTypeName 更"自然"(int / double 而非 Int32 / Double)。
        // 用 C# 关键字写的 Viewport 节点 port (DataTypeName: "int") 跟反射派出的标量端口能直接通过 TryWire 类型校验。
        private static readonly Dictionary<Type, string> _csAliases = new()
        {
            { typeof(int),     "int"     }, { typeof(uint),    "uint"   },
            { typeof(long),    "long"    }, { typeof(ulong),   "ulong"  },
            { typeof(short),   "short"   }, { typeof(ushort),  "ushort" },
            { typeof(byte),    "byte"    }, { typeof(sbyte),   "sbyte"  },
            { typeof(float),   "float"   }, { typeof(double),  "double" },
            { typeof(decimal), "decimal" }, { typeof(bool),    "bool"   },
            { typeof(char),    "char"    }, { typeof(string),  "string" },
            { typeof(object),  "object"  }, { typeof(void),    "void"   },
        };

        public static string PrettyTypeName(Type t)
        {
            if (Nullable.GetUnderlyingType(t) is { } u) return PrettyTypeName(u) + "?";
            if (_csAliases.TryGetValue(t, out var alias)) return alias;
            if (!t.IsGenericType) return t.Name;
            var name = t.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            var args = string.Join(", ", t.GetGenericArguments().Select(PrettyTypeName));
            return $"{name}<{args}>";
        }
    }
}
