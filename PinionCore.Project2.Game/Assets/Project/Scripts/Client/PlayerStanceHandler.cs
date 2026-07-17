using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using System;
using UniRx;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PinionCore.Project2.Client
{
    /// <summary>
    /// 姿態切換輸入:按鍵觸發 PlayerRemote.SwitchStance(冒險系 ⇄ 戰鬥系 idle)。
    /// 目標動作由 PlayerRemote 從 Transition 白名單以 ActionConfig 能力欄位選出
    /// (查表函式 = 殼的 FindAction,與伺服器同一份資產),白名單無目標直接回 false。
    ///
    /// 切換成功 = 走路被 idle 取代:通知 PlayerInputHandler 補送按住中的移動,
    /// 讓角色在新姿態下重新起步(邊緣觸發不會自己補)。
    ///
    /// 在途鎖與逾時:與 PlayerAttackHandler 相同,回應遺失由逾時解鎖。
    /// </summary>
    public class PlayerStanceHandler : MonoBehaviour
    {
        // 抽象為 QueryerHost:可掛 Client(直連)或 GatewayClient(經 Router)
        public PinionCore.NetSync.QueryerHost Client;
        public ActorProvider Provider;
        public PlayerRemote ClientPlayer;
        public PlayerInputHandler InputHandler;

        // InputSystem_Actions 的 Player/SwitchStance
        public InputActionReference SwitchStanceAction;

        // 回應逾時(秒):超過即解鎖,容忍越權切換與回應遺失
        [SerializeField] float ResponseTimeout = 2f;

        // 測試接縫:非 null 時取代 InputAction 讀值
        public Func<bool> InputSource;

        ActorShell _shell;
        readonly UniRx.CompositeDisposable _shellSubscriptions = new UniRx.CompositeDisposable();
        bool _actionActive;
        bool _awaitingResponse;
        float _sentTime;

        void OnEnable()
        {
            if (SwitchStanceAction != null)
                SwitchStanceAction.action.Enable();
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
            _shellSubscriptions.Clear();
            _shell = shell;

            // 一次性動作(攻擊/受傷)進行中切換必被拒,本地先擋下不浪費往返
            Observable.FromEvent<ActionInfo>(h => shell.ActionEvent += h, h => shell.ActionEvent -= h)
                .Subscribe(_OnActionEvent)
                .AddTo(_shellSubscriptions);
        }

        void _OnActionEvent(ActionInfo info)
        {
            var config = _shell != null ? _shell.FindAction(info.Action) : null;
            _actionActive = info.Action != ActionType.None && (config == null || !config.Loop);
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
                    _awaitingResponse = false;   // 越權切換/回應遺失:解鎖即可,無事可補
                return;
            }

            var pressed = InputSource != null ? InputSource()
                        : SwitchStanceAction != null && SwitchStanceAction.action.WasPressedThisFrame();
            if (!pressed)
                return;

            // 殼未綁定不送;一次性動作進行中的按鍵直接忽略(伺服器也會拒收)
            if (_shell == null || _actionActive)
                return;

            _awaitingResponse = true;
            _sentTime = Time.unscaledTime;
            ClientPlayer.SwitchStance(_shell.FindAction, _OnResponded);
        }

        void _OnResponded(bool accepted)
        {
            _awaitingResponse = false;
            // 切換成功即回 idle,按住中的方向鍵要在新姿態重新起步
            if (accepted && InputHandler != null)
                InputHandler.ForceResend();
        }

        void OnDisable()
        {
            _awaitingResponse = false;
            if (SwitchStanceAction != null)
                SwitchStanceAction.action.Disable();
        }

        void OnDestroy()
        {
            _shellSubscriptions.Dispose();
        }
    }
}
