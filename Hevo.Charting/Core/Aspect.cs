using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Hevo.Charting.Core
{
    public interface IChartFeature
    {
        void Compose(ChartCell chart, RenderContext ctx);
        void Decompose(ChartCell chart, RenderContext ctx);
    }

    public abstract class ChartAspect : IChartFeature, IDisposable, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // 内部私有类，外部不可直接创建
        private class EmptyAspect : ChartAspect
        {
            // 它什么都不重写，所以默认执行基类的行为：
            // Decorate 返回 ContentIdentity (表示不装饰)
            // Compose 什么都不做
        }

        // 静态单例，全局只存一份，节省内存
        public static readonly ChartAspect Empty = new EmptyAspect();

        // 这就是你要的：对内装饰和对外编排
        public virtual UIElement Decorate(UIElement inner)
        {
            return inner;
        }
        public virtual void Compose(ChartCell chart, RenderContext ctx) { }
        public virtual void Decompose(ChartCell chart, RenderContext ctx) { }
        public virtual void Dispose() { }

        // ==========================================
        // 💥 魔法契约：允许 Aspect 像字符串一样被拼接组合！
        // ==========================================
        public static ChartAspect operator +(ChartAspect left, ChartAspect right)
        {
            if (left == null || left == Empty) return right;
            if (right == null || right == Empty) return left;

            return new CombinedAspect(left, right);
        }

        // 内部类：负责将多个 WPF 装饰器嵌套包裹
        private class CombinedAspect : ChartAspect
        {
            private readonly ChartAspect _outer;
            private readonly ChartAspect _inner;

            public CombinedAspect(ChartAspect outer, ChartAspect inner)
            {
                _outer = outer;
                _inner = inner;
            }

            public override UIElement Decorate(UIElement inner)
            {
                return _outer.Decorate(_inner.Decorate(inner));
            }

            public override void Compose(ChartCell chart, RenderContext ctx)
            {
                _inner.Compose(chart, ctx);
                _outer.Compose(chart, ctx);
            }

            public override void Decompose(ChartCell chart, RenderContext ctx)
            {
                _outer.Decompose(chart, ctx);
                _inner.Decompose(chart, ctx);
            }
        }
    }
}
