using UnityEngine;


namespace PinionCore.Project2.Shared
{
    public interface IPlayer : IActor
    {
        // direction 為角色區域座標的相對方向:y=前方、x=右方。
        // 偏移角以比例式換算成角速度(偏移角/秒),角色沿鼻子方向前進並持續偏轉,
        // 直到下一個 Move 或 Stop。零向量回傳 false;要停止用 Stop()。
        PinionCore.Remote.Value<bool> Move(Vector2 direction);

        PinionCore.Remote.Value<bool> Stop();

    }
}
