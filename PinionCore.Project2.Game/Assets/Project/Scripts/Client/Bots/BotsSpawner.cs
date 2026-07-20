using System.Collections.Generic;
using UnityEngine;


namespace PinionCore.Project2.Client.Bots
{
    // 多 bot 產生器:Bot 只服務單一角色(一份連線一個玩家),
    // 這裡負責依 Count 複製/回收整組連線堆疊,並把各 Bot 的建立/移除事件匯流給 BotsMove。
    public class BotsSpawner : MonoBehaviour
    {
        class _Entry
        {
            public GameObject Instance;
            // IGame.Player 至多供應一個(Notifier 單數原則),單一槽位即可
            public QueryerHost Host;
            public Shared.IPlayer Player;
            public bool HasPlayer;
        }

        // 場景中未啟用的樣板:單一根物件內含 QueryerHost/連線元件/Bot 完整堆疊;
        // 必須未啟用,複製體才能在掛好事件轉發後再 SetActive 觸發 Bot.Start
        public GameObject Template;

        // 想要幾隻填幾隻,Update 逐幀對帳:多就建立、少就釋放
        [Min(0)]
        public int Count;

        // 對接 BotsMove.Begin/End(接線方式同原本 Bot 直連時期)
        public UnityEngine.Events.UnityEvent<QueryerHost, Shared.IPlayer> OnBotCreated = new UnityEngine.Events.UnityEvent<QueryerHost, Shared.IPlayer>();
        public UnityEngine.Events.UnityEvent<QueryerHost, Shared.IPlayer> OnBotRemoved = new UnityEngine.Events.UnityEvent<QueryerHost, Shared.IPlayer>();

        readonly List<_Entry> _Entries;

        public BotsSpawner()
        {
            _Entries = new List<_Entry>();
        }

        void Update()
        {
            while (_Entries.Count < Count)
            {
                if (!_Spawn())
                    break;
            }
            while (_Entries.Count > Count)
                _Release(_Entries[_Entries.Count - 1]);
        }

        bool _Spawn()
        {
            if (Template == null)
            {
                Debug.LogError("BotsSpawner: 未設定 Template,停用", this);
                enabled = false;
                return false;
            }
            if (Template.activeSelf)
            {
                Debug.LogError($"BotsSpawner: 樣板 {Template.name} 必須未啟用(啟用中無法在 Start 前掛事件),停用", this);
                enabled = false;
                return false;
            }

            var instance = Instantiate(Template, transform);
            var bot = instance.GetComponentInChildren<Bot>(true);
            if (bot == null)
            {
                Debug.LogError($"BotsSpawner: 樣板 {Template.name} 內找不到 Bot,停用", this);
                Destroy(instance);
                enabled = false;
                return false;
            }

            var entry = new _Entry { Instance = instance };
            bot.OnBotCreated.AddListener((host, player) =>
            {
                entry.Host = host;
                entry.Player = player;
                entry.HasPlayer = true;
                OnBotCreated.Invoke(host, player);
            });
            bot.OnBotRemoved.AddListener((host, player) =>
            {
                entry.HasPlayer = false;
                entry.Player = null;
                OnBotRemoved.Invoke(host, player);
            });
            instance.SetActive(true);
            _Entries.Add(entry);
            return true;
        }

        void _Release(_Entry entry)
        {
            _Entries.Remove(entry);
            // 主動斷線不會發 Unsupply(gateway 設計),銷毀前自行補發移除事件讓下游清理
            if (entry.HasPlayer)
                OnBotRemoved.Invoke(entry.Host, entry.Player);
            Destroy(entry.Instance);
        }

        void OnDestroy()
        {
            foreach (var entry in _Entries)
            {
                if (entry.HasPlayer)
                    OnBotRemoved.Invoke(entry.Host, entry.Player);
                Destroy(entry.Instance);
            }
            _Entries.Clear();
        }
    }
}
