namespace PinionCore.Project2.Shared
{
    public enum ActionType
    {
        None = 0,       // 無動作進行中
        Attack = 1,
        Walk = 2,
    }

    // 動作類別:Locomotion 隱含四個不變量 —— 循環播放、可被 Cast 打斷、
    // 可被新 Move 重定向、可被 Stop 結束;Cast 為一次性固定長度(現行攻擊語意)。
    public enum ActionCategory
    {
        Cast = 0,
        Locomotion = 1,
    }

    // 動作播放描述:過線的只有這顆(分段位移資料留在伺服器端 ActionConfig,不序列化);
    // 晚訂閱者以 (world time - StartTicks) 算動畫偏移,Action == None 即動作已結束。
    public struct ActionInfo
    {
        public ActionType Action;
        public long StartTicks;      // 伺服器時間戳(TimeSpan ticks)
    }

    // ActionType → 類別的 client 端對照:ActionConfig(含 Category)不過線,
    // client 只拿得到 ActionInfo,表現規則(旋轉凍結/攻擊鎖)需要據此分流
    public static class ActionTypes
    {
        public static bool IsLocomotion(ActionType type) => type == ActionType.Walk;
    }
}
