using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Hevo.Trade;
using Python.Runtime;

namespace Hevo.Drawing.LowCodeDemo
{
    /// <summary>
    /// 通知渠道分类 —— 通知中心 UI 用颜色 / 图标区分。
    /// </summary>
    public enum NotificationKind
    {
        Email,
        DingTalk,
        Webhook,
        Sms,
        Alert,
        Order,
    }

    /// <summary>
    /// 单条通知记录 —— 不可变,以 record 形式投递到 UI / 持久化日志。
    /// </summary>
    /// <param name="Timestamp">本机时间(创建时刻)。</param>
    /// <param name="Kind">渠道分类,见 <see cref="NotificationKind"/>。</param>
    /// <param name="Title">短标题(邮件主题 / 钉钉标题 / 告警 message)。</param>
    /// <param name="Body">长内容(邮件正文 / Webhook payload 字符串化 / 告警 detail)。</param>
    /// <param name="Recipient">收件人(邮件 to / 短信号码 / Webhook URL / 钉钉群)。</param>
    public sealed record NotificationEvent(
        DateTime Timestamp,
        NotificationKind Kind,
        string Title,
        string Body,
        string Recipient);

    /// <summary>
    /// 业务侧 mock 通知中枢 —— Python 端 builtin <c>send_email / send_dingtalk / send_webhook / send_sms / log_alert</c>
    /// 收到的 dict 全部进这里:① 落进程级历史 <see cref="History"/>;② 触发 <see cref="NotificationFired"/> 事件。
    ///
    /// <para>
    /// **不发真实网络请求** —— 仅 demo / CI / smoke test 用,验证 <see cref="PyBusinessBridge"/> 桥的接法。
    /// 真实业务侧把这五个 builtin 换成 SmtpClient / HttpClient 实现即可,Python 策略代码无需任何改动。
    /// </para>
    ///
    /// <para>
    /// **线程模型**:Python 调用 send_xxx 时在 Python indicator 解析线程跑(GIL 持锁),
    /// 解 PyObject 后**立即 fire** 事件 —— 事件 handler 内部不应做耗时 I/O。NotificationCenterView
    /// 在 handler 里只做 Dispatcher.BeginInvoke 入队,真正 UI 更新在 UI 线程,避免阻塞 indicator。
    /// </para>
    /// </summary>
    public static class MockNotifier
    {
        private const int MaxHistory = 1000;

        // 进程级历史 —— ConcurrentQueue 让 Python 多线程并发 push 安全;
        // 头部老记录在到达 MaxHistory 后被 TryDequeue 弹出,内存有界。
        private static readonly ConcurrentQueue<NotificationEvent> _history = new();

        /// <summary>历史快照(只读,最新在末尾)。</summary>
        public static IReadOnlyList<NotificationEvent> History => _history.ToArray();

        /// <summary>新通知触达 —— UI 订阅刷新 DataGrid;handler 跑在 Python 调用线程,
        /// UI 侧务必 marshal 到 Dispatcher 不要直接 mutate observable collection。</summary>
        public static event Action<NotificationEvent>? NotificationFired;

        /// <summary>显式清空 —— UI "清空"按钮调用。</summary>
        public static void Clear()
        {
            while (_history.TryDequeue(out _)) { }
            NotificationFired?.Invoke(new NotificationEvent(
                DateTime.Now, NotificationKind.Alert, "[已清空]", "历史记录已清空", ""));
        }

        // ── Python builtin 入口 ─────────────────────────────────────────────

        /// <summary>py: <c>send_email({'subject':'...','body':'...','to':'...'})</c></summary>
        public static void SendEmail(PyObject req)
        {
            var (title, body, to) = ParseTriple(req, keyTitle: "subject", keyBody: "body", keyTo: "to");
            Fire(NotificationKind.Email, title, body, to);
        }

        /// <summary>py: <c>send_dingtalk({'title':'...','text':'...','at_mobiles':['...']})</c></summary>
        public static void SendDingTalk(PyObject req)
        {
            var title = TryGetString(req, "title", fallback: "[钉钉]");
            var text  = TryGetString(req, "text",  fallback: "");
            var ats   = TryGetStringList(req, "at_mobiles");
            var recipient = ats.Count > 0 ? "@" + string.Join(",@", ats) : "(群)";
            Fire(NotificationKind.DingTalk, title, text, recipient);
        }

