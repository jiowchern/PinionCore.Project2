using PinionCore.Remote;

namespace PinionCore.Project2.Client
{
    /// <summary>
    /// 轉發型 QueryerHost:handlers 與 ClientConsole 都引用此元件,
    /// 切換 Gateway/直連只需改 Host 欄位指向不同的連線物件。
    /// </summary>
    public class QueryerHost : PinionCore.NetSync.QueryerHost
    {
        public PinionCore.NetSync.QueryerHost Host;

        public override INotifierQueryable Queryer => Host != null ? Host.Queryer : null;

        public override PinionCore.NetSync.QueryerHost Resolve()
            => Host != null && Host != this ? Host.Resolve() : this;
    }
}
