using System;
using System.Collections.Generic;
using PinionCore.Project2.Shared;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 抓取配對系統:HitEffect=Grab 的命中在 HitResolver 掃描中 enqueue(不動位置,
    /// 避免污染同幀後續命中判定),Tick(World.Update 於 HitResolver 之後)結算成 pair。
    /// 節點對映由 _Families 家族描述表驅動(每套成對抓取動畫一筆),以下行為描述以
    /// 第一套(Grab*)的節點名為例,對其他家族同構成立。
    ///
    /// 配對存續期間:
    /// - grabber 的每次 MoveInfo emission 加錨點偏移「轉發」為 victim 的 MoveInfo
    ///   (MoveEvent 訂閱即 replay = 建立當下完成初始吸附);victim 面向 grabber(Facing = -錨軸),
    ///   位移以「負 Speed 沿 -Facing」表達並投影到錨軸 —— MoveInfo.Facing 同時是位移方向與
    ///   視覺朝向,被拖行者「面向 grabber 倒退走」只能這樣編碼(段側移的垂直分量丟棄,
    ///   由下一次 emission / dirty 校正重新對錨)。
    /// - victim 的外來 MoveInfo emission(循環 wrap park、動作結束/Enter park、TOI redirect)
    ///   以 reentrancy 旗標識別後標 dirty,Tick 同幀重新轉發一次蓋回錨點
    ///   (不同步重入 _SetMoveInfo,校正上界 = 每幀一次)。
    /// - grabber 節點轉移鏡射到 victim(IdleA→IdleB、WalkA→WalkB、ThrowA→ThrowB+解體);
    ///   離開 grab 家族(如被第三方打進 UnarmedDamage)即解體釋放 victim ——
    ///   「打抓取者解體」規則在此自動實現。UnarmedGrabAtk1A 不鏡射:B 側受創反應由 Damage 路由
    ///   (Atk1A 的 HitSegment 命中 → victim.Damage() → 節點 Damage=UnarmedGrabAtk1B),
    ///   第三方打 victim 與抓取者補打因此走同一條管線。
    /// - victim 進 UnarmedGrabBreakB(白名單放行的玩家輸入)→ 解體,grabber 進 UnarmedGrabBreakA 後搖。
    ///
    /// 時序前提(stale-EndEvent 競態結構性不存在):ForceTransition 同步 swap _ControllerStatus
    /// 後,舊 ControllerStatus 雖仍訂閱 Player.EndEvent,但 EndEvent 只從 _ProcessDueRedirects
    /// 發出,其入口(StartAction/Move/Stop/Player.Update)在 force 之後、下次 StatusMachine
    /// dequeue 之前對該 Player 全部不可達(controller.Update 順序 = 狀態機先、Player.Update 後;
    /// 狀態機換狀態先 old.Leave() 退訂再 new.Enter())。
    /// </summary>
    internal class GrabResolver
    {
        /// <summary>
        /// 抓取動作家族描述:一套成對抓取動畫的全部節點對映與參數。
        /// 配對生命週期邏輯(鏡射/轉發/解體規則)全家族共通,只認欄位不認 enum 值 ——
        /// 擴充新一套抓取動作 = 在 _Families 加一筆,不要在本類別寫死任何 ActionType 比對
        /// (詳見 docs/grab-action-set-extension.md)。
        /// </summary>
        class GrabFamily
        {
            public ActionType Start;             // 起手(HitEffect=Grab 的命中窗動作;配對唯一驗證點)
            public ActionType IdleA, IdleB;      // 抓住循環(A→B 鏡射)
            public ActionType WalkA, WalkB;      // 拖行(A→B 鏡射;B 位置由轉發驅動)
            public ActionType Atk1A, Atk1B;      // 補打/受創反應(不鏡射,B 側經 Damage 路由進入)
            public ActionType ThrowA, ThrowB;    // 丟投(A 起手即解體,B 自驅烘焙飛行)
            public ActionType BreakA, BreakB;    // 掙脫(B 白名單唯一入口;成立後 A 進後搖)
            // 錨點距離(公尺):victim 吸附在 grabber 錨軸(ActionForward)前方此距離、面向 grabber。
            public float AnchorDistance;
        }

        static readonly GrabFamily[] _Families =
        {
            new GrabFamily
            {
                Start = ActionType.UnarmedGrabStart,
                IdleA = ActionType.UnarmedGrabIdleA,   IdleB = ActionType.UnarmedGrabIdleB,
                WalkA = ActionType.UnarmedGrabWalkA,   WalkB = ActionType.UnarmedGrabWalkB,
                Atk1A = ActionType.UnarmedGrabAtk1A,   Atk1B = ActionType.UnarmedGrabAtk1B,
                ThrowA = ActionType.UnarmedGrabThrowA, ThrowB = ActionType.UnarmedGrabThrowB,
                BreakA = ActionType.UnarmedGrabBreakA, BreakB = ActionType.UnarmedGrabBreakB,
                AnchorDistance = 0.9f,   // editor 目測調參
            },
        };

        class Pair
        {
            public GrabFamily Family;
            public PlayerController Grabber;
            public PlayerController Victim;
            public Action<Transition> GrabberTransitionHandler;
            public Action<Transition> VictimTransitionHandler;
            public Action<MoveInfo> GrabberMoveHandler;
            public Action<MoveInfo> VictimMoveHandler;
            public bool Forwarding;   // reentrancy:轉發中 victim 的 MoveEvent 是我方造成,不標 dirty
            public bool Dirty;        // victim 有外來 emission,Tick 需重新轉發蓋回錨點
        }

        struct PendingGrab
        {
            public PlayerController Grabber;
            public PlayerController Victim;
        }

        readonly List<PendingGrab> _Pending = new List<PendingGrab>();
        readonly List<Pair> _Pairs = new List<Pair>();
        // 任一方(grabber 或 victim)的 ActorId 都索引到所屬 pair(一人同時只能在一組配對)
        readonly Dictionary<Guid, Pair> _PairsByActor = new Dictionary<Guid, Pair>();

        /// <summary>HitResolver 掃描中命中呼叫:只 enqueue,結算在 Tick(掃描中不動位置)。</summary>
        public void EnqueueGrab(PlayerController grabber, PlayerController victim)
        {
            _Pending.Add(new PendingGrab { Grabber = grabber, Victim = victim });
        }

        public void Tick(long now)
        {
            if (_Pending.Count > 0)
            {
                foreach (var pending in _Pending)
                    _TryEstablish(pending.Grabber, pending.Victim);
                _Pending.Clear();
            }

            // 排空 dirty:victim 的外來 emission(wrap park / Enter park / TOI)蓋回錨點
            foreach (var pair in _Pairs)
            {
                if (!pair.Dirty)
                    continue;
                pair.Dirty = false;
                _Forward(pair);
            }
        }

        /// <summary>
        /// 玩家離開世界(World.Leave 於 controller.Shutdown 之前呼叫)或世界關閉:
        /// 清掉 pending 與所屬配對;在配對中則解體並讓倖存方回 UnarmedIdle。
        /// </summary>
        public void Forget(Guid actorId)
        {
            _Pending.RemoveAll(p => (Guid)p.Grabber.ActorId == actorId || (Guid)p.Victim.ActorId == actorId);
            if (!_PairsByActor.TryGetValue(actorId, out var pair))
                return;
            var survivor = (Guid)pair.Grabber.ActorId == actorId ? pair.Victim : pair.Grabber;
            _Dissolve(pair);
            survivor.ForceTransition(ActionType.UnarmedIdle, Vector2.zero);
        }

        /// <summary>世界關閉:退訂全部事件,不再驅動任何轉移(controller 隨後由 Dispose 逐一 Shutdown)。</summary>
        public void Clear()
        {
            foreach (var pair in new List<Pair>(_Pairs))
                _Dissolve(pair);
            _Pending.Clear();
        }

        void _TryEstablish(PlayerController grabber, PlayerController victim)
        {
            // 唯一驗證點:同幀 grabber 可能已被 Damage force 走(照 whiff 流程收尾)、
            // 任一方可能已在配對中(含同幀先到的抓取 —— 先到先贏;同幀互抓時後到方
            // 的節點已被換成 IdleB,自動失敗)。
            var family = Array.Find(_Families, f => f.Start == grabber.CurrentTransition.Current.Action);
            if (family == null)
                return;
            Guid grabberId = grabber.ActorId;
            Guid victimId = victim.ActorId;
            if (_PairsByActor.ContainsKey(grabberId) || _PairsByActor.ContainsKey(victimId))
                return;

            var pair = new Pair { Family = family, Grabber = grabber, Victim = victim };

            // 先換節點再訂閱:初始 Idle 對不需鏡射(此處顯式建立),訂閱後只處理後續變化。
            // 錨軸 = grabber 的動作朝向基底(命中判定同座標系);victim 面向其反方向進場。
            var anchorAxis = grabber.Player.ActionForward;
            grabber.ForceTransition(family.IdleA, Vector2.zero);   // zero = 沿用起手動作的朝向基底
            victim.ForceTransition(family.IdleB, -anchorAxis);

            pair.GrabberTransitionHandler = t => _OnGrabberTransition(pair, t);
            pair.VictimTransitionHandler = t => _OnVictimTransition(pair, t);
            pair.GrabberMoveHandler = _ => _OnGrabberMove(pair);
            pair.VictimMoveHandler = _ => _OnVictimMove(pair);

            grabber.TransitionChangedEvent += pair.GrabberTransitionHandler;
            victim.TransitionChangedEvent += pair.VictimTransitionHandler;
            victim.Player.MoveEvent += pair.VictimMoveHandler;     // 訂閱 replay → 標 dirty(下方歸零)
            grabber.Player.MoveEvent += pair.GrabberMoveHandler;   // 訂閱 replay → 立即轉發 = 初始吸附

            _Pairs.Add(pair);
            _PairsByActor[grabberId] = pair;
            _PairsByActor[victimId] = pair;
            pair.Dirty = false;   // 訂閱 replay 造成的 dirty 已被上一行的初始轉發覆蓋
        }

        void _OnGrabberMove(Pair pair)
        {
            _Forward(pair);
        }

        void _OnVictimMove(Pair pair)
        {
            if (pair.Forwarding)
                return;
            pair.Dirty = true;
        }

        /// <summary>
        /// 把 grabber 當下的權威 MoveInfo 轉發為 victim 的:
        /// 位置 = grabber 位置 + 錨軸 × 錨距;朝向 = -錨軸(面向 grabber);
        /// 速度 = -(grabber 位移在錨軸上的投影)—— 負速沿 -Facing 位移,
        /// 即沿錨軸正向與 grabber 同步前進(拖行時 grabber 的走向 = ActionForward = 錨軸,投影無損)。
        /// </summary>
        void _Forward(Pair pair)
        {
            var grabberPlayer = pair.Grabber.Player;
            var info = grabberPlayer.CurrentMoveInfo;
            var anchorAxis = grabberPlayer.ActionForward;
            var forwarded = new MoveInfo
            {
                Position = info.Position + anchorAxis * pair.Family.AnchorDistance,
                Facing = -anchorAxis,
                Speed = -Vector2.Dot(info.Facing, anchorAxis) * info.Speed,
                StartTicks = info.StartTicks
            };
            pair.Forwarding = true;
            pair.Victim.Player.ApplyExternalMoveInfo(forwarded);
            pair.Forwarding = false;
        }

        void _OnGrabberTransition(Pair pair, Transition transition)
        {
            var family = pair.Family;
            var action = transition.Current.Action;
            if (action == family.IdleA)
            {
                _Mirror(pair, family.IdleB);
            }
            else if (action == family.WalkA)
            {
                _Mirror(pair, family.WalkB);
            }
            else if (action == family.Atk1A)
            {
                // 不鏡射:B 側受創反應由 Damage 路由(見類註解),避免 double-trigger
            }
            else if (action == family.ThrowA)
            {
                // 丟投:起手即解體,victim 以自身朝向(-錨軸)為基底自驅 ThrowB 的烘焙飛行
                //(B clip 位移在被抓者局部空間 —— 作者視角面向 grabber,不能傳 grabber 朝向)
                var victim = pair.Victim;
                var launchBasis = -pair.Grabber.Player.ActionForward;
                _Dissolve(pair);
                victim.ForceTransition(family.ThrowB, launchBasis);
            }
            else
            {
                // 離開所屬家族(第三方打進 UnarmedDamage 等):解體釋放 victim
                var freed = pair.Victim;
                _Dissolve(pair);
                freed.ForceTransition(ActionType.UnarmedIdle, Vector2.zero);
            }
        }

        void _OnVictimTransition(Pair pair, Transition transition)
        {
            var family = pair.Family;
            var action = transition.Current.Action;
            if (action == family.IdleB)
            {
                // Atk1B 播完自然回 IdleB 時,若 grabber 正在拖行,補鏡射回 WalkB
                //(A 側早已在 WalkA,不會再有轉移事件觸發鏡射)
                if (pair.Grabber.CurrentTransition.Current.Action == family.WalkA)
                    _Mirror(pair, family.WalkB);
            }
            else if (action == family.WalkB || action == family.Atk1B)
            {
                // 配對內正常節點(鏡射結果/受創反應)
            }
            else if (action == family.BreakB)
            {
                // 掙脫(唯一由 victim 玩家輸入放行的路徑):解體,grabber 進被掙脫後搖
                var grabber = pair.Grabber;
                _Dissolve(pair);
                grabber.ForceTransition(family.BreakA, Vector2.zero);
            }
            else
            {
                // 不預期的節點(防禦):解體,雙方各自收尾
                Debug.LogWarning($"[GrabResolver] 被抓者離開配對節點({action}),防禦性解體");
                var grabber = pair.Grabber;
                _Dissolve(pair);
                grabber.ForceTransition(ActionType.UnarmedIdle, Vector2.zero);
            }
        }

        /// <summary>
        /// 鏡射 grabber 的節點變化到 victim。冪等(同節點不重 force,吸收先後抖動);
        /// victim 受創反應(Atk1B)中不打斷 —— 播完自然回 IdleB 時由 victim 側 handler 補鏡射。
        /// </summary>
        void _Mirror(Pair pair, ActionType victimNode)
        {
            var current = pair.Victim.CurrentTransition.Current.Action;
            if (current == victimNode || current == pair.Family.Atk1B)
                return;
            pair.Victim.ForceTransition(victimNode, -pair.Grabber.Player.ActionForward);
        }

        void _Dissolve(Pair pair)
        {
            if (!_Pairs.Remove(pair))
                return;   // 冪等:同幀多來源解體只作用一次
            pair.Grabber.TransitionChangedEvent -= pair.GrabberTransitionHandler;
            pair.Victim.TransitionChangedEvent -= pair.VictimTransitionHandler;
            pair.Grabber.Player.MoveEvent -= pair.GrabberMoveHandler;
            pair.Victim.Player.MoveEvent -= pair.VictimMoveHandler;
            _PairsByActor.Remove(pair.Grabber.ActorId);
            _PairsByActor.Remove(pair.Victim.ActorId);
        }
    }
}
