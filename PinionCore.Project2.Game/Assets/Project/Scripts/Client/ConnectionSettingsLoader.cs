using System.Collections;
using System.Net;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace PinionCore.Project2.Client
{
    /// <summary>
    /// 部署連線端點解析器:啟動時自 StreamingAssets/connection.json 讀取端點,
    /// 與 ConnectionConfig 資產的內建值合併後保存在本元件(資產保持唯讀),
    /// 由 DemoLoginUI 於連線時以參數傳給 connector 的 Connect 重載,
    /// 讓部署換端點只需改 json、免重新建置。
    /// web 欄位填 "auto" 時由頁面 URL(Application.absoluteURL)推導
    /// ws(s)://&lt;頁面主機&gt;:&lt;webPort&gt;(https 頁自動用 wss);非 WebGL 平台無頁面 URL,
    /// auto 不生效、使用內建值。json 不存在或欄位缺漏 → 使用內建值。
    /// 讀取完成前鎖住登入按鈕,保證解析先於第一次連線;Editor 下跳過讀取
    /// (Editor 走 Standalone 模式不使用這些端點)。
    /// </summary>
    public class ConnectionSettingsLoader : MonoBehaviour
    {
        [System.Serializable]
        class Settings
        {
            public string web;
            public int webPort;
            public string tcpHost;
            public int tcpPort;
        }

        [Tooltip("Web 連線設定資產(LocalWebUser),提供 web 端點內建值。")]
        public PinionCore.NetSync.Web.WebConnectionConfig WebConfig;

        [Tooltip("TCP 連線設定資產(LocalTcpUser),提供 tcp 端點內建值。")]
        public PinionCore.NetSync.Tcp.TcpConnectionConfig TcpConfig;

        [Tooltip("讀取完成前鎖住的登入按鈕,避免以未解析端點搶先連線。")]
        public Button LoginButton;

        // json 解析出的覆寫值;null / <=0 表示未覆寫,回落資產內建值
        string _WebUrl;
        string _TcpHost;
        int _TcpPort;

        /// <summary>解析後的 WebSocket URL;json 未覆寫時回傳資產內建值。</summary>
        public string ResolveWebUrl()
        {
            if (!string.IsNullOrEmpty(_WebUrl))
                return _WebUrl;
            return WebConfig != null ? WebConfig.Url : null;
        }

        /// <summary>
        /// 解析後的 TCP 端點;json 未覆寫時回傳資產內建值,位址不是合法 IPv4 時回傳 null。
        /// </summary>
        public IPEndPoint ResolveTcpEndPoint()
        {
            var host = !string.IsNullOrEmpty(_TcpHost) ? _TcpHost : TcpConfig != null ? TcpConfig.Host : null;
            var port = _TcpPort > 0 ? _TcpPort : TcpConfig != null ? TcpConfig.Port : 0;
            if (string.IsNullOrEmpty(host) || port <= 0 || !IPAddress.TryParse(host, out var ip))
                return null;
            return new IPEndPoint(ip, port);
        }

        void Awake()
        {
            if (LoginButton != null)
                LoginButton.interactable = false;
        }

        IEnumerator Start()
        {
#if !UNITY_EDITOR
            var path = System.IO.Path.Combine(Application.streamingAssetsPath, "connection.json");
            UnityWebRequest request = null;
            UnityWebRequestAsyncOperation operation = null;
            try
            {
                request = UnityWebRequest.Get(path);
                operation = request.SendWebRequest();
            }
            catch (System.Exception e)
            {
                // 例:Insecure connection not allowed(HTTP 被 Player 設定封鎖)——
                // 任何失敗都不能卡死登入,使用內建端點繼續
                Debug.LogWarning($"[ConnectionSettingsLoader] 讀取 {path} 例外({e.Message}),使用內建端點。");
                if (request != null)
                    request.Dispose();
                request = null;
            }
            if (request != null)
            {
                yield return operation;
                if (request.result == UnityWebRequest.Result.Success)
                    _Apply(request.downloadHandler.text);
                else
                    Debug.LogWarning($"[ConnectionSettingsLoader] 讀取 {path} 失敗({request.error}),使用內建端點。");
                request.Dispose();
            }
#else
            yield return null;
#endif
            if (LoginButton != null)
                LoginButton.interactable = true;
        }

        void _Apply(string json)
        {
            Settings settings;
            try
            {
                settings = JsonUtility.FromJson<Settings>(json);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ConnectionSettingsLoader] connection.json 解析失敗({e.Message}),使用內建端點。");
                return;
            }
            if (settings == null)
                return;

            if (!string.IsNullOrEmpty(settings.web))
            {
                var url = settings.web == "auto" ? _DeriveFromPage(settings.webPort) : settings.web;
                if (!string.IsNullOrEmpty(url))
                    _WebUrl = url;
            }

            if (!string.IsNullOrEmpty(settings.tcpHost) && settings.tcpPort > 0)
            {
                _TcpHost = settings.tcpHost;
                _TcpPort = settings.tcpPort;
            }

            Debug.Log($"[ConnectionSettingsLoader] 端點:web={ResolveWebUrl()} tcp={ResolveTcpEndPoint()}");
        }

        // 由頁面 URL 推導 WebSocket 端點;非 WebGL(absoluteURL 為空)或 port 無效時回傳 null
        static string _DeriveFromPage(int port)
        {
            if (port <= 0)
                return null;
            if (!System.Uri.TryCreate(Application.absoluteURL, System.UriKind.Absolute, out var uri))
                return null;
            var scheme = uri.Scheme == "https" ? "wss" : "ws";
            return $"{scheme}://{uri.Host}:{port}";
        }
    }
}
