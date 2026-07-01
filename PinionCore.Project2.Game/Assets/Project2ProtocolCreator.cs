namespace PinionCore.Project2.Protocols
{
    // 由 PinionCore.Remote.Tools.Protocol.Sources Source Generator 在「本組件內」
    // 掃描所有繼承 IObject 的介面,生成完整 protocol。
    // 重要:協議介面必須與本檔位於同一個 assembly,Source Generator 只掃描當前組件。
    public static partial class Project2ProtocolCreator
    {
        public static PinionCore.Remote.IProtocol Create()
        {
            PinionCore.Remote.IProtocol protocol = null;
            _Create(ref protocol);
            return protocol;
        }

        [PinionCore.Remote.Protocol.Creator]
        static partial void _Create(ref PinionCore.Remote.IProtocol protocol);
    }
}
