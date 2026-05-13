# Python Handler 指南

蓝图低代码体系下 Python handler 的写法、注册、引用、调试。

---

## 1. 全景架构

```
┌──────────────────────────────────────────────────────────────────┐
│  C# 蓝图节点(PlotFeature / ComputeFeature / HandlerFeature)    │
│   ─ Indicator: "ma_dual"   ← 字符串引用 handler                   │
│   ─ PortBindings: {Inputs.close: "candle_close"}                 │
└──────────────────────────────────────┬───────────────────────────┘
                                       │ 装配期 + 每帧
                                       │
        ┌──────────────────────────────┼──────────────────────────┐
        │                              │                          │
┌───────▼────────┐         ┌───────────▼──────────┐    ┌──────────▼────────┐
│ Handler 调用路径 │         │ Indicator 元数据路径 │    │ Input 布线路径   │
│                │         │ (@indicator 才有)    │    │                  │
│ Registry.TryGet│         │ IndicatorMetadata    │    │ PortBindings 反射 │
│  ↓             │         │  Registry.Get        │    │  ↓               │
│ Func<...>      │         │  ↓                   │    │ DataPort<T> 焊接  │
│  ↓             │         │ {series:[...]}       │    │                  │
│ Python fn      │         │  ↓                   │    │                  │
│                │         │ 自动展开 N 个 Line/Bar │    │                  │
│                │         │ 子 Feature           │    │                  │
└────────────────┘         └──────────────────────┘    └──────────────────┘
        │                              │                          │
        └──────────────────────────────┼──────────────────────────┘
                                       │
                                       ▼
                       Python 进程内 _hevo_handlers / _hevo_indicators
                       (装饰器写入,framework 反射读取)
```

**关键事实:**

- 蓝图节点是 **C# Feature**,不存在"Python 节点"
- Python 是 **handler 实现**,被 Feature 通过 string name 引用
- Handler 注册是**进程级共享**,任何蓝图都能用
- Framework 提供完整 `hevo_indicators.ta.*` 标准 ta 库(SMA / EMA / RSI / MACD / Boll / ATR / MFI / VWAP / OBV / ...),业务侧能直接组合

---

## 2. 装饰器协议

### 2.1 `@register` —— 底层 handler 注册

任何 Python handler 都需要 `@register` 才能被蓝图引用。

```python
from hevo_indicators import register

@register("scanner_change_pct", inputs=["latest", "prev_close"])
def change_pct(latest, prev_close):
    return (latest - prev_close) / prev_close
```

**参数:**

| 参数 | 必填 | 说明 |
|---|---|---|
| `name`(位置参数) | ✅ | 蓝图节点引用的 handler 名,**全进程唯一** |
| `inputs` | 多输入 handler 必填 | 形参名列表,**顺序跟函数 signature 一致**;蓝图节点用 nested PortBindings(`Inputs.latest` / `Inputs.prev_close`)按形参名 wire |
| `signature` | 强类型 handler 必填 | 类型签名串(如 `"(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]"`);C# 端按此推断 .NET delegate 类型;**返回 list[dict] 的 plot handler 留空** |
| `incremental` | 可选 | `True` 标记增量协议 handler(§D2.6.4);蓝图侧据此决定每帧是否重算还是只算尾部 |

**name 跟 fn 名解耦:**

```python
@register("market_health_index", inputs=["close", "volume"])
def my_internal_calculation(close, volume):  # fn 名跟 name 不必一致
    ...
```

framework 怎么知道这两件事独立?`PythonRegisterScanner` 的 regex 把装饰器 name 跟 **下一行 def 的函数名** 分别捕获:

```regex
@register("(?<name>[^"]*)"...)
\s*\r?\n
\s*def\s+(?<func>\w+)\s*\(
```

`<name>` 用作蓝图引用 key,`<func>` 用作运行时调用的 Python 函数名。两者分开存:

```
_hevo_handlers["market_health_index"] = ("my_internal_calculation", signature, inputs)
                ↑                          ↑
              蓝图引用 key                  实际调的 fn 名
```

