using PinionCore.NetSync.Gateways;
using PinionCore.NetSync.UniRx;
using System;
using UniRx;
using UnityEngine;

namespace PinionCore.Project2.Client
{
    public class PlayerCameraHandler : MonoBehaviour
    {
        // 抽象為 QueryerHost:可掛 Client(直連)或 GatewayClient(經 Router)
        public PinionCore.NetSync.QueryerHost Client;
        public ActorProvider Provider;
        public Unity.Cinemachine.CinemachineCamera FollowCamera;

        // 目前綁定的殼,用於殼被銷毀時比對解除
        ActorShell _bound;

        void Start()
        {
            // 每次 IPlayer supply(含斷線重連的 re-supply)都重新解析本地殼:
            // Provider.SupplyEvent() 會 replay 既有殼,IPlayer 先到或殼先建立兩種順序都成立;
            // 等殼 activeSelf(首個 MoveEvent 已定位 Target)才綁定,避免鏡頭先吸到原點;
            // Switch:新一輪 supply 取消上一輪還在等的訂閱
            // 統一入口:只 query IUserEntry,IPlayer 沿合約鏈(entry.Games → game.Players)取得
            var games = from entry in Client.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                        from game in entry.Games.SupplyEvent()
                        select game;

            var bind = from game in games
                       from player in game.Player.SupplyEvent()
                       select _ResolveShell(player.ActorId);
            bind.Switch().Subscribe(_Bind).AddTo(this);

            // 玩家 ghost 消失(登出/斷線)→ 解除跟隨
            (from game in games
             from player in game.Player.UnsupplyEvent()
             select player)
                .Subscribe(_ => _Unbind()).AddTo(this);

            // 綁定中的殼被銷毀(actor unsupply 先於 player unsupply)→ 解除跟隨
            Provider.UnsupplyEvent()
                .Where(shell => shell == _bound)
                .Subscribe(_ => _Unbind()).AddTo(this);
        }

        IObservable<ActorShell> _ResolveShell(Guid actorId)
        {
            return from shell in Provider.SupplyEvent().Where(s => s.ActorId == actorId).Take(1)
                   from _ in shell.gameObject
                       .ObserveEveryValueChanged(g => g.activeSelf)
                       .Where(active => active).Take(1)
                   select shell;
        }

        void _Bind(ActorShell shell)
        {
            _bound = shell;
            FollowCamera.Follow = shell.Target;
            FollowCamera.LookAt = shell.Target;
            // 首次取得目標:重置 vcam 內部狀態直接瞬移到位,不從場景原點阻尼飄過去
            FollowCamera.PreviousStateIsValid = false;
        }

        void _Unbind()
        {
            _bound = null;
            if (FollowCamera == null)
                return;
            FollowCamera.Follow = null;
            FollowCamera.LookAt = null;
        }
    }
}
