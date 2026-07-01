using System.Collections.Generic;
using System.Text;

namespace PinionCore.Project2.Protocols.Worlds
{
    public interface IWorld : PinionCore.Remote.Protocolable
    {
        
        PinionCore.Remote.Value<bool> LoadTerrain();
       




    }
}
