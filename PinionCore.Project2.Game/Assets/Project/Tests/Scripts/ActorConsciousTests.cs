using System.Collections;
using System.Linq;
using NUnit.Framework;
using UniRx;                       // First/Timeout/ToYieldInstruction 等 UniRx 擴充
using UnityEngine;
using UnityEngine.TestTools;
using PinionCore.NetSync.UniRx;    // SupplyEvent()/UnsupplyEvent()/RemoteValue()
using PinionCore.Project2.Shared;
using PinionCore.Project2.Shared.Users;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// world 端角色狀態機的能力開關端到端測試:
    /// IMoveable 由 IPlayer.Moveable 供應,Conscious/Unconscious 轉換(world 內部直接呼叫)
    /// 對 client 表現為 Supply/Unsupply 事件 —— client 由此得知當下可不可以移動。
    /// 斷言走伺服器側 Universe 的權威狀態(InternalsVisibleTo)直接觸發轉換。
    /// </summary>
    public class ActorConsciousTests
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

            // UnitySetUp 不受 [Timeout] 保護,找元件必須有界限,否則會掛死整輪
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

        /// <summary>
        /// 供應循環:進場即供應(Conscious 預設)→ ToUnconscious 收回(Unsupply)
        /// → ToConscious 重新供應(Supply)且 Move 恢復被伺服器接受。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator ConsciousToggleControlsMoveableSupplyTest()
        {
            yield return _EnterWorld("ConsciousTester");

            // 伺服器側權威 PlayerController:直接觸發狀態轉換(world 內部不走傳輸協議)
            var universe = _Scenes.FindComponent<PinionCore.Project2.Worlds.Universe>("World", "Universe");
            Assert.NotNull(universe, "World 場景應有 Universe");
            PinionCore.Project2.Worlds.PlayerController serverController = null;
            var foundController = TestWait.Until(() =>
            {
                serverController = universe.Worlds.SelectMany(w => w.ControllerItems).FirstOrDefault();
                return serverController != null;
            }, System.TimeSpan.FromSeconds(15));
            yield return foundController;
            TestWait.AssertDone(foundController, "伺服器世界應有一位權威 PlayerController");

            // 無意識:收回 IMoveable。先建等待再觸發,轉換於下一次 World.Update 生效
            var unsupply = TestWait.First(
                _PlayerGhost.Moveable.UnsupplyEvent(), System.TimeSpan.FromSeconds(15));
            serverController.ToUnconscious();
            yield return unsupply;
            TestWait.AssertDone(unsupply, "ToUnconscious 後 client 應收到 IMoveable 的 UnsupplyEvent");

            // 恢復意識:重新供應。訂閱當下 depot 為空,replay 不發射,只會等到轉換後的新 supply
            var resupply = TestWait.First(
                _PlayerGhost.Moveable.SupplyEvent(), System.TimeSpan.FromSeconds(15));
            serverController.ToConscious(PinionCore.Project2.Shared.StatusType.Adventure);
            yield return resupply;
            TestWait.AssertDone(resupply, "ToConscious 後 client 應重新收到 IMoveable 的 SupplyEvent");

            // 重新供應的 ghost 可用:Move 應被伺服器接受
            var moveResult = TestWait.First(
                resupply.Result.Move(new Vector2(0f, 1f)).RemoteValue(),
                System.TimeSpan.FromSeconds(10));
            yield return moveResult;
            TestWait.AssertDone(moveResult, "重新供應後 Move 未收到回傳值");
            Assert.IsTrue(moveResult.Result, "恢復意識後 Move 應被伺服器接受");

            resupply.Result.Stop();
        }

        IPlayer _PlayerGhost;
        IMoveable _MoveableGhost;

        // 進場流程:Verify → 取得 IPlayer → 等 IPlayer.Moveable 首次供應(Conscious 預設態)
        IEnumerator _EnterWorld(string playerName)
        {
            var verifiableSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Verifiables.SupplyEvent()),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, "連線後 client 應從 User 服務收到 IVerifiable");

            var verifyResult = TestWait.First(
                verifiableSupply.Result.Verify(playerName, CharactorType.Cube).RemoteValue(),
                System.TimeSpan.FromSeconds(10));
            yield return verifyResult;
            TestWait.AssertDone(verifyResult, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");

            var playerSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Games.SupplyEvent())
                    .SelectMany(game => game.Player.SupplyEvent()),
                System.TimeSpan.FromSeconds(15));
            yield return playerSupply;
            TestWait.AssertDone(playerSupply, "Verify 通過後 client 應收到 IPlayer");
            _PlayerGhost = playerSupply.Result;
            System.Guid actorId = _PlayerGhost.ActorId;

            // 進場即供應:Player 建構時進 Conscious,首次 World.Update 供應 IMoveable
            var moveableSupply = TestWait.First(
                _PlayerGhost.Moveable.SupplyEvent(), m => m.ActorId == actorId,
                System.TimeSpan.FromSeconds(15));
            yield return moveableSupply;
            TestWait.AssertDone(moveableSupply, "進場後 client 應收到自身的 IMoveable ghost(Conscious 預設供應)");
            _MoveableGhost = moveableSupply.Result;
        }
    }
}
