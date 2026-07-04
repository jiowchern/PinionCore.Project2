using PinionCore.NetSync;
using PinionCore.NetSync.UniRx;
using UniRx;
using UnityEngine;

namespace PinionCore.Project2.Users
{
    public class Universe
        : MonoBehaviour
    {

        public Client Client;
        public void Start()
        {
            Client.Queryer.QueryNotifier<PinionCore.Project2.Protocols.IUniverse>().SupplyEvent().Subscribe(_Bind).AddTo(this);
        }


        public void _Bind(PinionCore.Project2.Protocols.IUniverse universe)
        {
            if (universe == null)
            {
                Debug.LogError("Universe is null");
                return;
            }
            Debug.Log($"Bind Universe:{universe}");
        }

    }
}