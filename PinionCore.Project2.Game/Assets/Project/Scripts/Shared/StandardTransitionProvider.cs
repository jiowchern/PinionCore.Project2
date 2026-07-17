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

            _Transitions = new System.Collections.Generic.Dictionary<ActionType, Transition>
            {
                { ActionType.AdventureIdle, adventureIdle },
                { ActionType.AdventureWalk, adventureWalk },
                { ActionType.BattleIdle, battleIdle },
                { ActionType.BattleWalk, battleWalk },
                { ActionType.BattleAttack, battleAttack },
                { ActionType.AdventureDamage, adventureDamage },
                { ActionType.BattleDamage, battleDamage },
            };
            Transitions = _Transitions;
        }
    }
}
