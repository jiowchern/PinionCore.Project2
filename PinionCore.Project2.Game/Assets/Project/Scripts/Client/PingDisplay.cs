using UnityEngine;
using UnityEngine.UI;

namespace PinionCore.Project2.Client
{
    /// <summary>
    /// 演示用 ping 顯示:定期從 QueryerHost.Resolve() 的連線物件讀 Ping(秒)換算毫秒。
    /// 讀取方式同 ClientConsole 的 ping 指令(Client 直連或 GatewayClient 二擇一)。
    /// </summary>
    public class PingDisplay : MonoBehaviour
    {
        public PinionCore.NetSync.QueryerHost QueryerHost;
        public Text Label;

        [SerializeField] float UpdateInterval = 0.5f;

        float _nextUpdate;

        void Update()
        {
            if (Time.unscaledTime < _nextUpdate)
                return;
            _nextUpdate = Time.unscaledTime + UpdateInterval;

            var ping = _ReadPing();
            Label.text = ping.HasValue
                ? $"Ping {Mathf.RoundToInt(ping.Value * 1000f)} ms"
                : "Ping --";
        }

        float? _ReadPing()
        {
            if (QueryerHost == null)
                return null;
            var host = QueryerHost.Resolve();
            if (host is PinionCore.NetSync.Client client)
                return client.Ping;
            if (host is PinionCore.NetSync.Gateways.GatewayClient gateway)
                return gateway.Ping;
            return null;
        }
    }
}
