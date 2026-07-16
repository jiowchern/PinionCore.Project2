namespace PinionCore.Project2.Shared
{
    // 動作型別即控制狀態機的轉移圖節點識別(ControllerStatus / StandardTransitionProvider);
    // 顯式數值:資產(ActionConfig.Action)存 int,插入新成員不得位移既有值。
    public enum ActionType
    {
        None = 0,           // 無動作進行中(僅 Cast 播完到下一狀態 Enter 之間短暫出現)
        BattleAttack = 1,
        AdventureWalk = 2,
        AdventureIdle = 3,
        BattleIdle = 4,
        BattleWalk = 5,
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

    // ActionType → 類別/表現狀態的 client 端對照:ActionConfig(含 Category)不過線,
    // client 只拿得到 ActionInfo,表現規則(旋轉凍結/攻擊鎖/動畫組切換)需要據此分流。
    // ActionType 自帶 stance 語意,StanceType 不再獨立過線(StanceEvent 已拆除),一律由此推導。
    public static class ActionTypeExtensions
    {
        public static bool IsLocomotion(this ActionType type) =>
            type == ActionType.AdventureWalk || type == ActionType.AdventureIdle ||
            type == ActionType.BattleWalk || type == ActionType.BattleIdle;

        public static StanceType StanceOf(this ActionType type)
        {
            switch (type)
            {
                case ActionType.BattleAttack:
                case ActionType.BattleIdle:
                case ActionType.BattleWalk:
                    return StanceType.Battle;
                default:
                    return StanceType.Adventure;
            }
        }
    }
}
