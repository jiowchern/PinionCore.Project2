using System;
namespace PinionCore.Project2.Users
{
    class Roster
    {
        
        readonly System.Collections.Generic.Dictionary<string, Protocols.ActorInfo> _Actors = new System.Collections.Generic.Dictionary<string, Protocols.ActorInfo>();
        public Roster()
        {

        }
        internal Protocols.ActorInfo? Register(string name)
        {
            // 如果存在則返回空
            if (_Actors.ContainsKey(name))
                return null;
            var actor = new Protocols.ActorInfo();
            actor.Name = name;
            
            _Actors.Add(name, actor);
            return actor;
        }
    }

}