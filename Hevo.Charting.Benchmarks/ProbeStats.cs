using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hevo.Charting.Benchmarks
{
    internal static class Stats
    {
        public static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return double.NaN;
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }

        /// <summary>均值 ± 95% 置信区间半宽(t 分布,样本数 &lt; 2 时半宽为 NaN)。</summary>
        public static (double Mean, double HalfWidth) MeanCi95(IReadOnlyList<double> v)
        {
            if (v.Count == 0) return (double.NaN, double.NaN);
            double mean = v.Average();
            if (v.Count < 2) return (mean, double.NaN);
            double sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
            return (mean, T95(v.Count - 1) * sd / Math.Sqrt(v.Count));
        }

        /// <summary>中位数 ± 95% 置信区间半宽(按 bootstrap 近似:1.58·IQR/√n,McGill 1978 的缺口箱线图公式)。</summary>
        public static (double Median, double HalfWidth) MedianCi95(IReadOnlyList<double> v)
        {
            if (v.Count == 0) return (double.NaN, double.NaN);
            double med = Percentile(v, 0.5);
            if (v.Count < 2) return (med, double.NaN);
            double iqr = Percentile(v, 0.75) - Percentile(v, 0.25);
            return (med, 1.58 * iqr / Math.Sqrt(v.Count));
        }

        // 双侧 95% t 临界值,df 1..30;更大用 1.96
        private static readonly double[] TTable =
        {
            12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
            2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
            2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
        };

        private static double T95(int df) => df <= 0 ? double.NaN : df <= TTable.Length ? TTable[df - 1] : 1.96;
    }

    /// <summary>
    /// 计数基线(CI 回归门槛)。只收确定性计数:Feature 重算/帧、图层重录/帧、绘制命令/帧 —— 不收耗时,
    /// 耗时在共享 CI 机器上噪声太大。超出基线 (1+容差) 倍即判回归;明显低于基线提示更新基线。
    /// </summary>
    internal sealed class ProbeBaseline
    {
        public sealed record Counter(double FeaturesPerFrame, double LayersPerFrame, double DrawCmdsPerFrame);

        public Dictionary<string, Counter> Entries { get; set; } = new();

        public static ProbeBaseline FromEntries(IEnumerable<RenderProbe.Entry> entries)
        {
            var b = new ProbeBaseline();
            foreach (var e in entries.Where(e => e.Frames > 0 && e.CountersDeterministic))
                b.Entries[e.Key] = new Counter(Math.Round(e.FeaturesPerFrame, 4), Math.Round(e.LayersPerFrame, 4), Math.Round(e.DrawCmdsPerFrame, 2));
            return b;
        }

        public static ProbeBaseline Load(string path)
            => JsonSerializer.Deserialize<ProbeBaseline>(File.ReadAllText(path)) ?? new ProbeBaseline();

        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        public (bool Regressed, string Text) Check(IEnumerable<RenderProbe.Entry> current, double tolerance)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### render-probe 计数回归检查");
            sb.AppendLine();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"容差 {tolerance:P0}(计数超过基线 ×(1+容差) 判回归)"));
            sb.AppendLine();
            sb.AppendLine("| 组合 | 指标 | 基线 | 本次 | 结论 |");
            sb.AppendLine("|---|---|---:|---:|---|");
            bool regressed = false;
            int checkedCount = 0;
            foreach (var e in current)
            {
                if (!Entries.TryGetValue(e.Key, out var b)) continue;
                checkedCount++;
                if (e.Frames == 0)
                {
                    regressed = true;
                    sb.AppendLine($"| {e.Key} | - | - | - | ❌ 失败:{e.Error} |");
                    continue;
                }
                regressed |= Row(sb, e.Key, "Feature重算/帧", b.FeaturesPerFrame, e.FeaturesPerFrame, tolerance, 0.01);
                regressed |= Row(sb, e.Key, "图层重录/帧", b.LayersPerFrame, e.LayersPerFrame, tolerance, 0.01);
                regressed |= Row(sb, e.Key, "绘制命令/帧", b.DrawCmdsPerFrame, e.DrawCmdsPerFrame, tolerance, 1);
            }
            if (checkedCount == 0)
            {
                regressed = true;
                sb.AppendLine("| - | - | - | - | ❌ 本次结果里没有一个组合能对上基线的 key(参数和生成基线时不一致?) |");
            }
            sb.AppendLine();
            sb.AppendLine($"共比对 {checkedCount} 个组合(上表只列出偏离基线的行)。");
            sb.AppendLine(regressed ? "结论:❌ 有计数回归" : "结论:✅ 计数未回归");
            return (regressed, sb.ToString());
        }

        private static bool Row(StringBuilder sb, string key, string name, double baseline, double actual, double tol, double slack)
        {
            bool bad = actual > baseline * (1 + tol) + slack;
            bool better = actual < baseline * (1 - tol) - slack;
            string verdict = bad ? "❌ 回归" : better ? "⬇ 低于基线(可更新基线)" : "✅";
            if (bad || better)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {key} | {name} | {baseline:F2} | {actual:F2} | {verdict} |"));
            return bad;
        }
    }
}
