namespace PinionCore.Project2.Shared
{
    /// <summary>
    /// 標準角色的控制轉移表:Current = 狀態播放的動作、Playables = 可轉移白名單
    /// (含 locomotion 自身 = 重定向)、Next = 自然結束/停止的去向。
    /// 「攻擊中無法移動」即 BattleAttack.Playables 為空。
    /// 轉移資料不可變,World 持有單一實例供全部 PlayerController 共用;
    /// client(ActorShell)以同一份圖做表現預測(動作播完先行切到 Next,不等伺服器)。
    /// 權威閘門仍是伺服器端 Transition.Playables 白名單 —— 改這張圖不等於改權限,
    /// 兩端必須同 commit 重編以維持預測與權威一致。
    /// todo: 未來可改由外部配置(ScriptableObject)驅動,支援不同角色不同轉移表。
    /// </summary>
    public class StandardTransitionProvider
    {
        readonly System.Collections.Generic.Dictionary<ActionType, Transition> _Transitions;
        public readonly System.Collections.Generic.IReadOnlyDictionary<ActionType, Transition> Transitions;

        static PlayInfo _Play(ActionType action) => new PlayInfo { Action = action };

        public StandardTransitionProvider()
        {
            var adventureIdle = new Transition
            {
                Current = _Play(ActionType.AdventureIdle),
                Playables = new[]
                {
                    _Play(ActionType.AdventureWalk),
                    _Play(ActionType.BattleIdle),
                },
                Next = _Play(ActionType.AdventureIdle),
                Damage = _Play(ActionType.AdventureDamage),
            };

            var adventureWalk = new Transition
            {
                Current = _Play(ActionType.AdventureWalk),
                Playables = new[]
                {
                    _Play(ActionType.AdventureWalk),   // 自身 = 重定向
                    _Play(ActionType.AdventureIdle),
                    _Play(ActionType.BattleIdle),
                },
                Next = _Play(ActionType.AdventureIdle),
                Damage = _Play(ActionType.AdventureDamage),
            };

            var battleIdle = new Transition
            {
                Current = _Play(ActionType.BattleIdle),
                Playables = new[]
                {
                    _Play(ActionType.BattleAttack),
                    _Play(ActionType.BattleAttack0),
                    _Play(ActionType.GrabStart),
                    _Play(ActionType.BattleWalk),
                    _Play(ActionType.AdventureIdle),
                },
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            var battleWalk = new Transition
            {
                Current = _Play(ActionType.BattleWalk),
                Playables = new[]
                {
                    _Play(ActionType.BattleWalk),      // 自身 = 重定向
                    _Play(ActionType.BattleIdle),
                    _Play(ActionType.BattleAttack),
                    _Play(ActionType.BattleAttack0),
                    _Play(ActionType.GrabStart),
                },
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            var battleAttack = new Transition
            {
                Current = _Play(ActionType.BattleAttack),
                Playables = System.Array.Empty<PlayInfo>(),   // 攻擊中無法移動/再出招
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            var adventureDamage = new Transition
            {
                Current = _Play(ActionType.AdventureDamage),
                Playables = System.Array.Empty<PlayInfo>(),   // 受傷中無法移動/攻擊
                Next = _Play(ActionType.AdventureIdle),
                Damage = _Play(ActionType.AdventureDamage),   // 連續挨打:重進硬直(刷新)
            };

            var battleDamage = new Transition
            {
                Current = _Play(ActionType.BattleDamage),
                Playables = System.Array.Empty<PlayInfo>(),   // 受傷中無法移動/攻擊
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),      // 連續挨打:重進硬直(刷新)
            };

            var battleAttack0 = new Transition
            {
                Current = _Play(ActionType.BattleAttack0),
                Playables = new[]
                {
                    _Play(ActionType.BattleAttack0_0),                    
                },
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            var battleAttack0_0 = new Transition
            {
                Current = _Play(ActionType.BattleAttack0_0),
                Playables = new[]
                {
                    _Play(ActionType.BattleAttack0_0_0),
                    _Play(ActionType.BattleAttack0_0_1),
                },
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            var battleAttack0_0_0 = new Transition
            {
                Current = _Play(ActionType.BattleAttack0_0_0),
                Playables = System.Array.Empty<PlayInfo>(),   // 攻擊中無法移動/再出招
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            var battleAttack0_0_1 = new Transition
            {
                Current = _Play(ActionType.BattleAttack0_0_1),
                Playables = System.Array.Empty<PlayInfo>(),   // 攻擊中無法移動/再出招
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            // 抓取家族:A 側(抓取者)/B 側(被抓者)成對節點;配對建立/鏡射/解體由
            // Worlds.GrabResolver 驅動(A 側離開 grab 家族即解體 → 第三方打抓取者自動釋放被抓者)。
            var grabStart = new Transition
            {
                Current = _Play(ActionType.GrabStart),
                Playables = System.Array.Empty<PlayInfo>(),   // 起手中不可動;未命中自然播完回 idle
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            var grabIdleA = new Transition
            {
                Current = _Play(ActionType.GrabIdleA),
                Playables = new[]
                {
                    _Play(ActionType.GrabWalkA),
                    _Play(ActionType.GrabAtk1A),
                    _Play(ActionType.GrabThrowA),
                },
                Next = _Play(ActionType.GrabIdleA),
                Damage = _Play(ActionType.BattleDamage),   // 被打→硬直;GrabResolver 見離開家族即解體
            };

            var grabWalkA = new Transition
            {
                Current = _Play(ActionType.GrabWalkA),
                Playables = new[]
                {
                    _Play(ActionType.GrabWalkA),   // 自身 = 重定向
                    _Play(ActionType.GrabIdleA),   // client Stop = Play(Next) 的放行入口
                    _Play(ActionType.GrabAtk1A),
                    _Play(ActionType.GrabThrowA),
                },
                Next = _Play(ActionType.GrabIdleA),
                Damage = _Play(ActionType.BattleDamage),
            };

            var grabAtk1A = new Transition
            {
                Current = _Play(ActionType.GrabAtk1A),
                Playables = System.Array.Empty<PlayInfo>(),   // 補打中不可動
                Next = _Play(ActionType.GrabIdleA),
                Damage = _Play(ActionType.BattleDamage),
            };

            var grabThrowA = new Transition
            {
                Current = _Play(ActionType.GrabThrowA),
                Playables = System.Array.Empty<PlayInfo>(),   // 丟投起手即解除配對
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            var grabBreakA = new Transition
            {
                Current = _Play(ActionType.GrabBreakA),
                Playables = System.Array.Empty<PlayInfo>(),   // 被掙脫後搖;只由 GrabResolver force 進入
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            var grabIdleB = new Transition
            {
                Current = _Play(ActionType.GrabIdleB),
                Playables = new[]
                {
                    _Play(ActionType.GrabBreakB),   // 被抓者唯一可用動作:掙脫
                },
                Next = _Play(ActionType.GrabIdleB),
                Damage = _Play(ActionType.GrabAtk1B),   // 被抓中挨打(含抓取者補打)→受創反應,仍被抓
            };

            var grabWalkB = new Transition
            {
                Current = _Play(ActionType.GrabWalkB),
                Playables = new[]
                {
                    _Play(ActionType.GrabBreakB),   // 被拖行中仍可掙脫
                },
                Next = _Play(ActionType.GrabIdleB),
                Damage = _Play(ActionType.GrabAtk1B),
            };

            var grabAtk1B = new Transition
            {
                Current = _Play(ActionType.GrabAtk1B),
                Playables = System.Array.Empty<PlayInfo>(),
                Next = _Play(ActionType.GrabIdleB),
                Damage = _Play(ActionType.GrabAtk1B),   // 連續挨打:重進反應(刷新)
            };

            var grabThrowB = new Transition
            {
                Current = _Play(ActionType.GrabThrowB),
                Playables = System.Array.Empty<PlayInfo>(),   // 飛行中不可動
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),   // 配對已解除,照常受創
            };

            var grabBreakB = new Transition
            {
                Current = _Play(ActionType.GrabBreakB),
                Playables = System.Array.Empty<PlayInfo>(),
                Next = _Play(ActionType.BattleIdle),
                Damage = _Play(ActionType.BattleDamage),
            };

            _Transitions = new System.Collections.Generic.Dictionary<ActionType, Transition>
            {
                { ActionType.AdventureIdle, adventureIdle },
                { ActionType.AdventureWalk, adventureWalk },
                { ActionType.BattleIdle, battleIdle },
                { ActionType.BattleWalk, battleWalk },
                { ActionType.BattleAttack, battleAttack },
                { ActionType.AdventureDamage, adventureDamage },
                { ActionType.BattleDamage, battleDamage },
                { ActionType.BattleAttack0, battleAttack0 },
                { ActionType.BattleAttack0_0, battleAttack0_0 },
                { ActionType.BattleAttack0_0_0, battleAttack0_0_0 },
                { ActionType.BattleAttack0_0_1, battleAttack0_0_1 },
                { ActionType.GrabStart, grabStart },
                { ActionType.GrabIdleA, grabIdleA },
                { ActionType.GrabWalkA, grabWalkA },
                { ActionType.GrabAtk1A, grabAtk1A },
                { ActionType.GrabThrowA, grabThrowA },
                { ActionType.GrabBreakA, grabBreakA },
                { ActionType.GrabIdleB, grabIdleB },
                { ActionType.GrabWalkB, grabWalkB },
                { ActionType.GrabAtk1B, grabAtk1B },
                { ActionType.GrabThrowB, grabThrowB },
                { ActionType.GrabBreakB, grabBreakB },
            };
            Transitions = _Transitions;
        }
    }
}