蓝图节点 `Indicator: "market_health_index"` → C# delegate 内部调 `module.Invoke("my_internal_calculation", args)`。

**几个硬约束:**

| 约束 | 说明 |
|---|---|
| 装饰器跟 def 中间不能有代码 | regex 要求 `@register(...)\s*\r?\n\s*def`,中间放 `print(...)` 之类的 statement 会让 regex 匹配失败,handler 注册不上 |
| fn 名必须是合法 Python identifier | 字母 / 下划线开头,不能空格 / 特殊字符 |
| 同 .py 文件内 fn 名必须唯一 | Python module attribute lookup |
| inputs 仍要跟形参名对齐 | fn 名能改,但 `inputs=[...]` 跟函数 `def fn(arg1, arg2)` 形参名顺序必须一致(蓝图 PortBindings nested key 按形参名 wire) |

**实践约定:`name = fn 名 = .py 文件名` 三者一致**,debug 友好:
- Python traceback 报错 `in ma_dual at line 5` → 直接对上蓝图节点 `Indicator: "ma_dual"`
- IDE go-to-definition 直接定位
- grep 能找到对应文件

```python
# ✅ 推荐:三者一致
# Assets/python/ma_dual.py
@register("ma_dual", inputs=["close"])
def ma_dual(close): ...
```

技术上能不一致(framework 支持),但**没合理用例**,只增加心智负担。

### 2.2 `@indicator` —— Pine 风味多 series 渲染

返回 `dict[str, ndarray]` 的 handler,声明 N 条 series 渲染元数据:

```python
from hevo_indicators import register, indicator, ta

@indicator("ma_dual", overlay=True, series=[
    ("ma20", "line", "#FF9800", 1.5),
    ("ma60", "line", "#2196F3", 1.5),
])
@register("ma_dual", inputs=["close"])
def ma_dual(close):
    return {
        "ma20": ta.sma(close, 20),
        "ma60": ta.sma(close, 60),
    }
```

**series 元素:** `(name, kind, color, width)`

| 字段 | 必填 | 说明 |
|---|---|---|
| `name` | ✅ | series 名,**必须跟函数返回 dict 的 key 对齐**(否则该 series 拿不到数据) |
| `kind` | ✅ | `"line"` / `"bar"` / `"scatter"` |
| `color` | 可选 | `"#RRGGBB"` 或 `"#AARRGGBB"`,缺省 `#888888` |
| `width` | 可选 | 线宽 / 柱宽,缺省 `1.5` |

**`overlay`** —— `True` 叠加主图(SMA / Bollinger),`False` 独立副图(MACD / RSI)。

> ⚠️ 当前 framework `@indicator` 跟 `@register` 是两个独立装饰器,**name 必须一致**。这是协议约束(蓝图节点 `Indicator` 字段单 string 同时查两个字典),**不写一致 silent miss**。

### 2.3 `@as_arrays` / `@pta_polyfill` —— 实现层语法糖

仅 framework `_ta_*.py` 内部用,业务 .py 一般用不上,了解即可。

- `@as_arrays(n)`:前 n 个位置参数自动 `np.asarray(.., dtype=float64)`,消除每个函数头的样板转换
- `@pta_polyfill(name)`:优先调 `pandas_ta.{name}(...)`,装不上 pandas_ta 时回退到原函数体

业务 .py 直接 `np.asarray(...)` 即可,不需要这两个。

---

## 3. 三种 handler 形态

按返回类型选装饰器组合:

### 3.1 单 ndarray —— 用 `@register`,蓝图节点手动 spec 1 条 series

```python
@register("rsi_14", inputs=["close"])
def rsi_14(close):
    return ta.rsi(close, 14)
```

蓝图节点:
```jsonc
{
  "TypeName": "PlotFeature",
  "Properties": {
    "Indicator": "rsi_14",
    "Series": [
      {"Name": "rsi_14", "Kind": "line", "Color": "Purple", "Width": 1.0,
       "YDomain": {"Min": 0, "Max": 100}}
    ]
  }
}
```

