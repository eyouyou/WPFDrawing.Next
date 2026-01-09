
namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 💥 统一的生命周期宿主接口
    /// 任何实现该接口的对象，都具备接管并销毁 Rx 数据流的能力。
    /// </summary>
    public interface IDisposableHost
    {
        void RegisterDisposable(IDisposable disposable);
    }
}
