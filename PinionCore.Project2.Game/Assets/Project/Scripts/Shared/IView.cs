namespace PinionCore.Project2.Shared
{
    /// <summary>
    /// 抽象的視圖介面
    /// 提供外部使用者查詢視圖資訊。
    /// </summary>    
    public interface IView : Remote.Protocolable
    {
        event System.Action<long> TimeTicksEvent; // 地圖產生開始的時間戳記。 
        Remote.Property<string> Name { get; } // 地圖名稱
    }
}