### 3.2 多 ndarray dict —— 用 `@register + @indicator`

```python
@indicator("macd", overlay=False, series=[
    ("macd",   "line", "#FFFFFF", 1.5),
    ("signal", "line", "#FF9800", 1.5),
    ("hist",   "bar",  "#4CAF50", 0.8),
])
@register("macd", inputs=["close"])
def macd(close):
    out = ta.macd(close, fast=12, slow=26, signal=9)
    return {"macd": out["macd"], "signal": out["signal"], "hist": out["hist"]}
```

蓝图节点 `Series` 字段**可省略** —— framework 从 `@indicator` 元数据自动展开 3 个子 Feature。

### 3.3 list[dict] —— 用 `@register`,蓝图节点 spec scatter / arrow

```python
@register("buy_signals_ma_cross", inputs=["close"])
def buy_signals_ma_cross(close):
    ma20 = ta.sma(close, 20)
    ma60 = ta.sma(close, 60)
    arrows = []
    for i in range(60, len(close)):
        if ma20[i-1] <= ma60[i-1] and ma20[i] > ma60[i]:
            arrows.append({
                "logical_x": float(i),
                "logical_y": float(close[i]),
                "direction": "down",
                "color":     "#4CAF50",
                "size":      8.0,
            })
    return arrows
```

蓝图节点:
```jsonc
{
  "TypeName": "PlotFeature",
  "Properties": {
    "Indicator": "buy_signals_ma_cross",
    "Series": [{"Name": "buy_signals_ma_cross", "Kind": "arrow"}]
  }
}
```

`@indicator` 不适用(它要求 series name 跟 dict key 对齐,scatter/arrow 是点位列表不是 ndarray dict)。

---

## 4. 命名约定:三层 string 严格对齐

```python
@indicator("ma_dual", series=[("ma20", "line", "#FF9800")])
@register("ma_dual", inputs=["close"])
def ma_dual(close):
    return {"ma20": ta.sma(close, 20)}
```

| 层级 | string | 出现位置 | 关联谁 |
|---|---|---|---|
| **Handler name** | `"ma_dual"` | `@register(name)` + `@indicator(name)` + 蓝图 `Indicator` 字段 | 三处必须严格一致;framework 用此 string 同时查 `_hevo_handlers` + `_hevo_indicators` |
| **Series name** | `"ma20"` | `@indicator(series=...)` + 函数返回 dict 的 key | 必须一致;不一致该子 layer 拿不到数据,屏幕该位置空 |
| **Input name** | `"close"` | `@register(inputs=...)` + 函数形参名 + 蓝图 PortBindings nested key(`Inputs.close`) | framework 自动从 `inspect.signature` 推 inputs 顺序;蓝图 nested key 按形参名 wire |

错位会**silent miss**(没有静态检查),debug 折磨。**永远写一致**。

---

## 5. inputs 协议(单输入 vs 多输入)

### 单输入

```python
@register("rsi_14", inputs=["close"])  # 单元素也写,清晰
def rsi_14(close):
    return ta.rsi(close, 14)
```

蓝图节点:
```jsonc
"PortBindings": { "Inputs.close": "candle_close" }
```

### 多输入

```python
@register("atr", inputs=["high", "low", "close"])
def atr(high, low, close):
    return ta.atr(high, low, close, period=14)
```

蓝图节点:
```jsonc
"PortBindings": {
  "Inputs.high":  "candle_high",
  "Inputs.low":   "candle_low",
  "Inputs.close": "candle_close"
}
```

framework 装配阶段按 inputs 列表把 N 个 port 焊到 handler 的对应位置参数。

### 形参名跟 inputs 必须一致

```python
@register("atr", inputs=["high", "low", "close"])
def atr(h, l, c):  # ❌ 错!形参名跟 inputs 不一致 → 蓝图无法 wire
    ...
```

framework 现在用 `inspect.signature` 自动推 inputs(跟形参名一致),写 `@register(inputs=[...])` 是**显式声明 + 校验**,推荐写。

