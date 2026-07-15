using PinionCore.Utility;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 管理單一 Player 的角色狀態機(GameProject1 Play 模式):狀態決定曝光給 client 的能力介面,
    /// 轉換為 world 內部直接呼叫、不過傳輸協議,於下一次 Update 生效。
    /// Player 只保留協議面與移動/碰撞/動作排程;狀態編排與未來戰鬥管線(死亡/僵直)的觸發集中在此。
    /// </summary>
    internal class PlayerController
    {
        public readonly Player Player;

        readonly StatusMachine _StatusMachine;

        public PlayerController(Player player)
        {
            Player = player;
            _StatusMachine = new StatusMachine();

            // 進世界即有意識;首次 Update 進入狀態並供應 IMoveable(Notifier 有 replay,晚訂閱安全)
            ToConscious();
        }

        /// <summary>回到有意識:恢復供應 IMoveable 與 Adventure/Battle 子狀態(進入即冒險態)。</summary>
        internal void ToConscious()
        {
            _StatusMachine.Push(new Statuses.ConsciousStatus(Player));
        }

        /// <summary>進入無意識(僵直/死亡等,未來由戰鬥管線觸發):收回全部能力供應。</summary>
        internal void ToUnconscious()
        {
            _StatusMachine.Push(new Statuses.UnconsciousStatus());
        }

        /// <summary>由 World.Update 每幀驅動:先推進狀態機(能力供應開關),再讓 Player 投影權威狀態。</summary>
        internal void Update()
        {
            _StatusMachine.Update();
            Player.Update();
        }

        /// <summary>離開世界:結束當前狀態(Leave 收回能力供應),讓 Unsupply 先於根解綁送達。</summary>
        internal void Shutdown()
        {
            _StatusMachine.Termination();
        }
    }
}
