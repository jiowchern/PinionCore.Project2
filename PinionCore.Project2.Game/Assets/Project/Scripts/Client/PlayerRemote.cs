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
        public PlayerRemote()
        {
            _Disposable = new UniRx.CompositeDisposable();
            _AttackDisposable = new UniRx.CompositeDisposable();
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

            // Take(1):Supply 會重播既有的 IControllable,一次 Move 只發一次 RPC,
            // 不讓訂閱殘留到未來的 re-supply 重發 Move
            var obs = from controllable in _Controllables().Take(1)
                      from result in controllable.Play(_WalkOf(controllable.Transition.Value.Current.Action), direction).RemoteValue()
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _Disposable.Add(disp);
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
                      from result in controllable.Play(controllable.Transition.Value.Next.Action, Vector2.zero).RemoteValue()
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _Disposable.Add(disp);
        }

        // 出招:BattleAttack 只在戰鬥系狀態的 Playables 白名單內,冒險態下伺服器回 false
        public void Attack(Action<bool> responded)
        {
            _AttackDisposable.Clear();

            var obs = from controllable in _Controllables().Take(1)
                      from result in controllable.Play(Shared.ActionType.BattleAttack, Vector2.zero).RemoteValue()
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _AttackDisposable.Add(disp);
        }

        // 統一入口:只 query IUserEntry,IControllable 沿合約鏈(entry.Games → game.Player → player.Controllable)取得;
        // 供應由 world 端控制狀態機開關(無意識時 unsupply,此流不發射,指令由上層逾時處理)。
        // 每狀態一顆 soul:轉移瞬間發往舊 soul 的 Play 會被伺服器靜默丟棄(無回應),同樣由逾時吸收。
        IObservable<Shared.IControllable> _Controllables()
        {
            return from entry in GatewayClient.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                   from game in entry.Games.SupplyEvent()
                   from player in game.Player.SupplyEvent()
                   from controllable in player.Controllable.SupplyEvent()
                   select controllable;
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
        }
    }
}
