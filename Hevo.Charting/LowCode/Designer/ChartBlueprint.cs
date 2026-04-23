using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

namespace Hevo.Charting.LowCode.Designer
{
    public class ChartBlueprint
    {
        public DataSourceModel? DataSource { get; set; }

        // 💥 淘汰 Layer 和 Sink！现在只有 Feature！
        public List<FeatureModel> Features { get; set; } = new();

        public List<StyleModel> InitialTraits { get; set; } = new();
    }

    public class DataSourceModel
    {
        public string TypeName { get; set; } = string.Empty;

        // 低代码配置：把数据源里的哪些字段，切片/映射到全局哪些引脚 ID 上
        public Dictionary<string, string> ScalarMappings { get; set; } = new();
        public Dictionary<string, string> VectorMappings { get; set; } = new();
    }

    public class StyleModel
    {
        public string TraitTypeName { get; set; } = string.Empty;
        public Dictionary<string, object?> Properties { get; set; } = new();
    }

    public class FeatureModel
    {
        public string TypeName { get; set; } = string.Empty;

        // 1. 普通属性配置 (如 LineColor, Period 等)
        public Dictionary<string, object?> Properties { get; set; } = new();

        // 2. 💥 引脚连线板：Key=Feature的属性名(如 "PricePort"), Value=全局引脚ID(如 "GlobalPrice")
        public Dictionary<string, string> PortBindings { get; set; } = new();
    }

    /// <summary>
    /// 💥 动态响应式图表骨架 (由 JSON 蓝图在运行时动态孵化)
    /// </summary>
    public class DynamicChartSchema<TItem> : ReactiveSchema
    {
        private readonly ChartBlueprint _blueprint;
        private readonly object _dataSourceInstance;
        private readonly IWorkflow<DataSnapshot<TItem>> _sourceStream;

        // 💥 全局引脚注册表：按 ID 缓存实例化的 DataPort<T>
        private readonly Dictionary<string, object> _portRegistry = new();

        public DynamicChartSchema(
            ChartBlueprint blueprint,
            object dataSourceInstance,
            IWorkflow<DataSnapshot<TItem>> sourceStream)
        {
            _blueprint = blueprint;
            _dataSourceInstance = dataSourceInstance;
            _sourceStream = sourceStream;
        }

        /// <summary>
        /// 💥 核心魔法：反射获取或创建强类型 DataPort<T>
        /// </summary>
        private object GetOrCreatePort(Type portGenericType, string portId)
        {
            if (!_portRegistry.TryGetValue(portId, out var port))
            {
                // 动态生成 DataPort<T> 类型并实例化！
                var portType = typeof(DataPort<>).MakeGenericType(portGenericType);
                port = Activator.CreateInstance(portType, portId)!;
                _portRegistry[portId] = port;
            }
            return port;
        }

        // ==========================================
        // 1. 动态编译数据流摄入管线
        // ==========================================
        protected override void DefineDataFlow(ChartCell chart)
        {
            // 在低代码/反射场景下，我们绕过 Fluent DSL，直接组装 UniversalDataPipe
            var pipe = new UniversalDataPipe<TItem>();
            var dsType = _dataSourceInstance.GetType();

            if (_blueprint.DataSource != null)
            {
                // 1. 挂载标量映射 (ContextIngestor)
                foreach (var kvp in _blueprint.DataSource.ScalarMappings)
                {
                    string propName = kvp.Key;   // DataSource 的属性名
                    string portId = kvp.Value;   // 全局引脚 ID

                    var propInfo = dsType.GetProperty(propName);
                    if (propInfo == null) continue;

                    var portInstance = GetOrCreatePort(propInfo.PropertyType, portId);

                    // 利用 SmartActivator 动态构建 ContextIngestor 并塞入 Pipe
                    var ingestorType = typeof(ContextIngestor<,,>).MakeGenericType(typeof(TItem), dsType, propInfo.PropertyType);

                    // 动态生成 selector 委托: (ds) => ds.Property
                    var param = System.Linq.Expressions.Expression.Parameter(dsType, "ds");
                    var getter = System.Linq.Expressions.Expression.Property(param, propInfo);
                    var selector = System.Linq.Expressions.Expression.Lambda(getter, param).Compile();

                    var ingestor = Activator.CreateInstance(ingestorType, portInstance, _dataSourceInstance, selector)!;
                    pipe.AddIngestor((IDataIngestor<TItem>)ingestor);
                }

                // 2. 挂载向量切片映射 (ScatterIngestor)
                // (此处可复用类似的反射逻辑动态生成 ScatterIngestor 并加入 pipe，逻辑与标量类似，略作精简展示)
            }

            // 💥 将组装好的反射管道与流绑定到图表生命周期
            _sourceStream.Select(items => pipe.Process(items)).BindTo(chart);
        }

        // ==========================================
        // 2. 动态装配声明式特征 (Features)
        // ==========================================
        protected override void DefineFeatures(IFeatureContext canvas)
        {
            // 1. 播种全局初始特质 (Seed)
            foreach (var traitDef in _blueprint.InitialTraits)
            {
                Type traitType = ComponentRegistry.Resolve(traitDef.TraitTypeName);
                var traitInstance = (IVisualTrait)SmartActivator.CreateAndInject(traitType, null, traitDef.Properties);
                canvas.Seed(traitInstance);
            }

            // 2. 💥 动态组装 Features
            foreach (var featureDef in _blueprint.Features)
            {
                Type featureType = ComponentRegistry.Resolve(featureDef.TypeName);
                var feature = (ChartFeature)Activator.CreateInstance(featureType)!;

                // 步骤 A：注入普通基本属性 (如 PaddingRatio = 0.05)
                SmartActivator.InjectProperties(feature, featureDef.Properties);

                // 步骤 B：执行引脚焊接 (Port Binding)
                foreach (var binding in featureDef.PortBindings)
                {
                    string propertyName = binding.Key; // 如 "PricePort"
                    string portId = binding.Value;     // 如 "GlobalPrice"

                    var propInfo = featureType.GetProperty(propertyName);
                    if (propInfo != null)
                    {
                        // 提取目标引脚所需的泛型类型：DataPort<T> 里的 T
                        Type portDataType = propInfo.PropertyType.GetGenericArguments()[0];

                        // 从注册表提取或创建真正的引脚实例
                        object portInstance = GetOrCreatePort(portDataType, portId);

                        // 💥 咔哒！将引脚插上 Feature！
                        propInfo.SetValue(feature, portInstance);
                    }
                }

                // 步骤 C：挂载到画布 (替换为最新标准 API: Add)
                canvas.Add(feature);
            }
        }
    }
}