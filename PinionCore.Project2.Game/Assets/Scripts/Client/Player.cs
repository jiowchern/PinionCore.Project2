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
        public GatewayClient GatewayClient;


        readonly UniRx.CompositeDisposable _Disposable;
        public Player()
        {
            _Disposable = new UniRx.CompositeDisposable();
        }


        // direction 為角色區域座標的相對方向:y=前方、x=右方
        public void Move(Vector2 direction)
        {
            // Clear 而非 Dispose:Dispose 後的 CompositeDisposable 會立刻銷毀之後 Add 的訂閱
            _Disposable.Clear();

            // Take(1):Supply 會重播既有的 IPlayer,一次 Move 只發一次 RPC,
            // 不讓訂閱殘留到未來的 re-supply 重發 Move
            var obs = from player in GatewayClient.Queryer.QueryNotifier<Shared.IPlayer>().SupplyEvent().Take(1)
                      from result in player.Move(direction).RemoteValue()
                      select result;
            var disp =  obs.Subscribe();
            _Disposable.Add(disp);
        }

        public void Stop()
        {
            _Disposable.Clear();

            var obs = from player in GatewayClient.Queryer.QueryNotifier<Shared.IPlayer>().SupplyEvent().Take(1)
                      from result in player.Stop().RemoteValue()
                      select result;
            var disp = obs.Subscribe();
            _Disposable.Add(disp);
        }

        void OnDestroy()
        {
            _Disposable.Dispose();
        }
    }
}
