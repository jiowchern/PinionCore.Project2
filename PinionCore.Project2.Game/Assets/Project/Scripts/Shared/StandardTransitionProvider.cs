namespace PinionCore.Project2.Shared
{
    /// <summary>
    /// 標準角色的控制轉移表:Current = 狀態播放的動作、Playables = 可轉移白名單
    /// (含 locomotion 自身 = 重定向)、Next = 自然結束/停止的去向。
    /// 「攻擊中無法移動」即 UnarmedAttack.Playables 為空。
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
                    _Play(ActionType.UnarmedIdle),
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
                    _Play(ActionType.UnarmedIdle),
                },
                Next = _Play(ActionType.AdventureIdle),
                Damage = _Play(ActionType.AdventureDamage),
            };

            var battleIdle = new Transition
            {
                Current = _Play(ActionType.UnarmedIdle),
                Playables = new[]
                {
                    _Play(ActionType.UnarmedAttack),
                    _Play(ActionType.UnarmedAttack0),
                    _Play(ActionType.UnarmedGrabStart),
                    _Play(ActionType.UnarmedWalk),
                    _Play(ActionType.AdventureIdle),
                },
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var battleWalk = new Transition
            {
                Current = _Play(ActionType.UnarmedWalk),
                Playables = new[]
                {
                    _Play(ActionType.UnarmedWalk),      // 自身 = 重定向
                    _Play(ActionType.UnarmedIdle),
                    _Play(ActionType.UnarmedAttack),
                    _Play(ActionType.UnarmedAttack0),
                    _Play(ActionType.UnarmedGrabStart),
                },
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var battleAttack = new Transition
            {
                Current = _Play(ActionType.UnarmedAttack),
                Playables = System.Array.Empty<PlayInfo>(),   // 攻擊中無法移動/再出招
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
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
                Current = _Play(ActionType.UnarmedDamage),
                Playables = System.Array.Empty<PlayInfo>(),   // 受傷中無法移動/攻擊
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),      // 連續挨打:重進硬直(刷新)
            };

            var battleAttack0 = new Transition
            {
                Current = _Play(ActionType.UnarmedAttack0),
                Playables = new[]
                {
                    _Play(ActionType.UnarmedAttack0_0),                    
                },
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var battleAttack0_0 = new Transition
            {
                Current = _Play(ActionType.UnarmedAttack0_0),
                Playables = new[]
                {
                    _Play(ActionType.UnarmedAttack0_0_0),
                    _Play(ActionType.UnarmedAttack0_0_1),
                },
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var battleAttack0_0_0 = new Transition
            {
                Current = _Play(ActionType.UnarmedAttack0_0_0),
                Playables = System.Array.Empty<PlayInfo>(),   // 攻擊中無法移動/再出招
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var battleAttack0_0_1 = new Transition
            {
                Current = _Play(ActionType.UnarmedAttack0_0_1),
                Playables = System.Array.Empty<PlayInfo>(),   // 攻擊中無法移動/再出招
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            // 抓取家族:A 側(抓取者)/B 側(被抓者)成對節點;配對建立/鏡射/解體由
            // Worlds.GrabResolver 驅動(A 側離開 grab 家族即解體 → 第三方打抓取者自動釋放被抓者)。
            var grabStart = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabStart),
                Playables = System.Array.Empty<PlayInfo>(),   // 起手中不可動;未命中自然播完回 idle
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var grabIdleA = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabIdleA),
                Playables = new[]
                {
                    _Play(ActionType.UnarmedGrabWalkA),
                    _Play(ActionType.UnarmedGrabAtk1A),
                    _Play(ActionType.UnarmedGrabThrowA),
                },
                Next = _Play(ActionType.UnarmedGrabIdleA),
                Damage = _Play(ActionType.UnarmedDamage),   // 被打→硬直;GrabResolver 見離開家族即解體
            };

            var grabWalkA = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabWalkA),
                Playables = new[]
                {
                    _Play(ActionType.UnarmedGrabWalkA),   // 自身 = 重定向
                    _Play(ActionType.UnarmedGrabIdleA),   // client Stop = Play(Next) 的放行入口
                    _Play(ActionType.UnarmedGrabAtk1A),
                    _Play(ActionType.UnarmedGrabThrowA),
                },
                Next = _Play(ActionType.UnarmedGrabIdleA),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var grabAtk1A = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabAtk1A),
                Playables = System.Array.Empty<PlayInfo>(),   // 補打中不可動
                Next = _Play(ActionType.UnarmedGrabIdleA),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var grabThrowA = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabThrowA),
                Playables = System.Array.Empty<PlayInfo>(),   // 丟投起手即解除配對
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var grabBreakA = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabBreakA),
                Playables = System.Array.Empty<PlayInfo>(),   // 被掙脫後搖;只由 GrabResolver force 進入
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            var grabIdleB = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabIdleB),
                Playables = new[]
                {
                    _Play(ActionType.UnarmedGrabBreakB),   // 被抓者唯一可用動作:掙脫
                },
                Next = _Play(ActionType.UnarmedGrabIdleB),
                Damage = _Play(ActionType.UnarmedGrabAtk1B),   // 被抓中挨打(含抓取者補打)→受創反應,仍被抓
            };

            var grabWalkB = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabWalkB),
                Playables = new[]
                {
                    _Play(ActionType.UnarmedGrabBreakB),   // 被拖行中仍可掙脫
                },
                Next = _Play(ActionType.UnarmedGrabIdleB),
                Damage = _Play(ActionType.UnarmedGrabAtk1B),
            };

            var grabAtk1B = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabAtk1B),
                Playables = System.Array.Empty<PlayInfo>(),
                Next = _Play(ActionType.UnarmedGrabIdleB),
                Damage = _Play(ActionType.UnarmedGrabAtk1B),   // 連續挨打:重進反應(刷新)
            };

            var grabThrowB = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabThrowB),
                Playables = System.Array.Empty<PlayInfo>(),   // 飛行中不可動
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),   // 配對已解除,照常受創
            };

            var grabBreakB = new Transition
            {
                Current = _Play(ActionType.UnarmedGrabBreakB),
                Playables = System.Array.Empty<PlayInfo>(),
                Next = _Play(ActionType.UnarmedIdle),
                Damage = _Play(ActionType.UnarmedDamage),
            };

            _Transitions = new System.Collections.Generic.Dictionary<ActionType, Transition>
            {
                { ActionType.AdventureIdle, adventureIdle },
                { ActionType.AdventureWalk, adventureWalk },
                { ActionType.UnarmedIdle, battleIdle },
                { ActionType.UnarmedWalk, battleWalk },
                { ActionType.UnarmedAttack, battleAttack },
                { ActionType.AdventureDamage, adventureDamage },
                { ActionType.UnarmedDamage, battleDamage },
                { ActionType.UnarmedAttack0, battleAttack0 },
                { ActionType.UnarmedAttack0_0, battleAttack0_0 },
                { ActionType.UnarmedAttack0_0_0, battleAttack0_0_0 },
                { ActionType.UnarmedAttack0_0_1, battleAttack0_0_1 },
                { ActionType.UnarmedGrabStart, grabStart },
                { ActionType.UnarmedGrabIdleA, grabIdleA },
                { ActionType.UnarmedGrabWalkA, grabWalkA },
                { ActionType.UnarmedGrabAtk1A, grabAtk1A },
                { ActionType.UnarmedGrabThrowA, grabThrowA },
                { ActionType.UnarmedGrabBreakA, grabBreakA },
                { ActionType.UnarmedGrabIdleB, grabIdleB },
                { ActionType.UnarmedGrabWalkB, grabWalkB },
                { ActionType.UnarmedGrabAtk1B, grabAtk1B },
                { ActionType.UnarmedGrabThrowB, grabThrowB },
                { ActionType.UnarmedGrabBreakB, grabBreakB },
            };
            Transitions = _Transitions;
        }
    }
}
