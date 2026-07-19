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
        BattleAttack0 = 8,          // 連擊:可以接續攻擊/攻擊中無法移動
        BattleAttack0_0 = 9,          // 連擊:攻擊中無法移動/攻擊
        BattleAttack0_0_0 = 10,          // 連擊:攻擊中無法移動/攻擊
        BattleAttack0_0_1 = 11,          // 連擊:攻擊中無法移動/攻擊

        // 抓取家族:A = 抓取者、B = 被抓者(成對動畫);配對生命週期由 Worlds.GrabResolver 管理
        GrabStart = 12,             // 抓取起手:帶 HitEffect=Grab 的命中窗,命中即配對雙方進 GrabIdleA/B
        GrabIdleA = 13,             // 抓取者抓住循環
        GrabIdleB = 14,             // 被抓者被抓循環(白名單只剩 GrabBreakB)
        GrabWalkA = 15,             // 抓取者拖行走路(locomotion,可重定向)
        GrabWalkB = 16,             // 被抓者被拖行(位置由 GrabResolver 轉發驅動)
        GrabAtk1A = 17,             // 抓取中補打
        GrabAtk1B = 18,             // 被抓者受創反應(經 Damage 路由觸發,非 mirror)
        GrabThrowA = 19,            // 丟投(起手即解除配對)
        GrabThrowB = 20,            // 被丟投飛行(真烘焙 root motion)
        GrabBreakA = 21,            // 被掙脫的後搖(只由 GrabResolver force 進入)
        GrabBreakB = 22,            // 掙脫(被抓者白名單唯一入口)
    }
}
