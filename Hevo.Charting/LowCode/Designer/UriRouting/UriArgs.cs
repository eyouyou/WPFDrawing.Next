using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hevo.Charting.LowCode.Designer.UriRouting
{
    /// <summary>
    /// URI dispatch 时传给 handler 的参数访问器。
    /// <para>
    /// 蓝图节点 <c>Events: {"ElementDoubleClicked": "hevo://open/kline_detail?index={Index}&amp;sym={Element.Symbol}"}</c>
    /// 触发时,framework 反射 EventArgs 填充模板,把 query string 解析成 (key, value) 对装进本类。
    /// 业务侧 handler 用 <see cref="GetInt"/> / <see cref="GetString"/> 等取参数,自带类型转换。
    /// </para>
    /// </summary>
    public sealed class UriArgs
    {
        private readonly IReadOnlyDictionary<string, string> _values;
        // services:framework dispatch 时按类型注入的强类型对象引用(典型:DataSource 实例 / BlueprintCanvas 等),
        // 业务 handler 通过 GetService<T> / RequireService<T> 拿强类型引用,
        // 不必把复杂业务对象序列化成 string 走 URI query 再反序列化。
        // 类比 ASP.NET Core MVC:_values 是 [FromQuery],_services 是 [FromServices](DI)。
        private readonly IReadOnlyDictionary<Type, object>? _services;

        /// <summary>来源 URI(已填充模板),业务侧 debug / log 用。</summary>
        public string SourceUri { get; }

        public UriArgs(string sourceUri, IReadOnlyDictionary<string, string> values)
            : this(sourceUri, values, services: null) { }

        public UriArgs(string sourceUri, IReadOnlyDictionary<string, string> values,
                       IReadOnlyDictionary<Type, object>? services)
        {
            SourceUri = sourceUri ?? throw new ArgumentNullException(nameof(sourceUri));
            _values   = values    ?? throw new ArgumentNullException(nameof(values));
            _services = services;
        }

        /// <summary>原始字符串值。key 不存在返回 null。</summary>
        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

        /// <summary>已解析参数集合(只读)。</summary>
        public IReadOnlyDictionary<string, string> All => _values;

        /// <summary>转 int。key 不存在 / 解析失败抛 FormatException(带原始值方便诊断)。</summary>
        public int GetInt(string key)
        {
            var raw = Get(key);
            if (raw == null) throw new FormatException(
                $"UriArgs['{key}'] 不存在(SourceUri='{SourceUri}')");
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            throw new FormatException($"UriArgs['{key}']='{raw}' 不是合法 int(SourceUri='{SourceUri}')");
        }

        public double GetDouble(string key)
        {
            var raw = Get(key);
            if (raw == null) throw new FormatException(
                $"UriArgs['{key}'] 不存在(SourceUri='{SourceUri}')");
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            throw new FormatException($"UriArgs['{key}']='{raw}' 不是合法 double(SourceUri='{SourceUri}')");
        }

        public bool GetBool(string key)
        {
            var raw = Get(key);
            if (raw == null) throw new FormatException(
                $"UriArgs['{key}'] 不存在(SourceUri='{SourceUri}')");
            if (bool.TryParse(raw, out var v)) return v;
            throw new FormatException($"UriArgs['{key}']='{raw}' 不是合法 bool(SourceUri='{SourceUri}')");
        }

        public bool TryGetInt(string key, out int value)
        {
            value = 0;
            var raw = Get(key);
            return raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public bool TryGetDouble(string key, out double value)
        {
            value = 0;
            var raw = Get(key);
            return raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // ── Service injection(类比 ASP.NET Core DI)────────────────────────

        /// <summary>
        /// 按类型反查 framework 注入的 service。返 null 表示未注册(或类型不匹配)。
        /// 用作可选依赖的访问;必需依赖请用 <see cref="RequireService{T}"/>。
        /// </summary>
        public T? GetService<T>() where T : class
        {
            if (_services == null) return null;
            // 精确类型命中
            if (_services.TryGetValue(typeof(T), out var exact) && exact is T ex) return ex;
            // 兜底:遍历找可 cast 的 entry(支持基类 / 接口反查)
            foreach (var kv in _services)
            {
                if (kv.Value is T match) return match;
            }
            return null;
        }

        /// <summary>
        /// 按类型反查 framework 注入的 service,缺失抛 <see cref="InvalidOperationException"/>。
        /// 业务 handler 强制依赖 DataSource 等服务时用此 API。
        /// </summary>
        public T RequireService<T>() where T : class
        {
            var v = GetService<T>();
            if (v != null) return v;
            throw new InvalidOperationException(
                $"UriArgs.RequireService<{typeof(T).Name}>():未注册或类型不匹配。" +
                $"SourceUri='{SourceUri}'。检查 framework 是否在 dispatch 时把对应 service 注入了 services dict。");
        }

        /// <summary>
        /// 从 URI 字符串解析。形态:<c>scheme://authority/path?key1=v1&amp;key2=v2</c>。
        /// 返回 (routeKey, args),routeKey = scheme + authority + path(无 query),用作 handler 注册表 key。
        /// </summary>
        public static (string RouteKey, UriArgs Args) Parse(string uri)
            => Parse(uri, services: null);

        /// <summary>
        /// 同 <see cref="Parse(string)"/>,但允许 framework dispatch 时把 services 注入构造的 <see cref="UriArgs"/>。
        /// </summary>
        public static (string RouteKey, UriArgs Args) Parse(string uri, IReadOnlyDictionary<Type, object>? services)
        {
            if (string.IsNullOrEmpty(uri)) throw new ArgumentNullException(nameof(uri));

            string routeKey;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            int qIdx = uri.IndexOf('?');
            if (qIdx < 0)
            {
                routeKey = uri;
            }
            else
            {
                routeKey = uri.Substring(0, qIdx);
                var query = uri.Substring(qIdx + 1);
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq < 0)
                    {
                        values[Uri.UnescapeDataString(pair)] = "";
                    }
                    else
                    {
                        var k = Uri.UnescapeDataString(pair.Substring(0, eq));
                        var v = Uri.UnescapeDataString(pair.Substring(eq + 1));
                        values[k] = v;
                    }
                }
            }
            return (routeKey, new UriArgs(uri, values, services));
        }
    }
}
