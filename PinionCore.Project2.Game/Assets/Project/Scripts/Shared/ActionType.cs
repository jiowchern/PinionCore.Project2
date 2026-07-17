namespace PinionCore.Project2.Shared
{
    // 動作型別即控制狀態機的轉移圖節點識別(ControllerStatus / StandardTransitionProvider);
    // 顯式數值:資產(ActionConfig.Action)存 int,插入新成員不得位移既有值。
    // 動作的能力/權限與表現規則(循環/重定向/打斷/凍結旋轉/stance)由 ActionConfig 欄位承載,
    // client 以 ActionType 查同一份資產取得,不再從型別名推導。
    public enum ActionType
    {
        None = 0,           // 哨兵:初始狀態與動作結束後的 replay 值
        BattleAttack = 1,
        AdventureWalk = 2,
        AdventureIdle = 3,
        BattleIdle = 4,
        BattleWalk = 5,
        AdventureDamage = 6,        // 受傷中無法移動/攻擊
        BattleDamage = 7,           // 受傷中無法移動/攻擊
    }
}