---

## 6. handler 注册机制

### 6.1 业务侧只写 .py 文件 + csproj 声明 EmbeddedResource

```xml
<ItemGroup>
  <EmbeddedResource Include="Assets\python\my_indicator.py" />
</ItemGroup>
```

### 6.2 App 启动一次性扫描注册

```csharp
EmbeddedPythonHost.Default.LoadPythonAssetsFromAssembly(
    typeof(MyApp).Assembly,
    resourcePrefix: "MyApp.Assets.python.");
```

framework 内部:
1. 枚举 assembly 所有 EmbeddedResource(prefix + ".py" 后缀)
2. 把 .py 内容写到 sandbox temp dir
3. `PythonRegisterScanner.ScanText` 用 regex 扫 `@register("name", inputs=[...])` 装饰器
4. 对每个 descriptor 调 `PythonHandlerRegistry.RegisterPythonFunction(name, filePath, fn, inputs)`

加新 handler 工作流:**加 `.py` 文件 + csproj 加 EmbeddedResource 即生效,无需写任何 C# RegisterDelegate ceremony**。

### 6.3 进程级共享

`EmbeddedPythonHost.Default` 是 process-level singleton,所有蓝图共用 registry。`@register` 进 `_hevo_handlers` 的 handler 任何蓝图都能引用。

---

## 7. 蓝图侧引用 handler

### 7.1 PlotFeature(渲染指标)

```jsonc
{
  "TypeName": "PlotFeature",
  "Properties": {
    "Indicator":     "ma_dual",     // ← @register name
    "IndicatorName": "ma_dual",     // 兼容旧字段
    "Series": [...]                 // 仅当未用 @indicator 时手动 spec
  },
  "PortBindings": {
    "Inputs.close": "candle_close"
  }
}
```

### 7.2 ComputeFeature(纯计算 → 单 port 输出)

```jsonc
{
  "TypeName": "ComputeFeature",
  "Properties": {
    "Compute": "scanner_change_pct"
  },
  "PortBindings": {
    "Inputs.latest":     "stock_latest",
    "Inputs.prev_close": "stock_prev_close",
    "OutputPort":        "computed_change_pct"
  }
}
```

### 7.3 HandlerFeature / TooltipDoubleWidgetFeature 等

各 feature 字段不同,但**引用 handler 都用 string name**;`PortBindings` nested key 按 `inputs` 形参名对齐。详见各 feature 的 XML doc。

---

## 8. C# handler vs Python handler 的对称性

framework `BlueprintHandlerRegistry` 把两类 handler 抽象到**同一个 dict**:

```
BlueprintHandlerRegistry
└── _handlers: Dictionary<string, Delegate>
      ├── "scanner_change_pct"  → C# delegate(via [BlueprintHandler] AutoDiscover)
      ├── "scatter_amount_ratio" → Python wrapper(via @register + LoadPythonAssetsFromAssembly)
      └── ...
```

蓝图节点引用 name 不区分 C# / Python 来源。

### C# handler 路径(对称参考)

```csharp
public static class MyHandlers
{
    [BlueprintHandler("change_pct")]
    public static ReadOnlyMemory<double> ChangePct(
        ReadOnlyMemory<double> latest,
        ReadOnlyMemory<double> prev_close) { ... }
}

// 启动一次性
registry.AutoDiscoverStatic(typeof(MyHandlers));
```

framework 自动从 `[BlueprintHandler]` attribute 反射注册,inputs 形参名从 `ParameterInfo` 推。

**蓝图侧引用 `Compute: "change_pct"` 时,不知道也不关心是 C# 还是 Python**。

---

## 9. 错误诊断 / 常见坑

### 9.1 `BadPythonDllException` —— Python.NET 加载 dll 失败

| 根因 | 修法 |
|---|---|
| python312.dll 不存在 / Python312/ 目录没部署 | 跑 `scripts/setup-python.ps1` 部署 |
| 进程是 x64 但加载到 x86 dll(反之) | 检查 Python 安装架构 |
| numpy 没装 | `setup-python.ps1` 自带 pip install numpy |
| `PYTHONHOME` / `PYTHONPATH` env 错 | `EmbeddedPythonHost` 自动管,业务侧不要手动 set |

