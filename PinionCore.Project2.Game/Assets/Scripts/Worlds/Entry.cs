using PinionCore.NetSync;
using PinionCore.Project2.Protocols;
using PinionCore.Project2.Worlds;
using PinionCore.Remote;
using System.Reflection;
using UnityEngine;

public class Entry : MonoBehaviour
{
    public Universe Universe;
    public Server Server;


    readonly System.Collections.Generic.Dictionary<ISessionBinder, PinionCore.Remote.ISoul> _Binders;
    public Entry()
    {
        _Binders = new System.Collections.Generic.Dictionary<ISessionBinder, PinionCore.Remote.ISoul>();
    }

    void Start()
    {
        
        
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
