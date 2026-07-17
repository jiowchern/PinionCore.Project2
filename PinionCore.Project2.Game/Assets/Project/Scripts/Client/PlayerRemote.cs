using PinionCore.NetSync.UniRx;
using System;
using UniRx;
using UnityEngine;

namespace PinionCore.Project2.Client
{
    public class PlayerRemote : MonoBehaviour
    {
        // 抽象為 QueryerHost:可掛 Client(直連)或 GatewayClient(經 Router)
        public PinionCore.NetSync.QueryerHost GatewayClient;


        readonly UniRx.CompositeDisposable _Disposable;
        // 攻擊用獨立容器:與 Move/Stop 共用 Clear 會取消在途移動回應,
        // 害 PlayerInputHandler 的在途鎖永久卡死
        readonly UniRx.CompositeDisposable _AttackDisposable;
        // 姿態切換同理,獨立容器不與攻擊互相取消
        readonly UniRx.CompositeDisposable _StanceDisposable;

        // 最新 Transition 快取:IControllable 與角色同生命週期(soul 恆存),
        // 首次指令時常駐訂閱一次 TransitionEvent(soul 端 add 即回放當前值,之後每次轉移推播),
        // 指令從快取讀當前狀態,不必每個指令多等一個網路往返
        readonly UniRx.ReplaySubject<Shared.Transition> _TransitionCache;
        System.IDisposable _TransitionFeed;

        public PlayerRemote()
        {
            _Disposable = new UniRx.CompositeDisposable();
            _AttackDisposable = new UniRx.CompositeDisposable();
            _StanceDisposable = new UniRx.CompositeDisposable();
            _TransitionCache = new UniRx.ReplaySubject<Shared.Transition>(1);
        }


        // direction 為世界座標 XZ 方向:x=+X、y=+Z
        public void Move(Vector2 direction)
        {
            Move(direction, null);
        }

        // responded:收到伺服器回傳值(接受與否)時回呼;
        // 下一個指令的 Clear 會取消未回應的訂閱,屆時不回呼(視同掉失,由上層逾時處理)。
        // 走路型別依當前 Transition 的表現側選:戰鬥系狀態走 BattleWalk,否則 AdventureWalk;
        // 已在走路狀態時同型別 Play = 重定向(伺服器端節流)。
        public void Move(Vector2 direction, Action<bool> responded)
        {
            // Clear 而非 Dispose:Dispose 後的 CompositeDisposable 會立刻銷毀之後 Add 的訂閱
            _Disposable.Clear();

            // Take(1):Supply/快取會重播既有狀態,一次 Move 只發一次 RPC,
            // 不讓訂閱殘留到未來的 re-supply/轉移重發 Move
            var obs = from controllable in _Controllables().Take(1)
                      from transition in _Transitions().Take(1)
                      from result in controllable.Play(_WalkOf(transition.Current.Action), direction).RemoteValue().DoOnError(_Error)
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _Disposable.Add(disp);
        }

        private void _Error(Exception exception)
        {
            UnityEngine.Debug.Log(exception);
        }

        public void Stop()
        {
            Stop(null);
        }

        // 停止 = 轉移到 Transition.Next(走路的自然結束去向 = 該表現側的 idle);
        // 已駐留(idle 的 Next = 自身,不在白名單)時回 false,與舊 Stop 駐留語意一致
        public void Stop(Action<bool> responded)
        {
            _Disposable.Clear();

            var obs = from controllable in _Controllables().Take(1)
                      from transition in _Transitions().Take(1)
                      from result in controllable.Play(transition.Next.Action, Vector2.zero).RemoteValue().DoOnError(_Error)
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _Disposable.Add(disp);
        }

