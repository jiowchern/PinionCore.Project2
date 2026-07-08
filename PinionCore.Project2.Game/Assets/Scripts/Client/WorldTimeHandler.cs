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
        public GatewayClient Client;


        public TimeSpan CurrentTime { get; private set; }

        [SerializeField, ReadOnly] private string _currentTimeDisplay; // Inspector 觀察用,勿讀取
        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            var obs = from view in Client.Queryer.QueryNotifier<Shared.IView>().SupplyEvent()
                      from ticks in UniRx.Observable.FromEvent<long>(h => view.TimeTicksEvent += h, h => view.TimeTicksEvent -= h)
                      select ticks;

            obs.Subscribe(_UpdateCurrentTime).AddTo(this);
        }



        private void _UpdateCurrentTime(long ticks)
        {
            CurrentTime = TimeSpan.FromTicks(ticks);
        }

        // Update is called once per frame
        void Update()
        {
            // 推進時間,由於網路延遲,CurrentTime 可能落後於真實時間,所以每次 Update 都要推進
            CurrentTime = CurrentTime.Add(TimeSpan.FromSeconds(Time.deltaTime));
            _currentTimeDisplay = CurrentTime.ToString(@"hh\:mm\:ss\.fff");
        }
    }

}