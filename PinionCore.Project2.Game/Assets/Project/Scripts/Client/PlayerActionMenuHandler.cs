using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using System;
using System.Collections.Generic;
using UniRx;
using UnityEngine;

namespace PinionCore.Project2.Client
{
    /// <summary>
    /// 行動選單表現層:訂閱 IControllable.TransitionEvent(經 PlayerRemote 快取),
    /// 每次轉移把 Transition.Playables 重建成放射選單,點擊元素即 Play 該動作
    ///(伺服器以當下白名單驗證);選單中心顯示 Current 動作名。
    /// 循環動作(ActionConfig.Loop)不上圓盤:移動類(Redirectable)由 WASD 專責、
    /// idle 類(駐留/姿態切換)由 Stop/R 鍵專責 —— 以能力欄位過濾,不硬編碼 ActionType,
    /// 圓盤只留一次性動作(攻擊等)。
    ///
    /// 查表需要殼的 FindAction(與伺服器同一份資產),故沿用輸入層的殼解析模式;
    /// 並接手原 PlayerAttackHandler 的職責:一次性動作結束(循環動作抵達)時
    /// 通知 PlayerInputHandler 補送按住中的移動(邊緣觸發不會自己補)。
    ///
    /// RMF_RadialMenu 元件保持 disabled:其 Update 走舊版 Input(專案為 Input System only,
    /// 執行會丟例外),佈局改由 Rebuild() 顯式驅動。方向選擇(lazy selection)不用 RMF
    /// 內建版,在本類 Update 以 Input System 的 Pointer 重實作(角度→索引與 RMF 同式):
    /// 游標離開中心死區即依方位鎖定元素(放大+染色+中心標籤提示),按下即觸發,
    /// 不必精準移到按鈕上;useLazySelection 設 true 讓元素關閉射線,
    /// 點擊只走本類單一路徑,避免與 uGUI Button 射線雙重觸發。
    /// </summary>
    public class PlayerActionMenuHandler : MonoBehaviour
    {
        // 抽象為 QueryerHost:可掛 Client(直連)或 GatewayClient(經 Router)
        public PinionCore.NetSync.QueryerHost Client;
        public ActorProvider Provider;
        public PlayerRemote ClientPlayer;
        public PlayerInputHandler InputHandler;
        public RMF_RadialMenu Menu;
        // 元素模板:掛在 Menu 底下的 inactive 物件,含 RMF_RadialMenuElement 與已接線的 Button
        public RMF_RadialMenuElement ElementTemplate;
        // 動作圖示配置:查到含 Button 的圖示 prefab 就取代預設按鈕,查無沿用預設文字按鈕
        public ActionIconConfigSet IconConfigs;

        [Header("方向選擇(lazy selection)")]
        [Tooltip("死區半徑(canvas 單位):游標距圓心小於此值不選任何動作")]
        public float DeadZoneRadius = 40f;
        [Tooltip("有效半徑(canvas 單位):游標距圓心超過此值視為不在選單上")]
        public float ActivationRadius = 300f;
        [Tooltip("被指向元素的放大倍率")]
        public float HighlightScale = 1.2f;
        [Tooltip("被指向元素的染色")]
        public Color HighlightColor = new Color(1f, 0.85f, 0.35f);

        ActorShell _shell;
        readonly UniRx.CompositeDisposable _shellSubscriptions = new UniRx.CompositeDisposable();
        readonly List<RMF_RadialMenuElement> _elements = new List<RMF_RadialMenuElement>();
        Transition? _transition;
        bool _actionActive;
        IDisposable _transitionSubscription;
        int _aimedIndex = -1;
        Vector3 _aimedOriginalScale;
        Color _aimedOriginalColor;

