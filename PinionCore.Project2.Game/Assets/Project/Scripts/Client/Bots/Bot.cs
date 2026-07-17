using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Client;
using PinionCore.Project2.Shared.Users;
using System.Linq;
using System.Runtime.CompilerServices;
using UniRx;
using UnityEngine;
using UnityEngine.Scripting;


namespace PinionCore.Project2.Client.Bots
{

    
    public class Bot : MonoBehaviour
    {
        // 一般 client 由登入 UI 觸發連線(設計決定,client 不掛 AutoConnector);
        // bot 無 UI,依此模式在 Start 自行觸發,連線物件解析方式同 ClientConsole:
        // 從 QueryerHost.Resolve() 的物件取 TcpConnector 或 Standalone.Connector+ListenerLocator
        public enum ConnectionMode
        {
            Tcp,
            Standalone,
        }

        public QueryerHost QueryerHost;
        public ConnectionMode Connection = ConnectionMode.Standalone;

        public Shared.Users.ModelType ModelType;
        
        // 欄位初始化:場景/prefab 走序列化會覆蓋;runtime AddComponent(測試生 bot)不會跑
        // 序列化初始化,沒有初始器會是 null 直接 NRE
        public UnityEngine.Events.UnityEvent<QueryerHost,Shared.IPlayer> OnBotCreated = new UnityEngine.Events.UnityEvent<QueryerHost, Shared.IPlayer>();
        public UnityEngine.Events.UnityEvent<QueryerHost, Shared.IPlayer> OnBotRemoved = new UnityEngine.Events.UnityEvent<QueryerHost, Shared.IPlayer>();


        static int _botIds = 0;
        string _Name()
        {
            return "Bot_" + _botIds++;
        }

        public void Start()
        {
            var playerSupplyObs = from entry in QueryerHost.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                                  from verify in entry.Verifiers.SupplyEvent()
                                  from result in verify.Verify(_Name(), ModelType).RemoteValue()
                                      // doacion log result
                                    .Do(value => Debug.Log($"Bot verify result: {value}"))
                                    where result
                                    from game in entry.Games.SupplyEvent()
                                    from player in game.Player.SupplyEvent()
                                    select player;    

            var playerDisposable = playerSupplyObs.Subscribe(player =>
            {
                OnBotCreated.Invoke(QueryerHost, player);
            }).AddTo(this);

            var playerUnsupplyObs = from entry in QueryerHost.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                                    from game in entry.Games.SupplyEvent()
                                    from player in game.Player.UnsupplyEvent()
                                    select player;
            var playerUnsupplyDisposable = playerUnsupplyObs.Subscribe(player =>
            {
                OnBotRemoved.Invoke(QueryerHost, player);
            }).AddTo(this);

            // 訂閱都掛好後才觸發連線,supply 事件抵達時上面的鏈已就緒
            StartCoroutine(_Connect());
        }

        // 重試直到連上為止(伺服器尚未就緒/場景還沒載入都靠下一輪補上);
        // 只負責首次連線,斷線不自動重連(重連會撞舊殼)
        System.Collections.IEnumerator _Connect()
        {
            var host = QueryerHost.Resolve().gameObject;
            if (Connection == ConnectionMode.Tcp)
            {
                var connector = host.GetComponent<PinionCore.NetSync.Tcp.TcpConnector>();
                if (connector == null)
                {
                    Debug.LogError($"Bot: {host.name} 上找不到 TcpConnector,無法連線", this);
                    yield break;
                }
                while (connector.CurrentStatus != PinionCore.NetSync.Tcp.TcpConnector.ConnectorStatus.Online)
                {
                    if (connector.CurrentStatus == PinionCore.NetSync.Tcp.TcpConnector.ConnectorStatus.Offline)
                        connector.Connect();
                    yield return new WaitForSeconds(1.0f);
                }
                Debug.Log($"Bot: tcp 已連線 ({host.name})");
            }
            else
            {
                var connector = host.GetComponent<PinionCore.NetSync.Standalone.Connector>();
                var locator = host.GetComponent<PinionCore.NetSync.Standalone.ListenerLocator>();
                if (connector == null || locator == null)
                {
                    Debug.LogError($"Bot: {host.name} 上找不到 Standalone.Connector/ListenerLocator,無法連線", this);
                    yield break;
                }
                while (!connector.IsConnect())
                {
                    var listener = locator.Find();
                    if (listener != null)
                        connector.Connect(listener);
                    if (!connector.IsConnect())
                        yield return new WaitForSeconds(1.0f);
                }
                Debug.Log($"Bot: standalone 已連線 ({host.name})");
            }
        }

    }
}