namespace PinionCore.Project2.Shared
{
    public enum ActionType
    {
        None = 0,       // 無動作進行中
        Attack = 1,
    }

    // 動作播放描述:過線的只有這顆(分段位移資料留在伺服器端 ActionConfig,不序列化);
    // 晚訂閱者以 (world time - StartTicks) 算動畫偏移,Action == None 即動作已結束。
    public struct ActionInfo
    {
        public ActionType Action;
        public long StartTicks;      // 伺服器時間戳(TimeSpan ticks)
    }
}