### 9.2 蓝图节点 silent miss

| 现象 | 根因 | 修法 |
|---|---|---|
| 该位置画面空 / 没数据 | series name 跟 dict key 不一致 | 检查 `@indicator(series=...)` 跟 `return {...}` 的 key 对齐 |
| handler 完全不调用 | `@register` name 跟蓝图 `Indicator` 不一致 | 检查 string 一致 |
| handler 收到 null 入参 | `inputs=[...]` 跟形参名不一致 / 蓝图 nested key 错 | 三处对齐 |

### 9.3 Python 函数运行时异常

framework `EmbeddedPythonHost` catch Python exception 翻成 `PythonDiagnosticsException`,`InnerException` 保留 Python traceback。debug 时看 `Debug.WriteLine` / Output 窗口。

### 9.4 handler 注册的两条路径(regex + Python dict)

framework `EmbeddedPythonHost.LoadPythonAssetsFromAssembly` **同时走两条路**:

```
1. regex 扫源码(基础保底)
   → PythonRegisterScanner.ScanText 拿 (name, fn, inputs)
   → RegisterPythonFunction 即时注册
   字面量 @register / 紧贴 def 的字面量场景全覆盖

2. Python dict 兜底(运行时真相)
   → import .py(装饰器实际执行,_hevo_handlers 字典填好)
   → 读 hevo_indicators._hevo_handlers 全局 dict
   → filter source_file 在 sandbox 内
   → RegisterPythonFunction 补注册 regex 漏掉的
   动态 name / 复杂装饰器叠加 / 一函数多 alias 等场景全覆盖
```

**dict 路径覆盖了所有 regex 限制**。Python 在线时,业务侧 .py 写法**自由**,不再受字面量约束:

| 写法 | regex 能否识别 | dict 路径能否识别 |
|---|---|---|
| `@register("foo", inputs=["x"])` 标准字面量 | ✅ | ✅ |
| `@register(VAR_NAME, inputs=["x"])` 动态 name | ❌ | ✅ |
| `@register("foo", inputs=COMMON_INPUTS)` 动态 inputs | ❌ | ✅ |
| `@register("foo")` `\n` `@some_decorator` `\n` `def foo` 中间夹装饰器 | ❌ | ✅ |
| `@register("foo")` `\n\n` `def foo` 中间空行 | ❌ | ✅ |
| 条件 / 循环里的 `@register`(运行期真分支才装饰) | ❌(regex 错误抓) | ✅(实际执行才进 dict) |
| 一函数多 alias `register("v1")(fn); register("v2")(fn)` | ❌ | ✅ |
| `register("foo")(fn)` 显式调用形式 | ❌ | ✅ |

#### 推荐写法仍然是字面量

虽然 dict 路径覆盖动态写法,**实践仍推荐字面量**,原因:

1. **DryRun 静态校验** —— 蓝图诊断 / IDE 提示走的是 regex 路径(无 Python runtime 依赖),写动态 name 时 DryRun 看不到 handler 存在,蓝图节点报"handler 未注册" warning
2. **可读性** —— 字面量直观,一眼看到 handler 名
3. **AI / grep 友好** —— 字符串字面量能 grep,`Indicator: "foo"` 配 `@register("foo")` 互查 0 成本

```python
# ✅ 标准写法(regex + dict 路径都识别)
from hevo_indicators import register, indicator, ta

@register("ma_dual", inputs=["close"])
def ma_dual(close):
    return ta.sma(close, 20)

# ✅ 双装饰器(@indicator 在外,@register 紧贴 def)
@indicator("ma_dual", overlay=True, series=[("ma20", "line")])
@register("ma_dual", inputs=["close"])
def ma_dual(close):
    return {"ma20": ta.sma(close, 20)}
```

#### 真要用动态写法(高级场景)的代价

