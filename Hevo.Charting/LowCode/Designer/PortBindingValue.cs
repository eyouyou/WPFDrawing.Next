using System.Text.Json;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// <see cref="FeatureModel.PortBindings"/> 值的解释器。
    /// <para>
    /// PortBindings 字典的 value 类型是 <c>object?</c>,接受三种形态:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>新格式 (推荐)</b>:JSON 数组 <c>["id1","id2"]</c> / 任何 <c>IEnumerable&lt;string&gt;</c>。
    ///         扇入端口 (<see cref="DataPort{T}"/><c>[]</c>) 优先用这种,可读性 + diff 友好。</item>
    ///   <item><b>新格式 单端口</b>:JSON 字符串 <c>"id1"</c>。普通 <see cref="DataPort{T}"/> 端口用。</item>
    ///   <item><b>旧格式 (兼容)</b>:CSV 字符串 <c>"id1,id2,id3"</c>。&lt;v1 蓝图扇入端口的写法,
    ///         入口照单全收,但 <see cref="GraphSerializer"/> 写出时统一升级到新格式数组。</item>
    /// </list>
    /// <para>
    /// 反序列化时 System.Text.Json 把 <c>object?</c> 解成 <see cref="JsonElement"/>,所以两个 Extract 方法
    /// 都要能识别 JsonElement 的两个 ValueKind (Array / String) + 直接传入的 string / List。
    /// </para>
    /// </summary>
    internal static class PortBindingValue
    {
        /// <summary>提取单端口 globalId,trim 后返回。读不到时返回空字符串。</summary>
        public static string ExtractSingle(object? raw)
        {
            if (raw is string s) return s.Trim();
            if (raw is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.String) return je.GetString()?.Trim() ?? string.Empty;
                if (je.ValueKind == JsonValueKind.Array)
                {
                    // 单端口字段配了数组 —— 取最后一个(GraphSerializer 老逻辑也是 ids.LastOrDefault)。
                    string last = string.Empty;
                    foreach (var el in je.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String) last = el.GetString()?.Trim() ?? last;
                    }
                    return last;
                }
            }
            return raw?.ToString()?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// 提取扇入端口 globalId 列表。空字符串过滤掉,trim。
        /// 识别:JSON 数组 / IEnumerable&lt;string&gt; / 旧 CSV / 单字符串(降级为单元素数组)。
        /// </summary>
        public static IReadOnlyList<string> ExtractList(object? raw)
        {
            if (raw == null) return Array.Empty<string>();

            if (raw is string s) return SplitCsvOrSingle(s);

            if (raw is IReadOnlyList<string> rls) return rls.Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
            if (raw is IEnumerable<string> seq)  return seq.Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

            if (raw is IEnumerable<object?> oseq)
                return oseq.Select(ExtractSingle).Where(t => t.Length > 0).ToArray();

            if (raw is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var el in je.EnumerateArray())
                    {
                        var v = ExtractSingle(el);
                        if (v.Length > 0) list.Add(v);
                    }
                    return list;
                }
                if (je.ValueKind == JsonValueKind.String)
                    return SplitCsvOrSingle(je.GetString() ?? string.Empty);
            }

            return Array.Empty<string>();
        }

        private static string[] SplitCsvOrSingle(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<string>();
            if (s.IndexOf(',') < 0)
            {
                var t = s.Trim();
                return t.Length > 0 ? new[] { t } : Array.Empty<string>();
            }
            // 旧 CSV 格式:"id1,id2,id3"
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        }
    }
}
