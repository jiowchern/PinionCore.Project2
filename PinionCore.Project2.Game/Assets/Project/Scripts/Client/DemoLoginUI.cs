using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared.Users;
using UniRx;
using UnityEngine;
using UnityEngine.UI;

namespace PinionCore.Project2.Client
{
    /// <summary>
    /// 演示用登入介面:client 不掛 AutoConnector(設計決定),連線由本 UI 觸發。
    /// 連線模式依平台決定(WebGL→Web、Editor→Standalone、其他 build→Tcp),
    /// 連線物件解析方式同 Bot/ClientConsole:從 QueryerHost.Resolve() 的物件取
    /// 對應的 connector(Standalone 另需 ListenerLocator)。
    /// 登入成功(IPlayer supply)後隱藏登入面板、顯示操作說明框;
    /// 斷線(IPlayer unsupply)收回說明框並回到登入面板。
    /// </summary>
    public class DemoLoginUI : MonoBehaviour
    {
        public enum ConnectionMode
        {
            Tcp,
            Standalone,
            Web,
        }

        public PinionCore.NetSync.QueryerHost QueryerHost;

        [Tooltip("部署端點解析器;連線時以解析後端點傳入 connector,未指派時使用 connector 的 Config 內建值。")]
        public ConnectionSettingsLoader Loader;

        // 由平台推導,僅供 Inspector 檢視
        [ReadOnly] public ConnectionMode Connection;

        [Header("UI")]
        public GameObject LoginPanel;
        public GameObject ControlsPanel;
        public InputField NameInput;
        public Dropdown ModelDropdown;   // 選項順序對齊 ModelType enum 值
        public Button LoginButton;
        public Text StatusText;
        public Text ModeText;

        bool _busy;

        static ConnectionMode _DetectMode()
        {
#if UNITY_EDITOR
            return ConnectionMode.Standalone;
#elif UNITY_WEBGL
            return ConnectionMode.Web;
#else
            return ConnectionMode.Tcp;
#endif
        }

        void Start()
        {
            Connection = _DetectMode();
            ModeText.text = $"連線模式:{Connection}";
            LoginPanel.SetActive(true);
            ControlsPanel.SetActive(false);
            StatusText.text = string.Empty;
            LoginButton.onClick.AddListener(_OnLoginClicked);

            // 進出世界 → 切換面板;訂閱常駐,支援斷線後回到登入面板
            var games = from entry in QueryerHost.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                        from game in entry.Games.SupplyEvent()
                        select game;
            (from game in games
             from player in game.Player.SupplyEvent()
             select player)
                .Subscribe(_ => _OnEntered()).AddTo(this);
            (from game in games
             from player in game.Player.UnsupplyEvent()
             select player)
                .Subscribe(_ => _OnLeft()).AddTo(this);
        }

        void _OnEntered()
        {
            _busy = false;
            LoginPanel.SetActive(false);
            ControlsPanel.SetActive(true);
        }

        void _OnLeft()
        {
            _busy = false;
            ControlsPanel.SetActive(false);
            LoginPanel.SetActive(true);
            LoginButton.interactable = true;
            StatusText.text = "連線已中斷";
        }

        void _OnLoginClicked()
        {
            if (_busy)
                return;
            var name = NameInput.text.Trim();
            if (name.Length == 0)
            {
                StatusText.text = "請輸入角色名稱";
                return;
            }

            _busy = true;
            LoginButton.interactable = false;
            StatusText.text = "連線中…";

            // 先掛好 Verify 鏈再觸發連線(supply 抵達時鏈已就緒);
            // 已連線時 notifier 會回放 supply,驗證失敗重試不需重連
            (from entry in QueryerHost.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
             from verifier in entry.Verifiers.SupplyEvent()
             from result in verifier.Verify(name, (ModelType)ModelDropdown.value).RemoteValue()
             select result)
                .Take(1)
                .Subscribe(_OnVerified)
                .AddTo(this);

            StartCoroutine(_Connect());
        }

        void _OnVerified(bool accepted)
        {
            if (accepted)
            {
                StatusText.text = "進入世界中…";
                return;
            }
            _Unlock("驗證失敗:名稱已被使用");
        }

        void _Unlock(string status)
        {
            _busy = false;
            LoginButton.interactable = true;
            StatusText.text = status;
        }

        // 重試直到連上為止(伺服器尚未就緒靠下一輪補上),解析方式與 Bot 相同;
        // 已連線時迴圈條件不成立,直接結束
        System.Collections.IEnumerator _Connect()
        {
            var host = QueryerHost.Resolve().gameObject;
            if (Connection == ConnectionMode.Tcp)
            {
                var connector = host.GetComponent<PinionCore.NetSync.Tcp.TcpConnector>();
                if (connector == null)
                {
                    _Unlock($"找不到 TcpConnector({host.name})");
                    yield break;
                }
                var endPoint = Loader != null ? Loader.ResolveTcpEndPoint() : null;
                while (connector.CurrentStatus != PinionCore.NetSync.Tcp.TcpConnector.ConnectorStatus.Online)
                {
                    if (connector.CurrentStatus == PinionCore.NetSync.Tcp.TcpConnector.ConnectorStatus.Offline)
                    {
                        if (endPoint != null)
                            connector.Connect(endPoint);
                        else
                            connector.Connect();
                    }
                    yield return new WaitForSeconds(1.0f);
                }
            }
            else if (Connection == ConnectionMode.Web)
            {
                var connector = host.GetComponent<PinionCore.NetSync.Web.WebConnector>();
                if (connector == null)
                {
                    _Unlock($"找不到 WebConnector({host.name})");
                    yield break;
                }
                var url = Loader != null ? Loader.ResolveWebUrl() : null;
                while (connector.CurrentStatus != PinionCore.NetSync.Web.WebConnector.ConnectorStatus.Online)
                {
                    if (connector.CurrentStatus == PinionCore.NetSync.Web.WebConnector.ConnectorStatus.Offline)
                    {
                        if (!string.IsNullOrEmpty(url))
                            connector.Connect(url);
                        else
                            connector.Connect();
                    }
                    yield return new WaitForSeconds(1.0f);
                }
            }
            else
            {
                var connector = host.GetComponent<PinionCore.NetSync.Standalone.Connector>();
                var locator = host.GetComponent<PinionCore.NetSync.Standalone.ListenerLocator>();
                if (connector == null || locator == null)
                {
                    _Unlock($"找不到 Standalone.Connector/ListenerLocator({host.name})");
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
            }
        }
    }
}
