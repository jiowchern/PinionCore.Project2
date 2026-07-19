using PinionCore.Project2.Shared;
using PinionCore.Utility;

namespace PinionCore.Project2.Worlds.Statuses
{
    /// <summary>
    /// 控制狀態:每個轉移節點一顆,Enter 播放 Current 動作,動作自然播完(Player.EndEvent)
    /// 自動轉移到 Next。協議面(IControllable soul)由 PlayerController 承載(與 Player 同生命週期),
    /// Play 委派進來;「動作能不能執行」由 Transition.Playables 白名單表達
    /// (攻擊中無法移動 = 攻擊態白名單為空)。
    /// ActionConfig.ChainWindow > 0 的動作播完不立即轉移:狀態(含 Playables 白名單)
    /// 續留至窗到期(Update 判定),窗內 Play 照常接招 —— combo 的播完後接招窗。
    /// </summary>
    internal class ControllerStatus : IStatus
    {
        readonly Player _Player;
        readonly UnityEngine.Vector2 _Direction;   // 進場方向(走路轉移用;零向量 = 無指定)

        public readonly Transition Transition;

        bool _Subscribed;      // 已訂閱 EndEvent(Leave 據此取消)
        bool _Done;            // 已發出轉移,之後的 Play/EndEvent 一律忽略,防重複 push
        bool _ChainPending;    // 動作已播完、接招窗未到期(_ChainDeadline 為到期 tick)
        long _ChainDeadline;

        /// <summary>要求轉移到指定動作狀態(方向給走路;非位移動作忽略)。</summary>
        public event System.Action<ActionType, UnityEngine.Vector2> NextEvent;

        public ControllerStatus(Player player, Transition transition, UnityEngine.Vector2 direction)
        {
            NextEvent += (type, dir) => { };   // 避免 null reference
            _Player = player;
            Transition = transition;
            _Direction = direction;
        }

        void IStatus.Enter()
        {
            // 狀態機轉移一律 force:白名單已在上一個狀態的 Play 驗過,
            // Player 的毫秒級重入閘不再適用(走路→idle 等 locomotion 間轉移會被它擋下)
            if (!_Player.StartAction(Transition.Current.Action, force: true, _Direction))
            {
                // 角色無此動作 config(如測試用精簡配置):不播動作,
                // 狀態照常運作、轉移照常可用,ActionInfo 維持原值
                UnityEngine.Debug.Log($"[ControllerStatus] 無 {Transition.Current.Action} 的 ActionConfig,僅維持狀態不播動作");
            }

            // EndEvent 無 replay:只會收到訂閱之後的結束訊號,不需基線過濾
            _Player.EndEvent += _OnEnd;
            _Subscribed = true;
        }

        void IStatus.Leave()
        {
            if (_Subscribed)
                _Player.EndEvent -= _OnEnd;
        }

        void IStatus.Update()
        {
            // 接招窗到期:無人接招,補發播完當下欠著的 Next 轉移
            if (!_ChainPending || _Done)
                return;
            if (_Player.NowTicks < _ChainDeadline)
                return;
            _ChainPending = false;
            _Done = true;
            NextEvent(Transition.Next.Action, UnityEngine.Vector2.zero);
        }

        public bool Play(ActionType name, UnityEngine.Vector2 direction)
        {
            if (_Done)
                return false;   // 已在轉移途中,由下一個狀態接手

            var playable = false;
            foreach (var p in Transition.Playables)
            {
                if (p.Action != name)
                    continue;
                playable = true;
                break;
            }
            if (!playable)
                return false;

            // 白名單含自身 = locomotion 重定向:不換狀態,直接改走向(吃 MoveAcceptInterval 節流)
            if (name == Transition.Current.Action)
                return _Player.Move(direction);

            _Done = true;
            NextEvent(name, direction);
            return true;
        }

        void _OnEnd(ActionType ended, long tick)
        {
            if (_Done)
                return;
            // 非本狀態的動作結束不代打轉移:伺服器 force 覆蓋(未來傷害管線)的動作播完
            // 由覆蓋方負責 push 對應狀態;測試直啟路徑(Move/StartAction)的結束同理
            if (ended != Transition.Current.Action)
                return;
            // 動作自然播完(一次性動作到期/循環被守門結束):轉移到 Next;
            // Next = 自身(idle)時不轉移,避免無意義的狀態 churn
            if (Transition.Next.Action == Transition.Current.Action)
                return;
            // 接招窗:播完後白名單仍開放的 config 先不轉移,交給 Update 的到期判定;
            // 白名單為空(combo 收招式)窗無意義,照常立即轉移
            var config = _Player.FindConfig(ended);
            if (config != null && config.ChainWindow > 0f && Transition.Playables.Length > 0)
            {
                _ChainPending = true;
                _ChainDeadline = tick + (long)(config.ChainWindow * System.TimeSpan.TicksPerSecond);
                return;
            }
            _Done = true;
            NextEvent(Transition.Next.Action, UnityEngine.Vector2.zero);
        }
    }
}
