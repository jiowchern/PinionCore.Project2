namespace PinionCore.Project2.Protocols
{
    /// <summary>
    /// 協議進入點介面。伺服器透過 IBinder 綁定此介面的實作，
    /// 客戶端透過 IAgent 取得對應的 Ghost 代理後即可遠端呼叫。
    /// </summary>
    public interface IEntry
    {
        // 在此定義遠端方法、事件、屬性或 Notifier，例如：
        //
        // PinionCore.Remote.Value<string> Echo(string message);
        //
        // event System.Action OnReady;
        //
        // PinionCore.Remote.Property<int> Count { get; }
        //
        // PinionCore.Remote.Notifier<ISomeObject> Objects { get; }
    }
}
