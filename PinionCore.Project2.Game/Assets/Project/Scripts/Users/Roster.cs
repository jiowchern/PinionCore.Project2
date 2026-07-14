using PinionCore.Project2.Shared.Users;
using System;
namespace PinionCore.Project2.Users
{
    class Roster
    {
        // todo: 臨時配置轉換 未來要抽到外部配置
        readonly System.Collections.Generic.Dictionary<CharactorType, string> _CharactorTypeToName;
        readonly System.Collections.Generic.Dictionary<string, Shared.ActorInfo> _Actors = new System.Collections.Generic.Dictionary<string, Shared.ActorInfo>();
        public Roster()
        {
            _CharactorTypeToName = new System.Collections.Generic.Dictionary<CharactorType, string>
            {
                { CharactorType.Cube, "Test1" },
                { CharactorType.Unitychan, "unitychan" }
            };
        }
        internal Shared.ActorInfo? Register(string name, CharactorType type)
        {
            // 如果存在則返回空
            if (_Actors.ContainsKey(name))
                return null;

            if (!_CharactorTypeToName.TryGetValue(type, out var modelName))
                return null;
            var actor = new Shared.ActorInfo();
            actor.DisplayName = name;
            actor.ModelName = modelName;


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