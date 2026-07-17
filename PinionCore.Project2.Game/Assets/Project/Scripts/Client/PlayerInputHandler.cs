using PinionCore.NetSync.Gateways;
using PinionCore.NetSync.UniRx;
using System;
using UniRx;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PinionCore.Project2.Client
{
    /// <summary>
    /// WASD 攝影機相對移動:輸入向量以相機 yaw 轉成世界 XZ 方向,直接送 Move(世界方向)。
    /// 伺服器瞬轉直走,指令純由本地輸入(相機+按鍵)決定、不回讀角色狀態,
    /// 沒有回饋迴路,延遲下天然穩定;方向改變才送,伺服器另有 MoveAcceptInterval 節流。
    /// 放開按鍵送一次 Stop(= 轉移回 idle,不受節流限制)。
    ///
    /// 在途指令鎖與逾時:送出 Move/Stop 後,收到伺服器回傳值才接受下一個指令(單一在途指令)。
    /// 每狀態一顆 soul 的架構下,狀態轉移窗口打在舊 soul 的指令會被靜默丟棄(回應不來),
    /// 屬預期事件:逾時記 log 後解鎖,下一幀以最新輸入重送(latest-wins),不讓輸入卡死。
    /// </summary>
    public class PlayerInputHandler : MonoBehaviour
    {
        // 抽象為 QueryerHost:可掛 Client(直連)或 GatewayClient(經 Router)
        public PinionCore.NetSync.QueryerHost Client;
        public ActorProvider Provider;
        public PlayerRemote ClientPlayer;

        // Main Camera(CinemachineBrain 輸出),讀玩家實際所見的朝向;不讀 vcam 內部軸值
        public Transform CameraTransform;

        // InputSystem_Actions 的 Player/Move;只 Enable/Disable 這個 action,
        // 不影響同一 asset 上 CinemachineInputAxisController 消費的 Look
        public InputActionReference MoveAction;

        // 世界方向偏離上次送出超過此角度(度)即重下指令(按鍵組合或相機轉動改向)
        [SerializeField] float RedirectAngleThreshold = 10f;

        // 指令之間的最小間隔:相機連續轉動時封頂指令頻率;
        // 應不小於伺服器 ActorConfig.MoveAcceptInterval(0.2),避免指令被節流拒收
        [SerializeField] float MinSendInterval = 0.2f;

        // 低頻重檢:指令被伺服器節流拒收或掉失時,以殼朝向(=伺服器已接受的方向)比對修復
        [SerializeField] float RecheckInterval = 1f;

        // 回應逾時(秒):超過即記 log 並解鎖(掉包不中斷遊戲,伺服器端另有 Soul not found 診斷 log)
        [SerializeField] float ResponseTimeout = 2f;

        [SerializeField] float DeadZone = 0.2f;

        // 測試接縫:非 null 時取代 InputAction 讀值,測試不需 InputTestFixture
        public Func<Vector2> InputSource;

        ActorShell _shell;
        bool _moving;
        float _lastSendTime;
        Vector2 _lastWorldDir; // 上次送出指令的世界方向

        // 在途指令鎖:true = 已送出 Move/Stop、尚未收到回傳值
        bool _awaitingResponse;

        void OnEnable()
        {
            if (MoveAction != null)
                MoveAction.action.Enable();
        }

        void Start()
        {
            // 與 PlayerCameraHandler 相同的解析模式:每次 IPlayer supply(含斷線重連的
            // re-supply)都重新解析本地殼;等殼 activeSelf(首個 MoveEvent 已定位)才綁定;
            // Switch:新一輪 supply 取消上一輪還在等的訂閱
            // 統一入口:只 query IUserEntry,IPlayer 沿合約鏈(entry.Games → game.Players)取得
            var games = from entry in Client.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                        from game in entry.Games.SupplyEvent()
                        select game;

            var bind = from game in games
                       from player in game.Player.SupplyEvent()
                       select _ResolveShell(player.ActorId);
            bind.Switch().Subscribe(shell => _shell = shell).AddTo(this);

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

        void _Unbind()
        {
            _shell = null;
            // ghost 已凍結/銷毀,不對其補送 Stop;在途回應也不會再抵達,解除鎖定
            _moving = false;
            _awaitingResponse = false;
        }

        void Update()
        {
            var input = InputSource != null ? InputSource()
                      : MoveAction != null ? MoveAction.action.ReadValue<Vector2>()
                      : Vector2.zero;

            // 在途指令鎖:回應抵達前不送下一個指令;desired 狀態每幀從輸入重算,
            // 解鎖後自然以最新狀態補送(latest-wins),不需排隊。
            // 逾時 = 指令掉失(狀態轉移窗口打在舊 soul,被伺服器靜默丟棄的預期事件,
            // 或 gateway 回應遺失):記 log 後解鎖重送,不讓輸入卡死
            if (_awaitingResponse)
            {
                if (Time.unscaledTime - _lastSendTime > ResponseTimeout)
                {
                    Debug.Log($"[PlayerInputHandler] Move/Stop 送出超過 {ResponseTimeout} 秒未收到回應(soul 轉移窗口掉包或回應遺失),解鎖重送");
                    _awaitingResponse = false;
                    // 邊緣觸發狀態一併重置:下一幀依當下輸入重新起步/停止
                    _moving = false;
                    _lastSendTime = float.MinValue;
                }
                return;
            }

            if (input.magnitude < DeadZone)
            {
                // 放開 → 邊緣觸發只送一次 Stop(零向量不能代替 Stop,server 會拒絕)
                if (_moving)
                {
                    _Send(() => ClientPlayer.Stop(_OnResponded));
                    _moving = false;
                }
                return;
            }

            // 殼未綁定:不送、不排隊;按住不放時綁定完成的下一幀自然開始
            if (_shell == null)
                return;

            var worldDir = _ToWorldDirection(input);
            if (worldDir.sqrMagnitude < 1e-6f)
                return;

            var sinceLast = Time.unscaledTime - _lastSendTime;
            var send =
                // 起步
                !_moving
                // 目標方向改變(按鍵組合或相機轉動)
                || Vector2.Angle(worldDir, _lastWorldDir) > RedirectAngleThreshold
                // 低頻修復:殼朝向=伺服器已接受的方向,不一致代表指令被節流拒收
                || (sinceLast >= RecheckInterval && Vector2.Angle(worldDir, _ShellFacing()) > RedirectAngleThreshold);

            if (!send || (_moving && sinceLast < MinSendInterval))
                return;

            _Send(() => ClientPlayer.Move(_shell.FindAction, worldDir, _OnResponded));
            _moving = true;
            _lastWorldDir = worldDir;
        }

        void _Send(Action dispatch)
        {
            _awaitingResponse = true;
            _lastSendTime = Time.unscaledTime;
            dispatch();
        }

        void _OnResponded(bool accepted)
        {
            _awaitingResponse = false;
        }

        /// <summary>
        /// 強制下一幀重評估輸入:動作(攻擊等)結束時由 PlayerAttackHandler 呼叫。
        /// 動作期間伺服器拒收 Move,而本層是邊緣觸發 —— 按住方向鍵出招,結束後
        /// _moving 仍為 true 且方向未變,永遠不會補送;重置後按住的鍵自然重新起步。
        /// 動作結束也是權威訊號:攻擊期間打在舊 soul 的在途指令不會再有回應,一併解鎖。
        /// </summary>
        public void ForceResend()
        {
            _moving = false;
            _lastSendTime = float.MinValue;
            _awaitingResponse = false;
        }

        Vector2 _ShellFacing()
        {
            var f = _shell.Target.forward;
            return new Vector2(f.x, f.z).normalized;
        }

        // 輸入 (x=右, y=前) 以相機 yaw 旋轉成世界 XZ 方向(Vector2:x=+X、y=+Z)
        Vector2 _ToWorldDirection(Vector2 input)
        {
            var f = CameraTransform.forward;
            var fwd = new Vector2(f.x, f.z);
            if (fwd.sqrMagnitude < 1e-6f)
            {
                // 俯視退化(本專案 orbital pitch 鎖 50°,理論上不會發生):改用 up 投影
                var u = CameraTransform.up;
                fwd = new Vector2(u.x, u.z);
            }
            if (fwd.sqrMagnitude < 1e-6f)
                fwd = _lastWorldDir;
            else
                fwd = fwd.normalized;

            var right = new Vector2(fwd.y, -fwd.x); // PerpRight:+Z 前 → +X 右
            return (input.x * right + input.y * fwd).normalized;
        }

        void OnDisable()
        {
            // 按住時元件被關閉:補送一次 Stop,避免角色沿最後方向跑不停;
            // 這會 Clear 掉在途訂閱(回應不再回呼),鎖一併重置,重新啟用時不卡死
            if (_moving)
            {
                ClientPlayer.Stop();
                _moving = false;
            }
            _awaitingResponse = false;
            if (MoveAction != null)
                MoveAction.action.Disable();
        }
    }
}
