using System.Collections.Generic;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 把旧 well-known 命名(VP_* / LinkedHit / 等)翻译成新 scope-qualified 形态(<c>cell:</c> / <c>dashboard:</c> 前缀)。
    /// 旧 JSON 资产零修改加载所必需的兼容层 ——
    /// <see cref="GraphDeserializer.FromBlueprint"/> 读 PortBinding 字符串后第一步走本 helper 翻译,
    /// 再写到 <c>Port.BindingId</c>。
    ///
    /// <para>
    /// 跟 plan_binding_first_class.md §1.5 一致。新代码推荐直接写 scope 前缀;老资产无需迁移。
    /// 字符串不在表里 → 原样返回(其它 scope 前缀 / cell-local guid / 业务自定义名都不动)。
    /// </para>
    /// </summary>
    public static class BindingIdMigrator
    {
        // 老 well-known → 新 scope-qualified。case-sensitive(老 JSON 都按 PascalCase 写)。
        private static readonly IReadOnlyDictionary<string, string> _legacyAlias = new Dictionary<string, string>
        {
            // viewport 三件套
            ["VP_LogicalLength"] = "cell:viewport.logicalLength",
            ["VP_UserRange"]     = "cell:viewport.userRange",
            ["VP_ActiveRange"]   = "cell:viewport.active",

            // dashboard hit 共享(旧 LinkedHit 字面值)
            ["LinkedHit"]        = "dashboard:linkedHit",
        };

        /// <summary>翻译一个 binding 字符串。不在表里 → 原样返回。</summary>
        public static string Migrate(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            return _legacyAlias.TryGetValue(raw, out var newName) ? newName : raw;
        }
    }
}
