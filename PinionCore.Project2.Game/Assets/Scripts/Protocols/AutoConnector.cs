using PinionCore.NetSync.Tcp;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PinionCore.Project2.Protocols
{
    /// <summary>
    /// Start 時依 Type 自動連線;搭配同物件上掛有 IConnectableAgent
    /// (Client / GatewayClient / GatewayRegistry)的 Connector 皆可使用:
    /// Tcp        → 使用 TcpConnector 連線,失敗或斷線後每隔 ReconnectInterval 秒重試。
    /// Web        → 使用 WebConnector 連線,失敗或斷線後每隔 ReconnectInterval 秒重試。
    /// Standalone → 使用 StandaloneListener 指定的目標連線;未指派時從 StandaloneSceneName
    ///              場景查找(StandaloneObjectName 可指定物件名稱,留空取第一個),
    ///              失敗後每隔 ReconnectInterval 秒重試(相容場景載入順序不定)。
    /// </summary>
    public class AutoConnector : MonoBehaviour
    {
        public TcpConnector TcpConnector;
        public PinionCore.NetSync.Web.WebConnector WebConnector;
        public PinionCore.NetSync.Standalone.Connector Connecter;

        [Tooltip("Standalone 連線目標;有指派時優先使用(僅限同場景參照)。")]
        public PinionCore.NetSync.Standalone.Listener StandaloneListener;

        [Tooltip("StandaloneListener 未指派時,從此場景查找 Standalone.Listener。")]
        public string StandaloneSceneName = "World";

        [Tooltip("查找時比對掛載 Listener 的物件名稱;留空取場景中第一個。同場景有多個 Listener 時(如 RegistryEndpoint / SessionEndpoint)必須指定。")]
        public string StandaloneObjectName = "";

        public UnityEngine.Events.UnityEvent ConnectBreakEvent = new UnityEngine.Events.UnityEvent();
        public UnityEngine.Events.UnityEvent ConnectSuccessEvent = new UnityEngine.Events.UnityEvent();
        public UnityEngine.Events.UnityEvent ConnectFailedEvent = new UnityEngine.Events.UnityEvent();
        public float ReconnectInterval = 5f;

        public enum ConnectorType
        {
            None,
            Tcp,
            Standalone,
            Web
        }
        public ConnectorType Type;

        Action _Shutdown = () => { };

        void Start()
        {
            Action connect = Type switch
            {
                ConnectorType.Tcp => _StartTcp,
                ConnectorType.Web => _StartWeb,
                ConnectorType.Standalone => _StartStandalone,
                _ => _Noop,
            };
            connect();
        }

        void OnDestroy()
        {
            _Shutdown();
            _Shutdown = () => { };
        }

        void _StartTcp()
        {
            TcpConnector.ConnectResultEvent.AddListener(_OnTcpConnectResult);
            TcpConnector.ConnectBreakEvent.AddListener(_OnTcpBreak);
            _Shutdown = () =>
            {
                CancelInvoke();
                TcpConnector.ConnectResultEvent.RemoveListener(_OnTcpConnectResult);
                TcpConnector.ConnectBreakEvent.RemoveListener(_OnTcpBreak);
            };
            _ConnectTcp();
        }

        void _ConnectTcp()
        {
            TcpConnector.Connect();
        }

        void _OnTcpConnectResult(TcpConnector.ConnectResult result)
        {
            Action handler = result switch
            {
                TcpConnector.ConnectResult.ConnectSuccess => ConnectSuccessEvent.Invoke,
                _ => _FailThenRetry,
            };
            handler();
        }

        void _FailThenRetry()
        {
            ConnectFailedEvent.Invoke();
            Invoke(nameof(_ConnectTcp), ReconnectInterval);
        }

        void _OnTcpBreak()
        {
            ConnectBreakEvent.Invoke();
            Invoke(nameof(_ConnectTcp), ReconnectInterval);
        }

        void _StartWeb()
        {
            WebConnector.ConnectResultEvent.AddListener(_OnWebConnectResult);
            WebConnector.ConnectBreakEvent.AddListener(_OnWebBreak);
            _Shutdown = () =>
            {
                CancelInvoke();
                WebConnector.ConnectResultEvent.RemoveListener(_OnWebConnectResult);
                WebConnector.ConnectBreakEvent.RemoveListener(_OnWebBreak);
            };
            _ConnectWeb();
        }

        void _ConnectWeb()
        {
            WebConnector.Connect();
        }

        void _OnWebConnectResult(PinionCore.NetSync.Web.WebConnector.ConnectResult result)
        {
            Action handler = result switch
            {
                PinionCore.NetSync.Web.WebConnector.ConnectResult.ConnectSuccess => ConnectSuccessEvent.Invoke,
                _ => _WebFailThenRetry,
            };
            handler();
        }

        void _WebFailThenRetry()
        {
            ConnectFailedEvent.Invoke();
            Invoke(nameof(_ConnectWeb), ReconnectInterval);
        }

        void _OnWebBreak()
        {
            ConnectBreakEvent.Invoke();
            Invoke(nameof(_ConnectWeb), ReconnectInterval);
        }

        void _StartStandalone()
        {
            _Shutdown = CancelInvoke;
            _ConnectStandalone();
        }

        void _ConnectStandalone()
        {
            var listener = StandaloneListener != null ? StandaloneListener : _FindStandaloneListener();
            Action connect = listener != null ? () => Connecter.Connect(listener) : (Action)_Noop;
            connect();

            var resultEvent = Connecter.IsConnect() ? ConnectSuccessEvent : ConnectFailedEvent;
            resultEvent.Invoke();
            Debug.Log($"AutoConnector: Standalone connect {(Connecter.IsConnect() ? "success" : "failed")}", this);

            if (!Connecter.IsConnect())
            {
                Invoke(nameof(_ConnectStandalone), ReconnectInterval);
            }
        }

        PinionCore.NetSync.Standalone.Listener _FindStandaloneListener()
        {
            var scene = SceneManager.GetSceneByName(StandaloneSceneName);
            var roots = scene.isLoaded ? scene.GetRootGameObjects() : Array.Empty<GameObject>();
            var listeners = roots.SelectMany(root => root.GetComponentsInChildren<PinionCore.NetSync.Standalone.Listener>(true));
            return string.IsNullOrEmpty(StandaloneObjectName)
                ? listeners.FirstOrDefault()
                : listeners.FirstOrDefault(listener => listener.gameObject.name == StandaloneObjectName);
        }

        void _Noop()
        {
        }
    }
}