        void Start()
        {
            Menu.gameObject.SetActive(false);
            // 元素在 Start 依此旗標關閉射線(CanvasGroup.blocksRaycasts=false),
            // 點擊改由本類 Update 的方向選擇單一路徑觸發;場景序列化值不可信,這裡強制
            Menu.useLazySelection = true;

            // 與 PlayerInputHandler 相同的解析模式:每次 IPlayer supply 都重新解析本地殼
            var games = from entry in Client.Queryer.QueryNotifier<Shared.IUserEntry>().SupplyEvent()
                        from game in entry.Games.SupplyEvent()
                        select game;

            var bind = from game in games
                       from player in game.Player.SupplyEvent()
                       select _ResolveShell(player.ActorId);
            bind.Switch().Subscribe(_Bind).AddTo(this);

            (from game in games
             from player in game.Player.UnsupplyEvent()
             select player)
                .Subscribe(_ => _Unbind()).AddTo(this);

            Provider.UnsupplyEvent()
                .Where(shell => shell == _shell)
                .Subscribe(_ => _Unbind()).AddTo(this);

            _transitionSubscription = ClientPlayer.SubscribeTransition(_OnTransition);
        }

        IObservable<ActorShell> _ResolveShell(Guid actorId)
        {
            return from shell in Provider.SupplyEvent().Where(s => s.ActorId == actorId).Take(1)
                   from _ in shell.gameObject
                       .ObserveEveryValueChanged(g => g.activeSelf)
                       .Where(active => active).Take(1)
                   select shell;
        }

        void _Bind(ActorShell shell)
        {
            _shellSubscriptions.Clear();
            _shell = shell;

            Observable.FromEvent<ActionInfo>(h => shell.ActionEvent += h, h => shell.ActionEvent -= h)
                .Subscribe(_OnActionEvent)
                .AddTo(_shellSubscriptions);

            _Render();
        }

        void _OnActionEvent(ActionInfo info)
        {
            // 一次性動作(非 Loop)佔用;查無 config 的未知動作保守視為佔用。
            // 循環動作抵達 = 前一個一次性動作已結束;None 只剩晚訂閱 replay 的哨兵,一併視為解鎖
            var config = _shell != null ? _shell.FindAction(info.Action) : null;
            var occupies = info.Action != ActionType.None && (config == null || !config.Loop);
            if (occupies)
            {
                _actionActive = true;
                return;
            }
            if (_actionActive && InputHandler != null)
                InputHandler.ForceResend();
            _actionActive = false;
        }

        void _Unbind()
        {
            _shellSubscriptions.Clear();
            _shell = null;
            _actionActive = false;
            _transition = null;
            _Render();
        }

        void _OnTransition(Transition transition)
        {
            _transition = transition;
            _Render();
        }

        // 重建選單 = f(殼, 最新 Transition):Transition 常在殼解析完成前先抵達(快取回放),
        // 兩訊號任一更新都重算;殼未綁定時藏起(查不了 ActionConfig)
        void _Render()
        {
            // 元素整批重建,指向狀態直接歸零(舊元素即將銷毀,不必還原外觀)
            _aimedIndex = -1;
            foreach (var element in _elements)
                Destroy(element.gameObject);
            _elements.Clear();
            Menu.elements.Clear();

            if (_shell == null || _transition == null)
            {
                Menu.gameObject.SetActive(false);
                return;
            }

            var transition = _transition.Value;
            foreach (var playable in transition.Playables)
            {
                var action = playable.Action;
                // 循環動作(移動/idle)不上圓盤,未來新增者由能力欄位自然排除
                var config = _shell.FindAction(action);
                if (config != null && config.Loop)
                    continue;

                // 模板是 inactive,clone 也以 inactive 出生:按鈕替換須在 SetActive 前完成,
                // RMF_RadialMenuElement.Awake 才不會抓到舊按鈕
                var element = Instantiate(ElementTemplate, Menu.transform);

                var icon = _IconOf(action);
                if (icon != null)
                {
                    // 圖示 prefab 接手預設按鈕的環上位置;ForceDirection 抵銷根物件旋轉保持正立
                    var slot = (RectTransform)element.button.transform;
                    var iconInstance = Instantiate(icon, element.transform);
                    var iconRt = (RectTransform)iconInstance.transform;
                    iconRt.anchoredPosition = slot.anchoredPosition;
                    iconInstance.gameObject.AddComponent<RMF_ForceDirection>();
                    Destroy(element.button.gameObject);
                    element.button = iconInstance.GetComponent<UnityEngine.UI.Button>();
                }
                else
                {
                    var text = element.button.GetComponentInChildren<UnityEngine.UI.Text>();
                    if (text != null)
                        text.text = action.ToString();
                }

                element.gameObject.SetActive(true);
                element.label = action.ToString();
                // 方向在點擊當下讀取:按住 WASD 出招,帶位移動作(翻滾等)朝輸入方向執行;
                // 零向量(未按方向鍵)= 伺服器 fallback 當前朝向
                element.button.onClick.AddListener(() => ClientPlayer.Play(
                    action,
                    InputHandler != null ? InputHandler.HeldWorldDirection : Vector2.zero,
                    null));
                _elements.Add(element);
                Menu.elements.Add(element);
            }

            if (Menu.textLabel != null)
                Menu.textLabel.text = transition.Current.Action.ToString();

            // 過濾後為空(如攻擊/受傷中)整組藏起;Rebuild 須在元素清單就緒後呼叫,
            // 角度設定先於元素 Start(旋轉在 Start 套用)
            Menu.gameObject.SetActive(_elements.Count > 0);
            if (_elements.Count > 0)
                Menu.Rebuild();
        }

