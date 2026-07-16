using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using System;
using UniRx;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PinionCore.Project2.Client
{
    /// <summary>
    /// 攻擊輸入:戰鬥狀態下按鍵觸發 IBattle.Attack RPC。
    /// 動作的表現(動畫/凍結旋轉)與位移全由 Actor 殼跟著伺服器事件走,本層只負責觸發;
    /// 收到殼的 ActionEvent None(動作結束)時通知 PlayerInputHandler 補送按住中的移動。
    ///
    /// 在途鎖與逾時:冒險狀態下 IBattle 不被供應,Attack 的訂閱不會發射(回呼不來),
    /// 故逾時自動解鎖;戰鬥狀態下回應必達,逾時只是保險。
    /// </summary>
    public class PlayerAttackHandler : MonoBehaviour
    {
        // 抽象為 QueryerHost:可掛 Client(直連)或 GatewayClient(經 Router)
        public PinionCore.NetSync.QueryerHost Client;
        public ActorProvider Provider;
        public Player ClientPlayer;
        public PlayerInputHandler InputHandler;

        // InputSystem_Actions 的 Player/Attack
        public InputActionReference AttackAction;

        // 回應逾時(秒):超過即解鎖,容忍冒險態誤按與回應遺失
        [SerializeField] float ResponseTimeout = 2f;

        // 測試接縫:非 null 時取代 InputAction 讀值
        public Func<bool> InputSource;

        Actor _shell;
        readonly UniRx.CompositeDisposable _shellSubscriptions = new UniRx.CompositeDisposable();
        bool _actionActive;
        bool _awaitingResponse;
        float _sentTime;

        void OnEnable()
        {
            if (AttackAction != null)
                AttackAction.action.Enable();
        }

        void Start()
        {
            // 與 PlayerInputHandler 相同的解析模式:每次 IPlayer supply 都重新解析本地殼
            var games = from entry in Client.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                        from game in entry.Games.SupplyEvent()
                        select game;

            var bind = from game in games
                       from player in game.Player.SupplyEvent()
                       select _ResolveShell(player.ActorId);
            bind.Switch().Subscribe(_Bind).AddTo(this);

            (from game in games
             from player in game.Player.UnsupplyEvent()
             select player)
                .Subscribe(_ => _Unbind()).AddTo(this);

            Provider.UnsupplyEvent()
                .Where(shell => shell == _shell)
                .Subscribe(_ => _Unbind()).AddTo(this);
        }

        IObservable<Actor> _ResolveShell(Guid actorId)
        {
            return from shell in Provider.SupplyEvent().Where(s => s.ActorId == actorId).Take(1)
                   from _ in shell.gameObject
                       .ObserveEveryValueChanged(g => g.activeSelf)
                       .Where(active => active).Take(1)
                   select shell;
        }

        void _Bind(Actor shell)
        {
            _shellSubscriptions.Clear();
            _shell = shell;

            // 殼的 ActionEvent 是伺服器權威訊號(訂閱即 replay):
            // None 且先前有動作進行 → 動作結束,讓移動輸入重新起步
            Observable.FromEvent<ActionInfo>(h => shell.ActionEvent += h, h => shell.ActionEvent -= h)
                .Subscribe(_OnActionEvent)
                .AddTo(_shellSubscriptions);
        }

        void _OnActionEvent(ActionInfo info)
        {
            // 走路(Locomotion)不佔用攻擊鎖:循環動作直到 Stop 才發 None,
            // 視為進行中會永久擋住攻擊輸入;攻擊取代走路時照常上鎖
            if (ActionTypes.IsLocomotion(info.Action))
                return;
            if (info.Action != ActionType.None)
            {
                _actionActive = true;
                return;
            }
            if (_actionActive && InputHandler != null)
                InputHandler.ForceResend();
            _actionActive = false;
        }

        void _Unbind()
        {
            _shellSubscriptions.Clear();
            _shell = null;
            _actionActive = false;
            _awaitingResponse = false;
        }

        void Update()
        {
            if (_awaitingResponse)
            {
                if (Time.unscaledTime - _sentTime > ResponseTimeout)
                    _awaitingResponse = false;   // 冒險態誤按/回應遺失:解鎖即可,無事可補
                return;
            }

            var pressed = InputSource != null ? InputSource()
                        : AttackAction != null && AttackAction.action.WasPressedThisFrame();
            if (!pressed)
                return;

            // 殼未綁定不送;非戰鬥狀態或動作進行中的按鍵直接忽略(伺服器也會拒收)
            if (_shell == null || _shell.Status != StatusType.Battle || _actionActive)
                return;

            _awaitingResponse = true;
            _sentTime = Time.unscaledTime;
            ClientPlayer.Attack(_OnResponded);
        }

        void _OnResponded(bool accepted)
        {
            _awaitingResponse = false;
        }

        void OnDisable()
        {
            _awaitingResponse = false;
            if (AttackAction != null)
                AttackAction.action.Disable();
        }

        void OnDestroy()
        {
            _shellSubscriptions.Dispose();
        }
    }
}
