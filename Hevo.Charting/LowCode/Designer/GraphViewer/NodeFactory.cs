using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
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

        /// <summary>判断一个类型属于 GraphViewer 哪种节点。</summary>
        public static Kind Classify(Type type)
        {
            if (typeof(ChartFeature).IsAssignableFrom(type)) return Kind.Feature;
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
            var typeName = type.Name;

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
                    outputs = scalars.Concat(vectors).ToArray();

                    // 把字段→portId 的初始映射放进 Properties,为 GraphSerializer 写出 ScalarMappings/VectorMappings 用。
                    // 默认每个 output 端口都自动派一个全局唯一 portId,用户拖线时直接使用。
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
        /// 数组型(<c>DataPort&lt;T&gt;[]</c>)目前直接跳过 —— 蓝图 PortBindings 是 Dictionary&lt;string,string&gt;,
        /// 不支持一对多焊接;典型例 <see cref="UniversalAutoScaleFeature.ValuePorts"/> 仍需业务侧 IPipelinePolicy 接管。
        /// </summary>
        private static (Port[] inputs, Port[] outputs) ScanFeaturePorts(Type featureType)
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
        /// 沿基类链找 DataSource&lt;TSource, TItem&gt;,返回 TItem。
        /// 找不到说明不是低代码可识别的数据源。
        /// </summary>
        public static Type? FindDataSourceItemType(Type type)
        {
            var t = type.BaseType;
            while (t != null && t != typeof(object))
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DataSource<,>))
                    return t.GetGenericArguments()[1];
                t = t.BaseType;
            }
            return null;
        }

        private static (List<Port> scalars, List<Port> vectors) ScanDataSourceOutputs(Type dsType)
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
            var itemType = FindDataSourceItemType(dsType);
            if (itemType != null)
            {
                foreach (var p in itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!IsScalarType(p.PropertyType)) continue;
                    if (!p.CanRead) continue;
                    var memoryName = $"ReadOnlyMemory<{PrettyTypeName(p.PropertyType)}>";
                    vectors.Add(new Port(
                        Id: p.Name,
                        Name: p.Name,
                        DataTypeName: memoryName,
                        IsInput: false));
                }
            }
            return (scalars, vectors);
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
        /// </summary>
        private static void SeedTraitDefaults(Type type, Dictionary<string, object?> props)
        {
            if (type.GetConstructor(Type.EmptyTypes) != null) return;
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
            object? defaultPreset =
                (type.GetField("Default", F)?.GetValue(null))
                ?? (type.GetProperty("Default", F)?.GetValue(null));
            if (defaultPreset == null)
            {
                if (HasStaticPreset(type, "Default")) props["Preset"] = "Default";
                return;
            }
            var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor == null) { props["Preset"] = "Default"; return; }
            foreach (var p in ctor.GetParameters())
            {
                if (string.IsNullOrEmpty(p.Name)) continue;
                var prop = type.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null) continue;
                props[p.Name] = prop.GetValue(defaultPreset);
            }
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