        /// <summary>py: <c>send_webhook({'url':'https://...','json':{...}})</c></summary>
        public static void SendWebhook(PyObject req)
        {
            var url     = TryGetString(req, "url", fallback: "(no url)");
            var payload = TryGetReprAtKey(req, "json");
            Fire(NotificationKind.Webhook, "POST " + url, payload, url);
        }

        /// <summary>py: <c>send_sms({'phone':'138...','text':'...'})</c></summary>
        public static void SendSms(PyObject req)
        {
            var (text, _, phone) = ParseTriple(req, keyTitle: "text", keyBody: "text", keyTo: "phone");
            // SMS 单字段 text,title=body 同源
            Fire(NotificationKind.Sms, text, text, phone);
        }

        /// <summary>py: <c>log_alert({'level':'INFO','kind':'...','msg':'...'})</c></summary>
        public static void LogAlert(PyObject req)
        {
            var level = TryGetString(req, "level", fallback: "INFO");
            var kind  = TryGetString(req, "kind",  fallback: "alert");
            var msg   = TryGetString(req, "msg",   fallback: "");
            Fire(NotificationKind.Alert, $"[{level}] {kind}", msg, "");
        }

        /// <summary>C# 侧调用:把 trade.place_order 的下单回执映射成 Order 类型通知。</summary>
        public static void LogOrder(string symbol, string direction, double qty, double price, string brokerOrderId, bool success)
        {
            var title = success
                ? $"{direction.ToUpperInvariant()} {symbol} ×{qty:0.##} @ {price:F3}"
                : $"❌ {direction.ToUpperInvariant()} {symbol} ×{qty:0.##} @ {price:F3} 被拒";
            var body = $"broker_order_id={brokerOrderId} ({(success ? "已接单" : "失败")})";
            Fire(NotificationKind.Order, title, body, symbol);
        }

        // ── OrderUpdates 元数据缓存 + Observable 接入 ───────────────────────
        //
        // ITradeService.OrderUpdates 推的 OrderUpdate 只带 BrokerOrderId / Status / FilledQty /
        // AvgPrice / Ts —— 不含 symbol / direction / quantity。要在通知中心呈现完整可读的订单事件,
        // 必须在 PlaceOrderAsync 成功 ack 时记一份 (BrokerOrderId → OrderRequest) 元数据缓存,
        // OrderUpdate 到达时反查。装饰器 RecordingTradeService 负责调 RecordOrderRequest 灌缓存。
        //
        // 缓存条目数上界 = 进程已下过的总订单数,理论无限增长。demo 容忍(单次跑 < 1000 单),
        // 真实业务侧需要 LRU / 终态后清理。

        private static readonly ConcurrentDictionary<string, OrderRequest> _orderMeta = new();

        /// <summary>RecordingTradeService 在 PlaceOrderAsync 成功 ack 后调,灌元数据缓存。</summary>
        public static void RecordOrderRequest(string brokerOrderId, OrderRequest req)
        {
            if (string.IsNullOrEmpty(brokerOrderId) || req == null) return;
            _orderMeta[brokerOrderId] = req;
        }

