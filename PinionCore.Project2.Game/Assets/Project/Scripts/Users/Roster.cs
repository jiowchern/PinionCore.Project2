using PinionCore.Project2.Shared.Users;
using System;
namespace PinionCore.Project2.Users
{
    class Roster
    {
        // todo: 臨時配置轉換 未來要抽到外部配置
        readonly System.Collections.Generic.Dictionary<ModelType, string> _ModelTypeToName;
        readonly System.Collections.Generic.Dictionary<string, Shared.ActorInfo> _Actors = new System.Collections.Generic.Dictionary<string, Shared.ActorInfo>();
        public Roster()
        {
            _ModelTypeToName = new System.Collections.Generic.Dictionary<ModelType, string>
            {
                { ModelType.HumanM, "humanm" },
                { ModelType.Cube, "Test1" },
                { ModelType.Unitychan, "unitychan" }
            };
        }
        internal Shared.ActorInfo? Register(string name, ModelType type)
        {
            // 如果存在則返回空
            if (_Actors.ContainsKey(name))
                return null;

            if (!_ModelTypeToName.TryGetValue(type, out var modelName))
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