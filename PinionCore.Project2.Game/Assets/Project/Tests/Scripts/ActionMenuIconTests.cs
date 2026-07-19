using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UniRx;
using UnityEngine;
using UnityEngine.TestTools;
using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using PinionCore.Project2.Shared.Users;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// 徑向選單圖示端到端測試(比照 ActorStanceTests 的四場景 Standalone 流程):
    /// 進戰鬥態後選單元素應以 ActionIconConfig 的圖示 prefab 呈現(元素 button 上有 ActionIcon),
    /// 而非預設文字按鈕後備。抓住中/被抓的 Transition 只由 GrabResolver 配對觸發
    ///(單一 client 無法自然抵達),改以合成 Transition 直驅 handler 的渲染路徑,
    /// 驗證同一條 _Render→_IconOf 流程對 GrabAtk1A/GrabThrowA/GrabBreakB 的圖示解析與 Loop 過濾。
    /// </summary>
    public class ActionMenuIconTests
    {
        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.QueryerHost _Client;
        bool _PreviousRunInBackground;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // 編輯器失焦時 player loop 會停住,連線流程會卡住
            _PreviousRunInBackground = Application.runInBackground;
            Application.runInBackground = true;

            _Scenes = new StandaloneSceneLoader();

            yield return _Scenes.Load("Gateway");
            yield return _Scenes.Load("World");
            yield return _Scenes.Load("User");
            yield return _Scenes.Load("Client");

            PinionCore.NetSync.QueryerHost host = null;
            PinionCore.NetSync.Standalone.Listener listener = null;
            var found = TestWait.Until(() =>
            {
                if (host == null)
                    host = _Scenes.FindClientHost();
                if (host != null && _Connector == null)
                    _Connector = host.GetComponent<PinionCore.NetSync.Standalone.Connector>();
                if (host != null && listener == null)
                {
                    var locator = host.GetComponent<PinionCore.NetSync.Standalone.ListenerLocator>();
                    if (locator != null)
                        listener = locator.Find();
                }
                return listener != null && _Connector != null;
            }, System.TimeSpan.FromSeconds(30));
            yield return found;
            TestWait.AssertDone(found, "SetUp:應在時限內從 QueryHost 解析出 Connector 與 Standalone 連線目標");
            _Client = host;

            yield return null;

            _Connector.Connect(listener);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_Connector != null && _Connector.IsConnect())
                _Connector.Disconnect();
            _Connector = null;
            _Client = null;

            yield return null;

            yield return _Scenes.UnloadAll();
            _Scenes.Dispose();
            _Scenes = null;

            Application.runInBackground = _PreviousRunInBackground;
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator GrabActionsShowIconsOnRadialMenu()
        {
            yield return _EnterWorld("MenuIconTester");

            var handler = _FindMenuHandler();
            Assert.NotNull(handler, "Client 場景應有 PlayerActionMenuHandler");

            // 圖示配置完整性:四個會上選單的抓取動作都應解析出含 Button 的圖示 prefab
            //(與 _IconOf 同一判定;缺 Button 會靜默退回文字按鈕)
            var grabActions = new[]
            {
                ActionType.GrabStart, ActionType.GrabAtk1A,
                ActionType.GrabThrowA, ActionType.GrabBreakB,
            };
            foreach (var action in grabActions)
            {
                var config = handler.IconConfigs.Find(action);
                Assert.NotNull(config, $"{action} 應有 ActionIconConfig");
                Assert.NotNull(config.Icon, $"{action} 的 ActionIconConfig.Icon 應已指派");
                Assert.NotNull(config.Icon.GetComponent<UnityEngine.UI.Button>(),
                    $"{action} 的圖示 prefab 應掛 Button(否則選單會退回文字按鈕)");
            }

            var controllableSupply = TestWait.First(
                _PlayerGhost.Controllable.SupplyEvent(),
                System.TimeSpan.FromSeconds(15));
            yield return controllableSupply;
            TestWait.AssertDone(controllableSupply, "進場後應供應 IControllable");
            var controllable = controllableSupply.Result;

            // 切戰鬥(RPC 掉失保護:逾時重訂閱+重送)
            var battleTransition = TestWait.FirstWithRetry(
                () => TestWait.TransitionEvents(controllable)
                    .Where(t => t.Current.Action == ActionType.BattleIdle),
                onAttempt: () => controllable.Play(ActionType.BattleIdle, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return battleTransition;
            TestWait.AssertDone(battleTransition, "Play(BattleIdle) 後控制狀態應切到 Current==BattleIdle");

            // 戰鬥態選單:GrabStart 與 BattleAttack 都應以圖示元素呈現(button 上有 ActionIcon)
            var battleMenu = TestWait.Until(
                () => handler.Menu.gameObject.activeSelf &&
                      _IconElement(handler, ActionType.GrabStart) != null,
                System.TimeSpan.FromSeconds(10));
            yield return battleMenu;
            TestWait.AssertDone(battleMenu, "戰鬥態選單應出現 GrabStart 圖示元素");
            Assert.NotNull(_IconElement(handler, ActionType.BattleAttack),
                "戰鬥態選單的 BattleAttack 應維持圖示呈現");

            // 抓住中選單(合成 GrabIdleA Transition 直驅渲染):
            // GrabWalkA 是循環動作應被過濾,補打/丟投以圖示呈現
            _InjectTransition(handler, ActionType.GrabIdleA,
                new[] { ActionType.GrabWalkA, ActionType.GrabAtk1A, ActionType.GrabThrowA },
                next: ActionType.GrabIdleA, damage: ActionType.BattleDamage);
            Assert.AreEqual(2, handler.Menu.elements.Count,
                "抓住中選單應只剩補打/丟投兩個一次性動作(拖行循環動作被過濾)");
            Assert.NotNull(_IconElement(handler, ActionType.GrabAtk1A), "GrabAtk1A 應以圖示呈現");
            Assert.NotNull(_IconElement(handler, ActionType.GrabThrowA), "GrabThrowA 應以圖示呈現");

            // 被抓選單(合成 GrabIdleB Transition):唯一元素 = 掙脫,且以圖示呈現
            _InjectTransition(handler, ActionType.GrabIdleB,
                new[] { ActionType.GrabBreakB },
                next: ActionType.GrabIdleB, damage: ActionType.GrabAtk1B);
            Assert.AreEqual(1, handler.Menu.elements.Count, "被抓選單應只剩掙脫一個元素");
            Assert.NotNull(_IconElement(handler, ActionType.GrabBreakB), "GrabBreakB 應以圖示呈現");
        }

        IPlayer _PlayerGhost;

        System.IObservable<IGame> _Games()
        {
            return _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                .SelectMany(entry => entry.Games.SupplyEvent());
        }

        // 進場流程:Verify → IPlayer ghost → 等 ActorProvider 建出對應殼(選單 handler 綁殼的前提)
        IEnumerator _EnterWorld(string playerName)
        {
            var verifiableSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Verifiers.SupplyEvent()),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, "連線後 client 應從 User 服務收到 IVerifier");

            var verifyResult = TestWait.First(
                verifiableSupply.Result.Verify(playerName, ModelType.Cube).RemoteValue(),
                System.TimeSpan.FromSeconds(10));
            yield return verifyResult;
            TestWait.AssertDone(verifyResult, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");

            var playerSupply = TestWait.First(
                _Games().SelectMany(game => game.Player.SupplyEvent()),
                System.TimeSpan.FromSeconds(15));
            yield return playerSupply;
            TestWait.AssertDone(playerSupply, "Verify 通過後 client 應收到 IPlayer");
            _PlayerGhost = playerSupply.Result;
            System.Guid actorId = _PlayerGhost.ActorId;

            var provider = _Scenes.FindComponent<PinionCore.Project2.Client.ActorProvider>("Client", "Handlers");
            Assert.NotNull(provider, "Client 場景應有 ActorProvider");
            var shellWait = TestWait.First(provider.SupplyEvent(), a => a.ActorId == actorId, System.TimeSpan.FromSeconds(15));
            yield return shellWait;
            TestWait.AssertDone(shellWait, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.ActorShell");
        }

        PinionCore.Project2.Client.PlayerActionMenuHandler _FindMenuHandler()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName("Client");
            if (!scene.isLoaded)
                return null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var handler = root.GetComponentInChildren<PinionCore.Project2.Client.PlayerActionMenuHandler>(true);
                if (handler != null)
                    return handler;
            }
            return null;
        }

        // 選單中指定動作的圖示元素:label 對應且 button 物件掛 ActionIcon(= 圖示 prefab 取代了文字按鈕)
        static RMF_RadialMenuElement _IconElement(
            PinionCore.Project2.Client.PlayerActionMenuHandler handler, ActionType action)
        {
            foreach (var element in handler.Menu.elements)
            {
                if (element != null && element.label == action.ToString() &&
                    element.button != null && element.button.GetComponent<ActionIcon>() != null)
                    return element;
            }
            return null;
        }

        // 合成 Transition 直驅 handler 的私有 _OnTransition(GrabIdleA/B 只由 GrabResolver
        // 配對觸發,單一 client 的 E2E 無法自然抵達):走的是與正式事件完全相同的
        // _OnTransition→_Render→_IconOf 路徑,只繞過網路來源
        static void _InjectTransition(
            PinionCore.Project2.Client.PlayerActionMenuHandler handler,
            ActionType current, ActionType[] playables, ActionType next, ActionType damage)
        {
            var transition = new Transition
            {
                Current = new PlayInfo { Action = current },
                Playables = System.Array.ConvertAll(playables, a => new PlayInfo { Action = a }),
                Next = new PlayInfo { Action = next },
                Damage = new PlayInfo { Action = damage },
            };
            var method = typeof(PinionCore.Project2.Client.PlayerActionMenuHandler)
                .GetMethod("_OnTransition", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method, "PlayerActionMenuHandler 應有 _OnTransition(重構時請同步本測試)");
            method.Invoke(handler, new object[] { transition });
        }
    }
}
