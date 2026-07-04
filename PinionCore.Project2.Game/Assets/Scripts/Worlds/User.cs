using PinionCore.NetSync.Syncs.Souls;
using PinionCore.Remote;

namespace PinionCore.Project2.Worlds
{


    public class User : PinionCore.NetSync.Syncs.Souls.User
    {
        public Universe Universe;

        readonly System.Collections.Generic.List<PinionCore.Remote.ISoul> _Souls;

        public User()
        {
            _Souls = new System.Collections.Generic.List<PinionCore.Remote.ISoul>();
        }

        public override void Initial(ISessionBinder binder)
        {
            _Souls.Add(binder.Bind<PinionCore.Project2.Protocols.IUniverse>(Universe));

            gameObject.SetActive(true);
        }
        public override void Final(ISessionBinder binder)
        {
            foreach (var soul in _Souls)
            {
                binder.Unbind(soul);
            }
            _Souls.Clear();
            gameObject.SetActive(false);
        }
    }
}
