using System.ComponentModel;
using System.Text.Json;
using Hevo.Charting;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using ModelContextProtocol.Server;

namespace Hevo.Charting.Mcp;

/// <summary>
/// MCP 工具集 —— 把 Hevo.Charting 蓝图能力暴露给 LLM:
/// <list type="bullet">
///   <item><c>list_components</c>:列已注册组件,LLM 拼蓝图前先看清"积木盒里有什么"</item>
///   <item><c>describe_component</c>:看某组件的输入/输出端口,定义它能跟谁连</item>
///   <item><c>validate_blueprint</c>:LLM 生成蓝图后跑 DryRun,把端口类型不匹配 / 未注册 handler 等
///         "加载会过但运行黑屏"的隐性故障提前暴露。</item>
/// </list>
/// </summary>
[McpServerToolType]
public static class HevoChartingTools
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "list_components")]
    [Description("List components registered in the Hevo.Charting ComponentRegistry, optionally filtered by kind. " +
                 "Returns a JSON array of {alias, fullTypeName, kind}. Use this before composing blueprints to see " +
                 "what features/traits/datasources are available.")]
    public static string ListComponents(
        [Description("Filter: \"feature\" (ChartFeature derived), \"trait\" (IVisualTrait derived), " +
                     "\"datasource\" (DataSource<,> derived), or \"all\" (default).")]
        string kind = "all")
    {
        var entries = ComponentRegistry.ListAll().ToList();
        var filtered = entries
            .Select(kv => new { Alias = kv.Key, Type = kv.Value, Kind = ClassifyKind(kv.Value) })
            .Where(x => x.Kind != "unknown")  // 只暴露能用的三类,internal 杂项不出现在 LLM 视野里
            .Where(x => kind.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                        kind.Equals(x.Kind, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Alias, StringComparer.Ordinal)
            .Select(x => new
            {
                alias = x.Alias,
                fullTypeName = x.Type.FullName ?? x.Type.Name,
                kind = x.Kind,
            })
            .ToList();

        return JsonSerializer.Serialize(filtered, PrettyJson);
    }

    [McpServerTool(Name = "describe_component")]
    [Description("Describe a registered component: its category and port shape (input/output ports with types). " +
                 "Use the alias from list_components. Returns {alias, fullTypeName, kind, category, inputPorts, outputPorts}.")]
    public static string DescribeComponent(
        [Description("Component alias as registered in ComponentRegistry, e.g. \"LineSeriesFeature\".")]
        string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return SerializeError("typeName 不能为空。");
        if (!ComponentRegistry.IsRegistered(typeName))
            return SerializeError($"组件 '{typeName}' 未在 ComponentRegistry 登记。先调 list_components 看看可用组件。");

        Type type;
        try { type = ComponentRegistry.Resolve(typeName); }
        catch (Exception ex) { return SerializeError(ex.Message); }

        Node node;
        try { node = NodeFactory.CreateNode(type, new HevoPoint(0f, 0f)); }
        catch (Exception ex) { return SerializeError($"NodeFactory.CreateNode 失败: {ex.Message}"); }

        var result = new
        {
            alias = typeName,
            fullTypeName = type.FullName ?? type.Name,
            kind = ClassifyKind(type),
            category = node.Category.ToString(),
            inputPorts = node.InputPorts.Select(SerializePort).ToList(),
            outputPorts = node.OutputPorts.Select(SerializePort).ToList(),
        };
        return JsonSerializer.Serialize(result, PrettyJson);
    }

    [McpServerTool(Name = "validate_blueprint")]
    [Description("Validate a ChartBlueprint JSON via BlueprintLauncher.DryRun (static type / port / handler analysis). " +
                 "Catches the \"loads-but-blank-screen\" failure modes before launch. Returns {error, launched, diagnostics[]}.")]
    public static string ValidateBlueprint(
        [Description("Full ChartBlueprint JSON (DataSource + InitialTraits + Triggers + Features). " +
                     "MCP server cannot resolve named handlers (those are runtime-side), so trigger/delegate " +
                     "handler references will surface as warnings — that's expected for a static check.")]
        string blueprintJson)
    {
        if (string.IsNullOrWhiteSpace(blueprintJson))
            return SerializeError("blueprintJson 不能为空。");

        ChartBlueprint? blueprint;
        try
        {
            blueprint = JsonSerializer.Deserialize<ChartBlueprint>(blueprintJson, BlueprintJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            return SerializeError($"JSON 解析失败: {ex.Message}");
        }
        if (blueprint == null)
            return SerializeError("Blueprint JSON 反序列化为 null。");

        // handlers=null:MCP 进程没有业务侧 handler 模块的运行时实例,trigger/delegate handler 引用一律报
        // BP_TRIGGER_HANDLER_MISSING / BP_HANDLER_NOT_REGISTERED warning。这跟 LLM 拼蓝图时的语义一致 ——
        // 蓝图本身只持 string 名字,运行时由业务侧 AutoDiscover 把名字接到真实闭包上。
        var dryRun = BlueprintLauncher.DryRun(blueprint, handlers: null);

        var result = new
        {
            error = dryRun.Error,
            launched = dryRun.Launched,
            diagnostics = dryRun.Diagnostics.Select(d => new
            {
                severity = d.Severity.ToString(),
                code = d.Code,
                featureTypeName = d.FeatureTypeName,
                portName = d.PortName,
                message = d.Message,
            }).ToList(),
        };
        return JsonSerializer.Serialize(result, PrettyJson);
    }

    // -----------------------------------------------------------------
    // 内部工具
    // -----------------------------------------------------------------

    private static string ClassifyKind(Type t)
    {
        if (typeof(ChartFeature).IsAssignableFrom(t)) return "feature";
        if (typeof(IVisualTrait).IsAssignableFrom(t)) return "trait";
        if (LooksLikeDataSource(t)) return "datasource";
        return "unknown";
    }

    private static bool LooksLikeDataSource(Type type)
    {
        var t = type.BaseType;
        while (t != null && t != typeof(object))
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DataSource<,>)) return true;
            t = t.BaseType;
        }
        return false;
    }

    private static object SerializePort(Port p) => new
    {
        name = p.Name,
        dataType = p.DataTypeName,
        isArray = p.IsArray,
        description = p.Description,
    };

    private static string SerializeError(string message) =>
        JsonSerializer.Serialize(new { error = message }, PrettyJson);
}