        // 方向選擇:游標相對圓心的方位即選擇,毋須移到按鈕上;
        // 角度→索引換算與 RMF_RadialMenu.Update 同式(該元件因走舊版 Input 停用)
        void Update()
        {
            if (_elements.Count == 0 || !Menu.gameObject.activeInHierarchy)
                return;

            var pointer = UnityEngine.InputSystem.Pointer.current;
            if (pointer == null)
                return;

            var menuRt = (RectTransform)Menu.transform;
            // ScreenSpaceOverlay:rt.position 即螢幕像素;除以 lossyScale 還原 canvas 單位
            var offset = (pointer.position.ReadValue() - (Vector2)menuRt.position) / menuRt.lossyScale.x;
            var distance = offset.magnitude;
            if (distance < DeadZoneRadius || distance > ActivationRadius)
            {
                _Aim(-1);
                return;
            }

            var slice = 360f / _elements.Count;
            var rawAngle = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
            var clockwiseFromTop = Mathf.Repeat(-rawAngle + 90f - Menu.globalOffset + slice / 2f, 360f);
            var index = Mathf.Clamp((int)(clockwiseFromTop / slice), 0, _elements.Count - 1);
            _Aim(index);

            if (pointer.press.wasPressedThisFrame)
                _elements[index].button.onClick.Invoke();
        }

        // 指向提示:被指向元素放大+染色、中心標籤顯示動作名;離開範圍還原並顯示 Current
        void _Aim(int index)
        {
            if (index == _aimedIndex)
                return;

            if (_aimedIndex >= 0 && _aimedIndex < _elements.Count &&
                _elements[_aimedIndex] != null && _elements[_aimedIndex].button != null)
            {
                var previous = _elements[_aimedIndex].button;
                previous.transform.localScale = _aimedOriginalScale;
                if (previous.targetGraphic != null)
                    previous.targetGraphic.color = _aimedOriginalColor;
            }

            _aimedIndex = index;

            if (index < 0)
            {
                if (Menu.textLabel != null && _transition != null)
                    Menu.textLabel.text = _transition.Value.Current.Action.ToString();
                return;
            }

            var button = _elements[index].button;
            _aimedOriginalScale = button.transform.localScale;
            button.transform.localScale = _aimedOriginalScale * HighlightScale;
            if (button.targetGraphic != null)
            {
                _aimedOriginalColor = button.targetGraphic.color;
                button.targetGraphic.color = HighlightColor;
            }
            if (Menu.textLabel != null)
                Menu.textLabel.text = _elements[index].label;
        }

        // 可用的圖示 prefab:配置存在且 prefab 上有 Button(點擊接線的必要條件)才算,
        // 否則回 null 走預設文字按鈕
        ActionIcon _IconOf(ActionType action)
        {
            var config = IconConfigs != null ? IconConfigs.Find(action) : null;
            if (config == null || config.Icon == null)
                return null;
            return config.Icon.GetComponent<UnityEngine.UI.Button>() != null ? config.Icon : null;
        }

        void OnDestroy()
        {
            _transitionSubscription?.Dispose();
            _shellSubscriptions.Dispose();
        }
    }
}
