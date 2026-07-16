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
        public PlayInfo Current;
        public PlayInfo[] Playables;
        public PlayInfo Next;
    }

    // 單一控制能力:world 端每個控制狀態(ControllerStatus)即一顆 soul,
    // 供應時的 Transition 固定不變,狀態轉移 = unsupply 舊 soul + supply 新 soul。
    // 「動作能不能執行」由 Playables 白名單天然表達(如攻擊中無法移動)。
    public interface IControllable : Protocolable
    {
        PinionCore.Remote.Property<Transition> Transition { get; }

        // direction 為世界座標 XZ 方向(x=+X、y=+Z),只有走路類動作使用:
        // Play(走路, dir) 起走;走路中同型別再 Play = 重定向(吃 MoveAcceptInterval 節流);
        // 非位移動作忽略 direction。不在 Playables(且非重定向)一律回 false。
        PinionCore.Remote.Value<bool> Play(ActionType name, UnityEngine.Vector2 direction);
    }
}
