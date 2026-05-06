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
        /// </list>
        /// </summary>
        public static readonly JsonSerializerOptions Default = BuildDefault();

        private static JsonSerializerOptions BuildDefault()
        {
            var o = new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            };
            o.Converters.Add(new ColorJsonConverter());
            o.Converters.Add(new HevoBrushJsonConverter());
            return o;
        }
    }
}