如果业务侧确实需要(典型:动态生成 N 个 alias / 工厂模式批量注册):

```python
# 这种写法 regex 不识别,但 dict 路径能识别(运行时 import 后字典已填)
INDICATORS = ["ma5", "ma10", "ma20", "ma60"]
for n in [5, 10, 20, 60]:
    name = f"ma_{n}"
    @register(name, inputs=["close"])
    def _ma(close, period=n):
        return ta.sma(close, period)
```

**代价:DryRun 看不到这些 handler**(IDE / 蓝图编辑器列不出 dropdown 选项)。运行期 OK,设计期不友好。

#### 实现细节

`hevo_indicators/__init__.py` 的 `register` 装饰器存 (fn_name, signature, inputs, **source_file**)。`source_file` 由 `inspect.getfile(fn)` 自动捕获,framework C# 端用它 filter "哪些 dict entry 来自我们 sandbox 内的文件",避免误把 framework 自带的 `ta.*` handler 重复注册。

---

## 10. 写新 Python handler 的 step-by-step

### Step 1. 决定 handler 形态(返回类型)

| 我要画啥 | 返回类型 | 装饰器 |
|---|---|---|
| 单条曲线(一根 RSI) | `ndarray` | `@register` |
| 多条曲线(MACD 三件套) | `dict[str, ndarray]` | `@register + @indicator` |
| 散点 / 箭头 / 任意点位 | `list[dict]` | `@register` |

### Step 2. 创建 .py 文件

`Hevo.Drawing.MultiStockDemo/Assets/python/my_indicator.py`:

```python
from hevo_indicators import register, ta

@register("my_indicator", inputs=["close"])
def my_indicator(close):
    # 优先用 framework 提供的 ta.* 库,避免自己重写 SMA/EMA/RSI 等
    return ta.sma(close, 20)
```

### Step 3. csproj 加 EmbeddedResource

```xml
<EmbeddedResource Include="Assets\python\my_indicator.py" />
```

### Step 4. 蓝图节点引用

蓝图编辑器拖个 PlotFeature 节点,设 `Indicator = "my_indicator"`,把 close port 接到 `Inputs.close`。

### Step 5. 启动 demo,完事

`BlueprintAppBootstrap.EnsureInitialized()` 启动时扫所有 EmbeddedResource,自动注册。`my_indicator` 立刻可用。

**不写任何 RegisterPythonFunction / RegisterDelegate / 蓝图节点装配 C# 代码**。

---

## 11. framework 已经预装的 ta 指标库

业务侧用 `from hevo_indicators import ta` 然后 `ta.sma(close, 20)` 即可,**不需要自己写**。

| 模块 | handler |
|---|---|
| MA | `ta.sma` / `ta.ema` / `ta.wma` / `ta.dema` / `ta.tema` |
| 动量 | `ta.rsi` / `ta.roc` / `ta.mom` / `ta.macd` |
| 波动率 | `ta.stdev` / `ta.bbands`(Bollinger) |
| 多输入 | `ta.true_range` / `ta.atr` / `ta.mfi` / `ta.vwap` / `ta.obv` |

详见 [HevoIndicatorsSources.cs](HevoIndicatorsSources.cs) 内嵌的 `_ta_*.py` 源码。

---

## 12. 设计协议总结

1. **handler name 是 single source of truth** —— `@register(name)` / `@indicator(name)` / 蓝图 `Indicator` 字段必须一致
2. **handler 进程级共享** —— `_hevo_handlers` 字典,任何蓝图都能引用
3. **C# / Python handler 对蓝图侧透明** —— 引用 string name,不区分来源
4. **加 handler = 加文件**(零样板) —— `.py` + csproj EmbeddedResource;framework 启动时自动扫描注册
5. **`@indicator` 是多 series 语法糖** —— 不是"对外可见"标记,是"返回 dict 自动展开 N 子 layer"协议
6. **永远复用 ta.*** —— SMA / EMA / RSI 等通用指标 framework 自带,业务侧不要重写
