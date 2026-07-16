using PinionCore.NetSync;
using PinionCore.Project2.Shared;
using PinionCore.Remote;
using System.Reflection;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    public class WorldsEntry : MonoBehaviour
    {
        public Universe Universe;
        public Server Server;


        readonly System.Collections.Generic.Dictionary<ISessionBinder, PinionCore.Remote.ISoul> _Binders;
        public WorldsEntry()
        {
            _Binders = new System.Collections.Generic.Dictionary<ISessionBinder, PinionCore.Remote.ISoul>();
        }

        public void OnBinderCommand(Server.BinderCommand command)
        {
            if (command.Status == Server.BinderCommand.OperatorStatus.Add)
            {
                var soul = command.Binder.Bind<IUniverse>(Universe);
                _Binders.Add(command.Binder, soul);

            }
            else if (command.Status == Server.BinderCommand.OperatorStatus.Remove)
            {
                if (_Binders.TryGetValue(command.Binder, out var soul))
                {
                    command.Binder.Unbind(soul);
                    _Binders.Remove(command.Binder);
                }
            }
        }
    }
}
