using PinionCore.NetSync;
using PinionCore.NetSync.Tcp;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PinionCore.Project2.Users
{
    /// <summary>
    /// Start 時依 Type 自動連線:
    /// Tcp        → 使用 TcpConnector 連線,失敗或斷線後每隔 ReconnectInterval 秒重試。
    /// Standalone → 從 World scene 取得 Listener 直接連線,取不到即視為連線失敗,不重試。
    /// </summary>
    public class AutoConnector : MonoBehaviour
    {
        public Client WorldAgent;
        public TcpConnector TcpConnector;
        public PinionCore.NetSync.Standalone.Connector Connecter;
        public UnityEngine.Events.UnityEvent ConnectBreakEvent = new UnityEngine.Events.UnityEvent();
        public UnityEngine.Events.UnityEvent ConnectSuccessEvent = new UnityEngine.Events.UnityEvent();
        public UnityEngine.Events.UnityEvent ConnectFailedEvent = new UnityEngine.Events.UnityEvent();
        public float ReconnectInterval = 5f;
        public string WorldSceneName = "World";

        public enum ConnectorType
        {
            None,
            Tcp,
            Standalone
        }
        public ConnectorType Type;

        Action _Shutdown = () => { };

        void Start()
        {
            Action connect = Type switch
            {
                ConnectorType.Tcp => _StartTcp,
                ConnectorType.Standalone => _ConnectStandalone,
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

        void _ConnectStandalone()
        {
            var listener = _FindWorldListener();
            Action connect = listener != null ? () => Connecter.Connect(listener) : (Action)_Noop;
            connect();

            var resultEvent = Connecter.IsConnect() ? ConnectSuccessEvent : ConnectFailedEvent;
            resultEvent.Invoke();
            Debug.Log($"AutoConnector: Standalone connect {(Connecter.IsConnect() ? "success" : "failed")}");
        }

        PinionCore.NetSync.Standalone.Listener _FindWorldListener()
        {
            var scene = SceneManager.GetSceneByName(WorldSceneName);
            var roots = scene.isLoaded ? scene.GetRootGameObjects() : Array.Empty<GameObject>();
            return roots
                .Select(root => root.GetComponentInChildren<PinionCore.NetSync.Standalone.Listener>(true))
                .FirstOrDefault(listener => listener != null);
        }

        void _Noop()
        {
        }
    }
}
