using PinionCore.NetSync.Gateways;
using PinionCore.NetSync.UniRx;
using System;
using UniRx;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PinionCore.Project2.Client
{
    /// <summary>
    /// WASD 攝影機相對移動:輸入向量以相機 yaw 轉成世界 XZ 目標方向,
    /// 再換算成相對角色當下朝向的向量送 Move。
    ///
    /// 送指令採前饋式而非高頻重送:伺服器的 ω=偏移角/秒 本身就意味著「約 1 秒轉到目標」,
    /// 高頻重送會拿 client 重播(落後一個網路延遲)的朝向去量偏移,在 TCP 延遲下閉迴路發散
    /// (曾造成原地無限旋轉)。因此:方向改變時送一發轉向指令,等殼轉到對齊時送一發
    /// 直行指令(ω=0)斬斷永久弧線,之後靜默;僅低頻檢查漂移。放開按鍵送一次 Stop。
    /// </summary>
    public class PlayerInputHandler : MonoBehaviour
    {
        public GatewayClient Client;
        public ActorProvider Provider;
        public Player ClientPlayer;

        // Main Camera(CinemachineBrain 輸出),讀玩家實際所見的朝向;不讀 vcam 內部軸值
        public Transform CameraTransform;

        // InputSystem_Actions 的 Player/Move;只 Enable/Disable 這個 action,
        // 不影響同一 asset 上 CinemachineInputAxisController 消費的 Look
        public InputActionReference MoveAction;

        // 偏移角小於此值(度)視為已對齊:送直行指令(ω=0)而非微小弧線
        [SerializeField] float AlignAngleThreshold = 5f;

        // 世界目標方向偏離上次送出超過此角度(度)即重下指令(按鍵組合或相機轉動改向)
        [SerializeField] float RedirectAngleThreshold = 10f;

        // 指令之間的最小間隔:相機連續轉動時封頂指令頻率
        [SerializeField] float MinSendInterval = 0.25f;

        // 低頻重檢:轉向逾期未對齊(延遲、指令掉失)或直行後漂移,以最新偏移重下指令
        [SerializeField] float RecheckInterval = 1.25f;

        [SerializeField] float DeadZone = 0.2f;

        // 測試接縫:非 null 時取代 InputAction 讀值,測試不需 InputTestFixture
        public Func<Vector2> InputSource;

        Actor _shell;
        bool _moving;
        bool _turning;         // 最後一發指令是轉向弧線(ω≠0),等待對齊後送直行
        float _lastSendTime;
        Vector2 _lastWorldDir; // 上次送出指令時的世界目標方向

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
            var bind = from player in Client.Queryer.QueryNotifier<Shared.IPlayer>().SupplyEvent()
                       select _ResolveShell(player.ActorId);
            bind.Switch().Subscribe(shell => _shell = shell).AddTo(this);

            Client.Queryer.QueryNotifier<Shared.IPlayer>().UnsupplyEvent()
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

        void _Unbind()
        {
            _shell = null;
            // ghost 已凍結/銷毀,不對其補送 Stop
            _moving = false;
            _turning = false;
        }

        void Update()
        {
            var input = InputSource != null ? InputSource()
                      : MoveAction != null ? MoveAction.action.ReadValue<Vector2>()
                      : Vector2.zero;

            if (input.magnitude < DeadZone)
            {
                // 放開 → 邊緣觸發只送一次 Stop(零向量不能代替 Stop,server 會拒絕)
                if (_moving)
                {
                    ClientPlayer.Stop();
                    _moving = false;
                    _turning = false;
                }
                return;
            }

            // 殼未綁定:不送、不排隊;按住不放時綁定完成的下一幀自然開始
            if (_shell == null)
                return;

            var worldDir = _ToWorldDirection(input);
            if (worldDir.sqrMagnitude < 1e-6f)
                return;

            // 偏移角:世界目標方向相對殼當下朝向,正=右轉(與 server 的 Atan2(x,y) 同義)
            var f3 = _shell.Target.forward;
            var facing = new Vector2(f3.x, f3.z).normalized;
            var right = new Vector2(facing.y, -facing.x);
            var offsetRad = Mathf.Atan2(Vector2.Dot(worldDir, right), Vector2.Dot(worldDir, facing));
            var aligned = Mathf.Abs(offsetRad) <= AlignAngleThreshold * Mathf.Deg2Rad;

            var sinceLast = Time.unscaledTime - _lastSendTime;
            var send =
                // 起步
                !_moving
                // 目標方向改變(按鍵組合或相機轉動)
                || Vector2.Angle(worldDir, _lastWorldDir) > RedirectAngleThreshold
                // 弧線已轉到目標:送直行(ω=0)斬斷永久弧線,否則會繞圈不止
                || (_turning && aligned)
                // 低頻重檢:轉向逾期未對齊,或直行後漂移超出對齊範圍
                || (sinceLast >= RecheckInterval && (_turning || !aligned));

            if (!send || (_moving && sinceLast < MinSendInterval))
                return;

            // 對齊:直行;未對齊:偏移角的單位向量,server 端 Atan2 還原為 ω=偏移角/秒
            var rel = aligned
                ? new Vector2(0f, 1f)
                : new Vector2(Mathf.Sin(offsetRad), Mathf.Cos(offsetRad));
            ClientPlayer.Move(rel);
            _moving = true;
            _turning = !aligned;
            _lastSendTime = Time.unscaledTime;
            _lastWorldDir = worldDir;
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
            // 按住時元件被關閉:補送一次 Stop,避免角色以最後一筆弧線跑不停
            if (_moving)
            {
                ClientPlayer.Stop();
                _moving = false;
                _turning = false;
            }
            if (MoveAction != null)
                MoveAction.action.Disable();
        }
    }
}
