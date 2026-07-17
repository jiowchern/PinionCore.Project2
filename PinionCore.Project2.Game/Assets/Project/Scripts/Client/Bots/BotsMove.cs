using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using System.Linq;
using UniRx;
using UnityEngine;


namespace PinionCore.Project2.Client.Bots
{
    // 讓多個 bot 玩家在 MoveRange 內以隨機間隔遊走:
    // Begin 登錄一個玩家(訂閱 + coroutine),End/OnDestroy 釋放。
    public class BotsMove : MonoBehaviour
    {
        // 每 bot 一份的前進式時鐘(同 WorldTimeHandler 只往前校時原則):
        // MoveInfo.StartTicks 即對時來源;WorldTimeHandler 綁單一 QueryerHost,
        // 這裡管多個 bot 各自的連線,故每 bot 自持一份
        class _ForwardClock
        {
            long _baseTicks;
            double _baseRealtime;
            bool _synced;

            public void Observe(long ticks)
            {
                if (_synced && ticks <= NowTicks)
                    return;
                _baseTicks = ticks;
                _baseRealtime = Time.realtimeSinceStartupAsDouble;
                _synced = true;
            }

            public long NowTicks
            {
                get
                {
                    if (!_synced)
                        return 0;
                    var elapsed = Time.realtimeSinceStartupAsDouble - _baseRealtime;
                    return _baseTicks + (long)(elapsed * System.TimeSpan.TicksPerSecond);
                }
            }
        }

        // 單一 bot 的最新狀態:訂閱回呼寫入、coroutine 讀取(皆主執行緒)
        class _BotState
        {
            public Shared.IControllable Controllable;
            public Shared.Transition? Transition;
            public MoveInfo Move;
            public bool HasMove;
            public readonly _ForwardClock Clock = new _ForwardClock();
            // 在途 Play 單一槽位:新指令自動取消上一筆未回應的訂閱,不累積
            public readonly SerialDisposable Playing = new SerialDisposable();
        }

        readonly System.Collections.Generic.Dictionary<System.Guid, System.Action> _Releases;

        public ActorConfigSet ActorConfigSet;
        // 世界座標 XZ 平面上的遊走矩形:x=+X、y=+Z(同 MoveInfo/Play direction 的 Vector2 慣例)
        public UnityEngine.Rect MoveRange = new UnityEngine.Rect(-5, -5, 10, 10);
        public float MoveIntervalMin = 1.0f;
        public float MoveIntervalMax = 3.0f;

        public ActionType[] Walks;
        public BotsMove()
        {
            _Releases = new System.Collections.Generic.Dictionary<System.Guid, System.Action>();
        }

        public void Start()
        {
            // 走路類 = Redirectable:它就是 Move 直接起走/重定向的選型條件(ActionConfig 註解),
            // 不需另立分類欄位
            Walks = ActorConfigSet.Configs
                .SelectMany(c => c.Actions)
                .Where(a => a.Redirectable)
                .Select(a => a.Action)
                .Distinct()
                .ToArray();
        }

        public void Begin(QueryerHost queryer, Shared.IPlayer player)
        {
            System.Guid actorId = player.ActorId;
            if (_Releases.TryGetValue(actorId, out var stale))
            {
                // 不該發生:同一玩家重複 Begin 時踢掉舊登錄重來
                Debug.LogWarning($"BotsMove: 重複的 Begin, ActorId={actorId}");
                stale();
                _Releases.Remove(actorId);
            }

            var state = new _BotState();
            var disposables = new CompositeDisposable { state.Playing };

            player.Controllable.SupplyEvent()
                .Subscribe(c => state.Controllable = c)
                .AddTo(disposables);
            player.Controllable.UnsupplyEvent()
                .Subscribe(_ =>
                {
                    state.Controllable = null;
                    state.Transition = null;
                })
                .AddTo(disposables);

            // soul 端 add 即回放當前 Transition,晚訂閱安全;Switch 讓 re-supply 換訂新 soul
            player.Controllable.SupplyEvent()
                .Select(c => Observable.FromEvent<Shared.Transition>(
                    h => c.TransitionEvent += h, h => c.TransitionEvent -= h))
                .Switch()
                .Subscribe(t => state.Transition = t)
                .AddTo(disposables);

            // 自身位置來源:以 ActorId 對應自己的 IActor,MoveEvent 訂閱時 replay 當下軌跡
            player.Actors.SupplyEvent()
                .Where(a => (System.Guid)a.ActorId == actorId)
                .Select(a => Observable.FromEvent<MoveInfo>(
                    h => a.MoveEvent += h, h => a.MoveEvent -= h))
                .Switch()
                .Subscribe(info =>
                {
                    state.Clock.Observe(info.StartTicks);
                    state.Move = info;
                    state.HasMove = true;
                })
                .AddTo(disposables);

            var wander = StartCoroutine(_Wander(state));
            _Releases.Add(actorId, () =>
            {
                StopCoroutine(wander);
                disposables.Dispose();
            });
        }

        public void End(QueryerHost queryer, Shared.IPlayer player)
        {
            System.Guid actorId = player.ActorId;
            if (!_Releases.TryGetValue(actorId, out var release))
            {
                Debug.LogWarning($"BotsMove: End 找不到登錄, ActorId={actorId}");
                return;
            }
            release();
            _Releases.Remove(actorId);
        }

        System.Collections.IEnumerator _Wander(_BotState state)
        {
            while (true)
            {
                yield return new WaitForSeconds(Random.Range(MoveIntervalMin, MoveIntervalMax));

                var controllable = state.Controllable;
                if (controllable == null || !state.Transition.HasValue)
                    continue;

                // 白名單裡的走路動作:Playables 含 locomotion 自身(= 重定向),
                // 不可走的狀態(如攻擊中白名單為空)自然跳過本輪
                var walk = ActionType.None;
                foreach (var playable in state.Transition.Value.Playables)
                {
                    if (System.Array.IndexOf(Walks, playable.Action) < 0)
                        continue;
                    walk = playable.Action;
                    break;
                }
                if (walk == ActionType.None)
                    continue;

                var direction = _ToRandomTarget(state);
                if (direction.sqrMagnitude < 0.0001f)
                    continue;

                // fire-and-forget:接受與否一律由伺服器白名單裁決,被拒就等下一輪
                state.Playing.Disposable = controllable
                    .Play(walk, direction.normalized).RemoteValue()
                    .Subscribe(_ => { }, e => Debug.Log(e));
            }
        }

        // 朝 MoveRange 內隨機目標點的方向;尚無自身軌跡時退化為隨機方向
        Vector2 _ToRandomTarget(_BotState state)
        {
            var target = new Vector2(
                Random.Range(MoveRange.xMin, MoveRange.xMax),
                Random.Range(MoveRange.yMin, MoveRange.yMax));
            if (!state.HasMove)
                return Random.insideUnitCircle;

            // client 時間可能落後 StartTicks,clamp 為 0 視同還在起點
            var elapsed = (state.Clock.NowTicks - state.Move.StartTicks) / (double)System.TimeSpan.TicksPerSecond;
            if (elapsed < 0)
                elapsed = 0;
            MoveSampler.Sample(state.Move, elapsed, out var position, out _);
            return target - position;
        }

        void OnDestroy()
        {
            foreach (var release in _Releases.Values)
                release();
            _Releases.Clear();
        }
    }
}