        /// <summary>
        /// 给 ITradeService.OrderUpdates.Subscribe 用 —— 把 broker 推的状态更新翻成 Order 类型通知。
        /// Pending(挂单回执) / Filled(成交) / Cancelled / Rejected / Partial 都会落一条记录,
        /// title 含中文状态标签 + symbol + direction + qty + 价格,body 含 broker_order_id + 成交均价。
        /// </summary>
        public static void OnOrderUpdate(OrderUpdate u)
        {
            // 反查元数据 —— 没记到(罕见:外部直连 broker 没经 PlaceOrderAsync 落地)用 fallback 字段
            string symbol    = "?";
            string direction = "?";
            double reqQty    = 0;
            double reqPrice  = 0;
            if (_orderMeta.TryGetValue(u.BrokerOrderId, out var req))
            {
                symbol    = req.Symbol;
                direction = req.Direction.ToString();
                reqQty    = req.Quantity;
                reqPrice  = req.LimitPrice;
            }

            string statusLabel = u.Status switch
            {
                OrderStatus.Pending   => "🟡 挂单",
                OrderStatus.Partial   => "🟠 部成",
                OrderStatus.Filled    => "🟢 成交",
                OrderStatus.Cancelled => "⚪ 已撤",
                OrderStatus.Rejected  => "🔴 被拒",
                _ => u.Status.ToString(),
            };

            string title = u.Status == OrderStatus.Filled || u.Status == OrderStatus.Partial
                ? $"{statusLabel} {direction.ToUpperInvariant()} {symbol} ×{u.FilledQty:0.##} @ {u.AvgPrice:F3}"
                : $"{statusLabel} {direction.ToUpperInvariant()} {symbol} ×{reqQty:0.##} (req@{reqPrice:F3})";

            string body = $"broker_order_id={u.BrokerOrderId}, status={u.Status}, filled={u.FilledQty:0.##}, avg={u.AvgPrice:F3}, ts={u.Ts:HH:mm:ss.fff}";

            Fire(NotificationKind.Order, title, body, symbol);

            // 终态后清缓存(成交 / 撤单 / 拒单后再有更新不可能,留着白占内存)
            if (u.Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected)
            {
                _orderMeta.TryRemove(u.BrokerOrderId, out _);
            }
        }

        // ── 内部 ────────────────────────────────────────────────────────────

        private static void Fire(NotificationKind kind, string title, string body, string recipient)
        {
            var ev = new NotificationEvent(DateTime.Now, kind, title, body, recipient);
            _history.Enqueue(ev);
            while (_history.Count > MaxHistory && _history.TryDequeue(out _)) { }
            try { NotificationFired?.Invoke(ev); }
            catch { /* UI 侧 handler 抛不应该传染 Python 调用 */ }
        }

        // ── PyObject 解析工具 ──────────────────────────────────────────────
        // 调用者已经在 Python GIL 内(send_xxx 由 PyBusinessBridge 注入,Python 持锁调入 C#),
        // 这里 re-acquire Py.GIL 是为了安全(GIL 可重入,nested 无害),避免有人在别处直接调这五个静态方法。

        private static (string title, string body, string recipient) ParseTriple(
            PyObject req, string keyTitle, string keyBody, string keyTo)
        {
            using (Py.GIL())
            {
                return (
                    TryGetStringInGil(req, keyTitle),
                    TryGetStringInGil(req, keyBody),
                    TryGetStringInGil(req, keyTo));
            }
        }

        private static string TryGetString(PyObject req, string key, string fallback = "")
        {
            using (Py.GIL())
            {
                var v = TryGetStringInGil(req, key);
                return string.IsNullOrEmpty(v) ? fallback : v;
            }
        }

        private static string TryGetStringInGil(PyObject req, string key)
        {
            try
            {
                using var val = req.GetItem(key);
                if (val == null || val.IsNone()) return "";
                return val.ToString() ?? "";
            }
            catch
            {
                // KeyError / TypeError(非 dict) / __getitem__ 不支持 —— 静默返回空串
                return "";
            }
        }

        private static List<string> TryGetStringList(PyObject req, string key)
        {
            var result = new List<string>();
            using (Py.GIL())
            {
                try
                {
                    using var val = req.GetItem(key);
                    if (val == null || val.IsNone()) return result;
                    using var iter = val.GetIterator();
                    while (iter.MoveNext())
                    {
                        var item = iter.Current;
                        var s = item?.ToString();
                        if (!string.IsNullOrEmpty(s)) result.Add(s!);
                    }
                }
                catch { /* 不是 iterable 时返回空,跟 missing key 同义 */ }
            }
            return result;
        }

        /// <summary>对 webhook json payload 这类嵌套结构,直接 repr 一下作为 body 字符串显示。</summary>
        private static string TryGetReprAtKey(PyObject req, string key)
        {
            using (Py.GIL())
            {
                try
                {
                    using var val = req.GetItem(key);
                    if (val == null || val.IsNone()) return "";
                    return val.ToString() ?? "";
                }
                catch { return ""; }
            }
        }
    }
}
