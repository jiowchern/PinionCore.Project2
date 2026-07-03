using PinionCore.Remote;

namespace PinionCore.Project2.Worlds
{


    public class User : PinionCore.NetSync.Syncs.Souls.User
    {
        public Universe Universe;

        public override void Initial(ISessionBinder binder)
        {
        
        }
        public override void Final(ISessionBinder binder)
        {
       
        }
    }
}
