using Eleon;
using Eleon.Modding;

namespace LivingSystems
{
    /// <summary>
    /// Contract between the stable loader and the reloadable logic. The loader owns every game
    /// subscription (some, like network receivers, can never be unregistered) and forwards here.
    /// Changing this interface DOES need a server restart; changing the logic does not.
    /// </summary>
    public interface ILogic
    {
        /// <param name="playfield">PfServer: the playfield already loaded, if any (on reload). Null on Dedi.</param>
        void Start(IModApi api, ModGameAPI legacy, System.Action<string> log, IPlayfield playfield);
        void Stop();

        void OnLegacyEvent(CmdId id, ushort seqNr, object data);
        void OnChat(MessageData m);
        void OnUpdate();
        void OnPlayfieldLoaded(IPlayfield pf);
        void OnPlayfieldUnloading(IPlayfield pf);
        void OnPacket(string sender, string playfieldName, byte[] data);
    }
}
