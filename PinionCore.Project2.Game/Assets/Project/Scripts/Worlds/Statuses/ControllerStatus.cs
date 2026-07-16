using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;
using System.Collections.Generic;

namespace PinionCore.Project2.Worlds.Statuses
{
    /// <summary>
    /// 控制狀態:狀態類自身即協議實作(IControllable),Enter 播放 Current 動作並供應自己、
    /// Leave 收回(每狀態一顆 soul)。「動作能不能執行」由 Transition.Playables 白名單表達
    /// (攻擊中無法移動 = 攻擊態白名單為空);Cast 自然播完(ActionEvent None)自動轉移到 Next。
    /// </summary>
    internal class ControllerStatus : IStatus, IControllable
    {
        readonly Player _Player;
        readonly Transition _Transition;
        readonly ICollection<IControllable> _Controllers;
        readonly UnityEngine.Vector2 _Direction;   // 進場方向(走路轉移用;零向量 = 無指定)
        readonly Property<Transition> _TransitionProperty;   // soul 綁定要求固定實例,禁止 getter 內新建

        ActionInfo _Started;   // 訂閱 replay 記下的基準(本狀態剛啟動的那筆)
        bool _Baselined;       // 已收到 replay 基準
        bool _Subscribed;      // 已訂閱 ActionEvent(Leave 據此取消)
        bool _Done;            // 已發出轉移,之後的 Play/ActionEvent 一律忽略,防重複 push

        /// <summary>要求轉移到指定動作狀態(方向給走路;非位移動作忽略)。</summary>
        public event System.Action<ActionType, UnityEngine.Vector2> NextEvent;

        public ControllerStatus(Player player, Transition transition, ICollection<IControllable> controllers, UnityEngine.Vector2 direction)
        {
            NextEvent += (type, dir) => { };   // 避免 null reference
            _Player = player;
            _Transition = transition;
            _Controllers = controllers;
            _Direction = direction;
            _TransitionProperty = new Property<Transition>(transition);
        }

        Property<Transition> IControllable.Transition => _TransitionProperty;

        void IStatus.Enter()
        {
            // 狀態機轉移一律 force:白名單已在上一個狀態的 Play 驗過,
            // Player 的毫秒級重入閘不再適用(走路→idle 等 locomotion 間轉移會被它擋下)
            if (!_Player.StartAction(_Transition.Current.Action, force: true, _Direction))
            {
                // 角色無此動作 config(如測試用精簡配置):不播動作,
                // 狀態照常供應、轉移照常可用,ActionInfo 維持原值
                UnityEngine.Debug.Log($"[ControllerStatus] 無 {_Transition.Current.Action} 的 ActionConfig,僅供應狀態不播動作");
            }

            // 訂閱即 replay:第一發即本狀態剛啟動的 ActionInfo,記為基準
            _Player.ActionEvent += _OnAction;
            _Subscribed = true;

            _Controllers.Add(this);
        }

        void IStatus.Leave()
        {
            if (_Subscribed)
                _Player.ActionEvent -= _OnAction;
            _Controllers.Remove(this);
        }

        void IStatus.Update()
        {
        }

        Value<bool> IControllable.Play(ActionType name, UnityEngine.Vector2 direction)
        {
            if (_Done)
                return false;   // 已在轉移途中,soul 即將收回

            var playable = false;
            foreach (var p in _Transition.Playables)
            {
                if (p.Action != name)
                    continue;
                playable = true;
                break;
            }
            if (!playable)
                return false;

            // 白名單含自身 = locomotion 重定向:不換狀態,直接改走向(吃 MoveAcceptInterval 節流)
            if (name == _Transition.Current.Action)
                return _Player.Move(direction);

            _Done = true;
            NextEvent(name, direction);
            return true;
        }

        void _OnAction(ActionInfo info)
        {
            if (!_Baselined)
            {
                _Started = info;
                _Baselined = true;
                return;
            }
            if (_Done)
                return;
            if (info.Action == ActionType.None)
            {
                // 動作自然播完(Cast 到期/locomotion 被守門結束):轉移到 Next;
                // Next = 自身(idle)時不轉移,避免無意義的 soul churn
                if (_Transition.Next.Action == _Transition.Current.Action)
                    return;
                _Done = true;
                NextEvent(_Transition.Next.Action, UnityEngine.Vector2.zero);
            }
            // 非本狀態的 (Action, StartTicks) = 伺服器 force 覆蓋(未來傷害管線):
            // 由覆蓋方負責 push 對應狀態,這裡不自動轉移
        }
    }
}
