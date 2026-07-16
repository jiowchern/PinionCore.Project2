using PinionCore.Project2.Client;
using UnityEngine;
using UniRx;
using PinionCore.NetSync.UniRx;
using System.Linq;
using PinionCore.Project2.Shared;
using System;
using PinionCore.Remote;
namespace PinionCore.NetSync.Consoles
{

    public class ClientConsoleDebughelper : MonoBehaviour
    {
        readonly System.Collections.Generic.Dictionary<Protocolable, Action> _Unregisters;
        public ClientConsole Console;
        public QueryerHost QueryerHost;
        int _CommandSn;

        ClientConsoleDebughelper()
        {
            _Unregisters = new System.Collections.Generic.Dictionary<Protocolable, Action>();
        }
        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            var controllableUnsupplyObs = from controllable in QueryerHost.Queryer.QueryNotifier<IControllable>().SupplyEvent()                                  
                                  select controllable;
            controllableUnsupplyObs.Subscribe(_Register).AddTo(this);


            var controllableSupplyObs = from controllable in QueryerHost.Queryer.QueryNotifier<IControllable>().UnsupplyEvent()
                                        select controllable;
            controllableSupplyObs.Subscribe(_Unregister).AddTo(this);
        }

        private void _Unregister(IControllable controllable)
        {
            if (_Unregisters.TryGetValue(controllable, out var unregister))
            {
                unregister();
                _Unregisters.Remove(controllable);
            }
        }

        private void _Register(IControllable controllable)
        {
            var name = $"controllable.battleidle-{_CommandSn++}";
            Console.GetCommand().Register(name, () => { 
                controllable.Play(ActionType.BattleIdle , Vector2.zero);
            });

            _Unregisters.Add(controllable, () =>
            {
                Console.GetCommand().Unregister(name);
            });
        }



        // Update is called once per frame
        void Update()
        {

        }
    }

}