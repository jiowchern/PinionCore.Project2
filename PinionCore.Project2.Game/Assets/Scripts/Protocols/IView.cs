namespace PinionCore.Project2.Protocols
{
    /// <summary>
    /// 抽象的視圖介面
    /// 提供外部使用者查詢視圖資訊。
    /// </summary>
    public interface IView : Remote.Protocolable
    {
        Remote.Property<string> Name { get; }
    }
}
