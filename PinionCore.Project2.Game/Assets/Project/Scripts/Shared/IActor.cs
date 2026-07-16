namespace PinionCore.Project2.Shared
{
    // 表現狀態分類(client 端動畫組切換/輸入 gating 用):
    // 不再獨立過線 —— 由 ActionConfig.Stance 查表(資產與伺服器同一份)
    public enum StanceType
    {
        Adventure,
        Battle,
    }
    // 角色的名稱 移動 等外觀資訊
    // 曝光給 client 的資訊
    public interface IActor : IIdentity, Remote.Protocolable
    {


        PinionCore.Remote.Property<string> DisplayName { get; }
        PinionCore.Remote.Property<string> ModelName { get; }

        // 位置與朝向完全由 MoveEvent 推導:訂閱時 replay 當下 MoveInfo(駐留或移動中),
        // 以 StartTicks + world time 取樣即得任意時刻狀態,故不需要 Position 屬性。
        event System.Action<MoveInfo> MoveEvent;

        // 自帶位移動作(idle/走路/攻擊)的播放狀態:訂閱時 replay 當下 ActionInfo,
        // None 只短暫出現在 Cast 播完到下一狀態之間;位移本身仍走 MoveEvent(分段等速直線)。
        // 表現狀態(冒險/戰鬥)也由 ActionType 推導,不另設事件。
        event System.Action<ActionInfo> ActionEvent;

    }
}
