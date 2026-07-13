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
        public Player()
        {
            _Disposable = new UniRx.CompositeDisposable();
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

            // Take(1):Supply 會重播既有的 IPlayer,一次 Move 只發一次 RPC,
            // 不讓訂閱殘留到未來的 re-supply 重發 Move
            var obs = from player in GatewayClient.Queryer.QueryNotifier<Shared.ICharactor>().SupplyEvent().Take(1)
                      from result in player.Move(direction).RemoteValue()
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

            var obs = from player in GatewayClient.Queryer.QueryNotifier<Shared.ICharactor>().SupplyEvent().Take(1)
                      from result in player.Stop().RemoteValue()
                      select result;
            var disp = obs.Subscribe(result => responded?.Invoke(result));
            _Disposable.Add(disp);
        }

        void OnDestroy()
        {
            _Disposable.Dispose();
        }
    }
}
