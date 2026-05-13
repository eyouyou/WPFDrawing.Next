using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Hevo.Charting.LowCode.Designer.UriRouting
{
    /// <summary>
    /// URI 模板填充。占位符 <c>{Path}</c> 走反射逐层 GetProperty / GetField,支持 <c>.</c> 嵌套 + <c>[X]</c> 索引。
    /// <para>
    /// 顶层根:
    /// </para>
    /// <list type="bullet">
    ///   <item>EventArgs 字段直接裸用:<c>{Index}</c> = EventArgs.Index、<c>{Element.Symbol}</c> = EventArgs.Element.Symbol</item>
    ///   <item>Context 里的额外 root 通过 <see cref="Fill(object, IReadOnlyDictionary{string, object?}?)"/> 注入,
    ///         典型:<c>{Items[Index].Symbol}</c> — Items 来自 DataSource snapshot context,Index 来自 EventArgs。</item>
    /// </list>
    /// <para>
    /// 索引语法 <c>[X]</c>:X 必须是已知 placeholder 名(EventArgs 字段或 context root key 之一),解析为 int 后做 IList 索引。
    /// 不支持 <c>[5]</c> 字面量索引(简单起见 + 蓝图层一般不需要),不支持嵌套 <c>[a[b]]</c>。
    /// </para>
    /// </summary>
    public sealed class UriTemplate
    {
        private static readonly Regex _placeholder = new(@"\{([^}]+)\}", RegexOptions.Compiled);

        private readonly string _template;
        // 每个槽位:在原模板的位置 + 长度 + 解析后的 PathSegment 序列
        private readonly IReadOnlyList<(int Start, int Length, PathSegment[] Path)> _slots;

        private UriTemplate(string template, IReadOnlyList<(int, int, PathSegment[])> slots)
        {
            _template = template;
            _slots    = slots;
        }

        public string Template => _template;

        public static UriTemplate Parse(string template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            var slots = new List<(int, int, PathSegment[])>();
            foreach (Match m in _placeholder.Matches(template))
            {
                var path = ParsePath(m.Groups[1].Value);
                if (path.Length == 0)
                    throw new FormatException($"UriTemplate '{template}' 含空占位符 {{}}");
                slots.Add((m.Index, m.Length, path));
            }
            return new UriTemplate(template, slots);
        }

        /// <summary>用 EventArgs 反射填充。</summary>
        public string Fill(object eventArgs) => Fill(eventArgs, null);

        /// <summary>
        /// 用 EventArgs + 额外 context root 反射填充。
        /// <paramref name="context"/> 提供"非 EventArgs 来源"的根(典型:DataSource.GetSnapshot().Items)。
        /// 同名 placeholder 优先 context,fallback EventArgs 字段。
        /// </summary>
        public string Fill(object eventArgs, IReadOnlyDictionary<string, object?>? context)
        {
            if (_slots.Count == 0) return _template;
            if (eventArgs == null) throw new ArgumentNullException(nameof(eventArgs));

            var sb = new StringBuilder(_template.Length + 16 * _slots.Count);
            int cursor = 0;
            foreach (var (start, length, path) in _slots)
            {
                sb.Append(_template, cursor, start - cursor);
                var value = ResolvePath(path, eventArgs, context);
                sb.Append(Uri.EscapeDataString(value?.ToString() ?? string.Empty));
                cursor = start + length;
            }
            sb.Append(_template, cursor, _template.Length - cursor);
            return sb.ToString();
        }

        // ── 路径解析 ─────────────────────────────────────────
        // PathSegment 表达 ".X"(普通属性 / 字段)或 "[X]"(索引,X 引用另一个 placeholder)
        private readonly struct PathSegment
        {
            public string Name { get; }   // 属性名 / 索引引用名
            public bool IsIndex { get; }  // true = [X],false = .X(或顶层根)
            public PathSegment(string name, bool isIndex) { Name = name; IsIndex = isIndex; }
        }

        // 解析 "Items[Index].Symbol" → [Items, [Index], Symbol]
        // 解析 "Element.Symbol"     → [Element, Symbol]
        // 解析 "Index"              → [Index]
        private static PathSegment[] ParsePath(string raw)
        {
            var result = new List<PathSegment>();
            int i = 0;
            int len = raw.Length;
            while (i < len)
            {
                if (raw[i] == '[')
                {
                    int close = raw.IndexOf(']', i + 1);
                    if (close < 0) throw new FormatException($"UriTemplate 占位符 '{raw}' 索引未闭合");
                    var indexName = raw.Substring(i + 1, close - i - 1).Trim();
                    if (indexName.Length == 0) throw new FormatException($"UriTemplate 占位符 '{raw}' 含空索引 []");
                    result.Add(new PathSegment(indexName, isIndex: true));
                    i = close + 1;
                    if (i < len && raw[i] == '.') i++;  // 跳过紧跟的 .
                }
                else
                {
                    int next = raw.IndexOfAny(new[] { '.', '[' }, i);
                    if (next < 0)
                    {
                        result.Add(new PathSegment(raw.Substring(i).Trim(), isIndex: false));
                        break;
                    }
                    var seg = raw.Substring(i, next - i).Trim();
                    if (seg.Length > 0) result.Add(new PathSegment(seg, isIndex: false));
                    if (raw[next] == '.') i = next + 1;
                    else i = next;  // [ 留给下次循环处理
                }
            }
            return result.ToArray();
        }

        private static object? ResolvePath(PathSegment[] path, object eventArgs, IReadOnlyDictionary<string, object?>? context)
        {
            object? current = null;
            for (int i = 0; i < path.Length; i++)
            {
                var seg = path[i];
                if (i == 0)
                {
                    // 第一段是根:优先 context,fallback EventArgs 字段
                    if (seg.IsIndex)
                        throw new FormatException("UriTemplate 占位符不能以 [X] 开头");
                    if (context != null && context.TryGetValue(seg.Name, out var ctxVal))
                        current = ctxVal;
                    else
                        current = ResolveMember(eventArgs, seg.Name);
                }
                else if (seg.IsIndex)
                {
                    // [X]:解析 X(从 context 或 EventArgs 拿 int),然后对当前对象做索引
                    if (current == null) return null;
                    object? indexValue;
                    if (context != null && context.TryGetValue(seg.Name, out var ctxVal))
                        indexValue = ctxVal;
                    else
                        indexValue = ResolveMember(eventArgs, seg.Name);

                    if (indexValue == null) return null;
                    int idx = Convert.ToInt32(indexValue);
                    current = ApplyIndex(current, idx);
                }
                else
                {
                    if (current == null) return null;
                    current = ResolveMember(current, seg.Name);
                }
            }
            return current;
        }

        private static object? ResolveMember(object root, string name)
        {
            var t = root.GetType();
            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null) return prop.GetValue(root);
            var field = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(root);
            throw new InvalidOperationException(
                $"UriTemplate 路径解析失败:类型 '{t.FullName}' 没有 public 属性 / 字段 '{name}'。");
        }

        private static object? ApplyIndex(object source, int index)
        {
            // IList(包括 T[] / List<T>)走索引器
            if (source is IList list)
            {
                if (index < 0 || index >= list.Count) return null;
                return list[index];
            }
            // 其他 enumerable / IReadOnlyList<T>:反射 this[int] indexer
            var t = source.GetType();
            var indexer = t.GetProperty("Item", new[] { typeof(int) });
            if (indexer != null) return indexer.GetValue(source, new object[] { index });
            throw new InvalidOperationException(
                $"UriTemplate 索引失败:类型 '{t.FullName}' 不支持 int 索引(没实现 IList 也没 this[int])。");
        }
    }
}