        // 姿態切換:目標 = 當前 Transition.Playables 中「另一側的 idle」,由 ActionConfig
        // 能力欄位判定(idle = Loop 且非 Redirectable、側別 = Stance),不硬編碼 ActionType;
        // findAction 由呼叫端提供(handler 傳 ActorShell.FindAction,與伺服器同一份資產)。
        // 白名單查無目標(如 BattleWalk 不含另一側 idle)不發 RPC,直接回 false
        public void SwitchStance(Func<Shared.ActionType, Shared.ActionConfig> findAction, Action<bool> responded)
        {
            _StanceDisposable.Clear();

            var obs = (from controllable in _Controllables().Take(1)
                       from transition in _Transitions().Take(1)
                       select new { controllable, transition })
                      .SelectMany(ct =>
                      {
                          var target = _SwitchStanceTargetOf(ct.transition, findAction);
                          if (target == Shared.ActionType.None)
                              return Observable.Return(false);
                          return ct.controllable.Play(target, Vector2.zero).RemoteValue().DoOnError(_Error);
                      });
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _StanceDisposable.Add(disp);
        }

        // 出招:BattleAttack 只在戰鬥系狀態的 Playables 白名單內,冒險態下伺服器回 false
        public void Attack(Action<bool> responded)
        {
            _AttackDisposable.Clear();

            var obs = from controllable in _Controllables().Take(1)
                      from result in controllable.Play(Shared.ActionType.BattleAttack, Vector2.zero).RemoteValue().DoOnError(_Error)
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _AttackDisposable.Add(disp);
        }

        // 統一入口:只 query IUserEntry,IControllable 沿合約鏈(entry.Games → game.Player → player.Controllable)取得;
        // 供應由 world 端開關(soul 與角色同生命週期,離開世界才 unsupply;
        // 未供應時此流不發射,指令由上層逾時處理)。
        IObservable<Shared.IControllable> _Controllables()
        {
            return from entry in GatewayClient.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                   from game in entry.Games.SupplyEvent()
                   from player in game.Player.SupplyEvent()
                   from controllable in player.Controllable.SupplyEvent()
                   select controllable;
        }

        // 最新 Transition 流:首次呼叫啟動常駐訂閱(涵蓋 re-supply),之後快取命中立即發射;
        // 首個指令仍需等一次回放(晚一個網路往返)。快取可能落後伺服器一個轉移,
        // 沒關係:Play 一律由伺服器以當下白名單驗證
        IObservable<Shared.Transition> _Transitions()
        {
            if (_TransitionFeed == null)
            {
                _TransitionFeed = _Controllables()
                    .SelectMany(c => UniRx.Observable.FromEvent<Shared.Transition>(
                        h => c.TransitionEvent += h, h => c.TransitionEvent -= h))
                    .Subscribe(_TransitionCache.OnNext);
            }
            return _TransitionCache;
        }

        // 白名單中「idle(Loop 且非 Redirectable)且 Stance 與當前不同」的動作;
        // 需比對 Stance:走路狀態的白名單同時含兩側 idle(同側 = Stop 的去向),只挑另一側。
        // 查無 config 或無目標回 None
        static Shared.ActionType _SwitchStanceTargetOf(Shared.Transition transition, Func<Shared.ActionType, Shared.ActionConfig> findAction)
        {
            var current = findAction(transition.Current.Action);
            if (current == null)
                return Shared.ActionType.None;

            foreach (var playable in transition.Playables)
            {
                var config = findAction(playable.Action);
                if (config != null && config.Loop && !config.Redirectable && config.Stance != current.Stance)
                    return playable.Action;
            }
            return Shared.ActionType.None;
        }

        // 當前狀態 → 對應的走路動作:戰鬥系 → BattleWalk、冒險系 → AdventureWalk
        static Shared.ActionType _WalkOf(Shared.ActionType current)
        {
            switch (current)
            { 
                case Shared.ActionType.BattleIdle:
                case Shared.ActionType.BattleWalk:
                case Shared.ActionType.BattleAttack:
                    return Shared.ActionType.BattleWalk;
                default:
                    return Shared.ActionType.AdventureWalk;
            }
        }

        void OnDestroy()
        {
            _Disposable.Dispose();
            _AttackDisposable.Dispose();
            _StanceDisposable.Dispose();
            _TransitionFeed?.Dispose();
            _TransitionCache.Dispose();
        }
    }
}
