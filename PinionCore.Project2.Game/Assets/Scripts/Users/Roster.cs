using System;
namespace PinionCore.Project2.Users
{
    class Roster
    {
        
        readonly System.Collections.Generic.Dictionary<string, Shared.ActorInfo> _Actors = new System.Collections.Generic.Dictionary<string, Shared.ActorInfo>();
        public Roster()
        {

        }
        internal Shared.ActorInfo? Register(string name)
        {
            // 如果存在則返回空
            if (_Actors.ContainsKey(name))
                return null;
            var actor = new Shared.ActorInfo();
            actor.DisplayName = name;
            actor.ModelName = "Test1";


            _Actors.Add(name, actor);
            return actor;
        }

        internal void Unregister(string name)
        {
            // 移除 info

            _Actors.Remove(name);
        }
    }

}