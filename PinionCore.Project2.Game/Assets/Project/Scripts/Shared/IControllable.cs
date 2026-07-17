using PinionCore.Remote;

namespace PinionCore.Project2.Shared
{
    // 轉移圖節點識別:目前只有動作型別,保留 struct 供日後擴充(冷卻等)
    public struct PlayInfo
    {
        public ActionType Action;
    }

    // 控制狀態的過線描述:Current = 本狀態播放中的動作、
    // Playables = 可轉移白名單(含 locomotion 自身 = 重定向)、
    // Next = 自然結束/停止的去向(Cast 播完自動轉移;client 的「停止」= Play(Next))
    public struct Transition
    {
        public uint Id;
        public PlayInfo Current;
        public PlayInfo[] Playables;
        public PlayInfo Next;
        public PlayInfo Damage;
    }

    // 單一控制能力:world 端 PlayerController 自身即 soul,與角色同生命週期
    //(進世界供應、離開收回);狀態轉移經 TransitionEvent 廣播,
    // soul 端 add 即回放當前 Transition(晚訂閱安全)。
    // 「動作能不能執行」由 Playables 白名單天然表達(如攻擊中無法移動)。
    public interface IControllable : Protocolable
    {
        // todo 未來這邊改成 Transition.Id
        event System.Action<Transition> TransitionEvent;

        // direction 為世界座標 XZ 方向(x=+X、y=+Z),只有走路類動作使用:
        // Play(走路, dir) 起走;走路中同型別再 Play = 重定向(吃 MoveAcceptInterval 節流);
        // 非位移動作忽略 direction。不在 Playables(且非重定向)一律回 false。
        PinionCore.Remote.Value<bool> Play(ActionType name, UnityEngine.Vector2 direction);
    }
}
