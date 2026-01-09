using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Core
{
    public record VisibilityTrait(bool IsVisible) : IVisualTrait
    {
        // 💥 预设：一眼看懂的语义
        public static readonly VisibilityTrait Visible = new(true);
        public static readonly VisibilityTrait Hidden = new(false);

        // 💥 工厂：方便从业务布尔值转换
        public static VisibilityTrait Create(bool visible) => visible ? Visible : Hidden;
    }

    public static class LayerProxyExtensions
    {
        /// <summary>
        /// 【强类型发布】
        /// 只有当目标图层 (TLayer) 声明了 IConsumes<TData> 时，此方法才能通过编译！
        /// </summary>
        public static VisualProxy<TLayer> PublishData<TLayer, TData>(
            this VisualProxy<TLayer> proxy,
            TData trait)
            where TLayer : IChartLayer, IConsumes<TData>
            where TData : class, IVisualTrait
        {
            // 底层调用刚才隐藏起来的 PublishRaw
            return proxy.PublishData(trait);
        }

        /// <summary>
        /// 【强类型更新】
        /// 基于老快照修改，只有合法图层才能调用。
        /// </summary>
        public static VisualProxy<TLayer> UpdateData<TLayer, TData>(
            this VisualProxy<TLayer> proxy,
            Func<TData?, TData> modifier)
            where TLayer : IChartLayer, IConsumes<TData>
            where TData : class, IVisualTrait
        {
            return proxy.PublishData(modifier);
        }

        /// <summary>
        /// 【通用能力】任何图层都可以隐藏
        /// </summary>
        public static VisualProxy<TLayer> SetVisibility<TLayer>(
            this VisualProxy<TLayer> proxy,
            bool isVisible)
            where TLayer : IChartLayer
        {
            // 绕过 PublishData 的强约束，直接对底层存储注入 Trait
            // 假设 proxy 内部有一个 UnsafePublish 供基建使用，或者直接操作字典
            proxy.PublishData(new VisibilityTrait(isVisible));
            return proxy;
        }
    }

    /// <summary>
    /// 全局共享黑板的专属发布通道
    /// </summary>
    public static class SharedProxyExtensions
    {
        /// <summary>
        /// 【全局无限制发布】
        /// 只要 TTarget 是 ISharedScope，任何特质都可以被直接发布，无需 IConsumes 约束！
        /// </summary>
        public static VisualProxy<TTarget> PublishData<TTarget, TData>(
            this VisualProxy<TTarget> proxy,
            TData trait)
            where TTarget : ISharedScope      // <--- 核心魔法：只针对全局作用域生效
            where TData : class, IVisualTrait
        {
            // 直接调用 internal 的底层方法
            return proxy.PublishData(trait);
        }

        /// <summary>
        /// 【全局无限制更新】
        /// </summary>
        public static VisualProxy<TTarget> UpdateData<TTarget, TData>(
            this VisualProxy<TTarget> proxy,
            Func<TData?, TData> modifier)
            where TTarget : ISharedScope
            where TData : class, IVisualTrait
        {
            return proxy.PublishData(modifier);
        }
    }

    /// <summary>
    /// 通用的外部支持特质扩展：任何图层都能用的能力，直接注入 Trait 就完事了。
    /// 框架使用者也可以在这里添加更多全局能力，而不需要修改核心引擎代码。
    /// 但是必须是全局能力，不能针对特定图层的业务能力，否则就违背了设计初衷！
    /// </summary>
    public static class UniversalTraitExtensions
    {
        /// <summary>
        /// 配置[`Layer`]的显隐性
        /// </summary>
        /// <typeparam name="TLayer"></typeparam>
        /// <param name="proxy"></param>
        /// <param name="isVisible"></param>
        /// <returns></returns>
        public static VisualProxy<TLayer> Visibility<TLayer>(this VisualProxy<TLayer> proxy, bool isVisible)
            where TLayer : IChartLayer => proxy.PublishData(new VisibilityTrait(isVisible));

        /// <summary>
        /// 💥 唯一真神：Meta 扩展。
        /// 在 Define 阶段调用：写入初始包。
        /// 在 Project 阶段调用：写入事务草稿包。
        /// </summary>
        public static VisualProxy<T> Meta<T>(
            this VisualProxy<T> proxy,
            MetaTrait meta)
            where T : IChartLayer
        {
            // 内部调用你已经写好的 PublishData
            return proxy.PublishData(meta);
        }

        /// <summary>
        /// 💥 一键发布纯动态资源元数据 (文字和颜色全是动态 Key)
        /// </summary>
        public static VisualProxy<T> MetaResource<T>(
            this VisualProxy<T> proxy,
            string groupName,
            string textKey,
            string brushKey, // 💥 顺应新架构，颜色也改用 Key
            string format = "G")
        where T : IChartLayer
        {
            // 调用 MetaTrait.cs 里定义的单例工厂
            return proxy.Meta(MetaTrait.SingleResource(groupName, textKey, brushKey, format));
        }

        /// <summary>
        /// 💥 一键发布纯静态字面量元数据 (文字写死，颜色静态)
        /// </summary>
        public static VisualProxy<T> MetaLiteral<T>(
            this VisualProxy<T> proxy,
            string name,
            IHevoBrush brush,
            string format = "G")
        where T : IChartLayer
        {
            // 调用 MetaTrait.cs 里定义的单例工厂
            return proxy.Meta(MetaTrait.SingleLiteral(name, brush, format));
        }
    }
}
