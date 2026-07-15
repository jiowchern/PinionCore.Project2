using PinionCore.NetSync.Gateways;
using PinionCore.NetSync.UniRx;
using System;
using System.Linq;
using UniRx;
using UnityEngine;

namespace PinionCore.Project2.Client
{
    public class Player :MonoBehaviour
    {        
        // 抽象為 QueryerHost:可掛 Client(直連)或 GatewayClient(經 Router)
        public PinionCore.NetSync.QueryerHost GatewayClient;


        readonly UniRx.CompositeDisposable _Disposable;
        // 攻擊用獨立容器:與 Move/Stop 共用 Clear 會取消在途移動回應,
        // 害 PlayerInputHandler 的在途鎖永久卡死
        readonly UniRx.CompositeDisposable _AttackDisposable;
        public Player()
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
        // 下一個指令的 Clear 會取消未回應的訂閱,屆時不回呼(視同掉失,由上層逾時處理)
        public void Move(Vector2 direction, Action<bool> responded)
        {
            // Clear 而非 Dispose:Dispose 後的 CompositeDisposable 會立刻銷毀之後 Add 的訂閱
            _Disposable.Clear();

            // Take(1):Supply 會重播既有的 IMoveable,一次 Move 只發一次 RPC,
            // 不讓訂閱殘留到未來的 re-supply 重發 Move
            var obs = from moveable in _Moveables().Take(1)
                      from result in moveable.Move(direction).RemoteValue()
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _Disposable.Add(disp);
        }

        public void Stop()
        {
            Stop(null);
        }

        public void Stop(Action<bool> responded)
        {
            _Disposable.Clear();

            var obs = from moveable in _Moveables().Take(1)
                      from result in moveable.Stop().RemoteValue()
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _Disposable.Add(disp);
        }

        // 出招:IBattle 只在戰鬥狀態被供應,冒險態下訂閱不會發射(由上層以逾時處理)
        public void Attack(Action<bool> responded)
        {
            _AttackDisposable.Clear();

            var obs = from battle in _Battles().Take(1)
                      from result in battle.Attack().RemoteValue()
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _AttackDisposable.Add(disp);
        }

        // 統一入口:只 query IUserEntry,IMoveable 沿合約鏈(entry.Games → game.Player → player.Moveable)取得;
        // 供應由 world 端角色狀態機開關(無意識時 unsupply,此流不發射,Move 由上層逾時處理)
        IObservable<Shared.IMoveable> _Moveables()
        {
            return from entry in GatewayClient.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                   from game in entry.Games.SupplyEvent()
                   from player in game.Player.SupplyEvent()
                   from moveable in player.Moveable.SupplyEvent()
                   select moveable;
        }

        // IBattle 只在戰鬥狀態被供應(world 端子狀態互斥開關),沿 game.Player → player.Battle 取得
        IObservable<Shared.IBattle> _Battles()
        {
            return from entry in GatewayClient.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                   from game in entry.Games.SupplyEvent()
                   from player in game.Player.SupplyEvent()
                   from battle in player.Battle.SupplyEvent()
                   select battle;
        }

        void OnDestroy()
        {
            _Disposable.Dispose();
            _AttackDisposable.Dispose();
        }
    }
}
