using UnityEngine;

namespace PinionCore.Project2.Protocols
{
    [CreateAssetMenu(menuName = "PinionCore/NetSync/Project2 Protocol Provider", fileName = "Project2Protocol")]
    public class Project2ProtocolProvider : PinionCore.NetSync.ProtocolProvider
    {
        readonly PinionCore.Remote.IProtocol _Protocol;

        public Project2ProtocolProvider()
        {
            _Protocol = Project2ProtocolCreator.Create();
        }

        public override PinionCore.Remote.IProtocol Get()
        {
            return _Protocol;
        }
    }
}
