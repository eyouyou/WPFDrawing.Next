using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hevo.Charting.LowCode.Designer.Converters
{
    /// <summary>
    /// 蓝图序列化的"标准答案"配置入口。所有蓝图导入/导出路径都应该用这个。
    /// <para>
    /// 不要每个调用点 new 一份 JsonSerializerOptions —— 蓝图一致性靠"全用同一份 options",
    /// 才能保证 Color / IHevoBrush / NaN 等坑点全程对齐(详见低代码.md §8.10 NaN/Infinity fix 同款思路)。
    /// </para>
    /// <para>
    /// 业务侧若需扩展自家 trait 类型的转换器,clone 一份再 Add Converter,不要在共享 Default 上原地改。
    /// </para>
    /// </summary>
    public static class BlueprintJsonOptions
    {
        /// <summary>
        /// 蓝图标准 options:
        /// <list type="bullet">
        /// <item>WriteIndented = true(蓝图是给人看 / diff 评审的)</item>
        /// <item>NumberHandling = AllowNamedFloatingPointLiterals(NaN / Infinity 写成字符串,见低代码.md §8.10)</item>
        /// <item>Color → "#RRGGBB"</item>
        /// <item>IHevoBrush → kind 判别符多态</item>
        /// <item>IHevoString → kind 判别符多态(literal / resource)</item>
        /// <item>IBrushResolver&lt;double&gt; → kind 判别符多态(static / threshold)+ legacy ConstantBrush 兼容</item>
        /// </list>
        /// </summary>
        public static readonly JsonSerializerOptions Default = BuildDefault();

        private static JsonSerializerOptions BuildDefault()
        {
            var o = new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                // PascalCase JSON 字段 ↔ camelCase ctor 参数(typical C# 命名约定)。
                // 不设这个,STJ 默认 case-sensitive ctor 参数匹配,RealRange(double min, double max)
                // 配不上 JSON {"Min", "Max"},反序列化失败 → struct 默认值 → IsValid=false。
                // 蓝图序列化是启动/保存的低频路径,case-insensitive 性能损失可忽略。
                PropertyNameCaseInsensitive = true,
            };
            o.Converters.Add(new ColorJsonConverter());
            o.Converters.Add(new HevoBrushJsonConverter());
            o.Converters.Add(new HevoStringJsonConverter());
            o.Converters.Add(new BrushResolverJsonConverter());
            o.Converters.Add(new ZoomStrategyJsonConverter());
            // §16 O3 曾尝试加 JsonStringEnumConverter 让 enum 写字符串,
            // 但全局 enum converter 在跟 SmartActivator.CoerceValue 兜底链 + IScale 接口路径
            // 互动时不稳(IScale 反序列化 NotSupportedException 在某些路径泄漏),
            // 撤回。后续如需 enum string 序列化,改成 per-converter 而非全局。
            // (CoerceValue 重构后,enum-string 注入已经稳定,见 SmartActivatorPresetTests §CoerceValue。)
            return o;
        }
    }
}
