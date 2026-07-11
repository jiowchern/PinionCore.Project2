using PinionCore.NetSync;
using PinionCore.Remote;
using UnityEngine;


namespace PinionCore.Project2.Users
{
    
    public class Entry : MonoBehaviour
    {
        readonly System.Collections.Generic.List<User> _Users;
        public NetSync.Client WorldAgent;
        public NetSync.Server User;

        public Entry()
        {
            _Users = new System.Collections.Generic.List<User>();
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
            var user = _Users.Find((u) => u == binder);

            if (user == null)
            {
                return;                
            }
            _Users.Remove(user);
            System.IDisposable disposable = user;
            disposable.Dispose();
        }

        private void _UserEnter(ISessionBinder binder)
        {
            PinionCore.Remote.INotifierQueryable notifier = WorldAgent.Queryer;
            var user = new User(binder, notifier);
            _Users.Add(user);
        }

        void Update()
        {
            foreach (var user in _Users)
            {
                user.Update();
            }
        }
    }
}
