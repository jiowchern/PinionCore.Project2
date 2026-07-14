using PinionCore.NetSync.Gateways;
using UnityEngine;
using UniRx;
using System.Linq;
using PinionCore.NetSync.UniRx;
using System;
namespace PinionCore.Project2.Client
{

    public class WorldTimeHandler : MonoBehaviour
    {
        // 抽象為 QueryerHost:可掛 Client(直連)或 GatewayClient(經 Router)
        public PinionCore.NetSync.QueryerHost Client;


        public TimeSpan CurrentTime { get; private set; }

        [SerializeField, ReadOnly] private string _currentTimeDisplay; // Inspector 觀察用,勿讀取

        // 對時基準:server tick 與收到當下的本機 realtime,之後以 realtime 差值推算。
        // 不可用 Time.deltaTime 累加:場景/模型載入卡頓時 deltaTime 被 maximumDeltaTime 截斷,
        // 被吃掉的真實時間會讓時鐘永久落後,直到下一次(TimeUpdateInterval 秒後)對時才修正。
        long _baseTicks;
        double _baseRealtime;
        bool _synced;
        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            // 統一入口:只 query IUserEntry,其餘沿合約鏈(entry.Games → game.Views)取得
            var obs = from entry in Client.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                      from game in entry.Games.SupplyEvent()
                      from view in game.View.SupplyEvent()
                      from ticks in UniRx.Observable.FromEvent<long>(h => view.TimeTicksEvent += h, h => view.TimeTicksEvent -= h)
                      select ticks;

            obs.Subscribe(_UpdateCurrentTime).AddTo(this);
        }



        private void _UpdateCurrentTime(long ticks)
        {
            ObserveServerTicks(ticks);
        }

        /// <summary>
        /// 回報一個來自伺服器的時間戳(週期對時或任何事件攜帶的 ticks)。
        /// 時間戳產生於伺服器的「當下」,抵達時只會更舊不會更新,因此是伺服器目前時間的下界:
        /// 比推算值快代表先前的對時封包曾被延遲(如載入卡頓時封包排隊),以較快者重新錨定。
        /// 只往前修正,時鐘不回跳。
        /// </summary>
        public void ObserveServerTicks(long ticks)
        {
            if (_synced && ticks <= _ImpliedTicks())
                return;

            _baseTicks = ticks;
            _baseRealtime = Time.realtimeSinceStartupAsDouble;
            _synced = true;
            CurrentTime = TimeSpan.FromTicks(ticks);
        }

        long _ImpliedTicks()
        {
            var elapsed = Time.realtimeSinceStartupAsDouble - _baseRealtime;
            return _baseTicks + (long)(elapsed * TimeSpan.TicksPerSecond);
        }

        // Update is called once per frame
        void Update()
        {
            if (!_synced)
                return;
            CurrentTime = TimeSpan.FromTicks(_ImpliedTicks());
            _currentTimeDisplay = CurrentTime.ToString(@"hh\:mm\:ss\.fff");
        }
    }

}