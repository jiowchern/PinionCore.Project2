using PinionCore.NetSync;
using PinionCore.Remote;
using UnityEngine;


namespace PinionCore.Project2.Users
{
    
    public class UsersEntry : MonoBehaviour
    {
        // 以 binder 為 key:User 不實作 ISessionBinder,session 關閉時只能靠 binder 找回對應的 User 做清理
        readonly System.Collections.Generic.Dictionary<ISessionBinder, User> _Users;
        // 基底 QueryerHost 型別:場景可指派 Client(序列化)或 DirectClient/wrapper(直通),拓撲切換不動此欄位型別
        public NetSync.QueryerHost WorldAgent;
        public NetSync.Server User;

        public UsersEntry()
        {
            _Users = new System.Collections.Generic.Dictionary<ISessionBinder, User>();
        }

        void Start()
        {
            User.BinderEvent.AddListener(_BinderEventHandler);
        }

        private void OnDestroy()
        {
            User.BinderEvent.RemoveListener(_BinderEventHandler);
        }

        private void _BinderEventHandler(Server.BinderCommand cmd)
        {
            if (cmd.Status == Server.BinderCommand.OperatorStatus.Add)
            {
                _UserEnter(cmd.Binder);
            }
            else if (cmd.Status == Server.BinderCommand.OperatorStatus.Remove)
            {
                _UserLeave(cmd.Binder);
            }
        }

        private void _UserLeave(ISessionBinder binder)
        {
            if (!_Users.TryGetValue(binder, out var user))
            {
                PinionCore.Utility.Log.Instance.WriteInfo($"UsersEntry leave miss binder:{binder.GetHashCode()}");
                return;
            }
            PinionCore.Utility.Log.Instance.WriteInfo($"UsersEntry leave binder:{binder.GetHashCode()}");
            _Users.Remove(binder);
            System.IDisposable disposable = user;
            disposable.Dispose();
        }

        private void _UserEnter(ISessionBinder binder)
        {
            PinionCore.Utility.Log.Instance.WriteInfo($"UsersEntry enter binder:{binder.GetHashCode()}");
            PinionCore.Remote.INotifierQueryable notifier = WorldAgent.Queryer;
            var user = new User(binder, notifier);
            _Users.Add(binder, user);
        }

        void Update()
        {
            foreach (var user in _Users.Values)
            {
                user.Update();
            }
        }
    }
}
