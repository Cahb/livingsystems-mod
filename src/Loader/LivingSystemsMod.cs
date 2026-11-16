using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Eleon;
using Eleon.Modding;

namespace LivingSystems
{
    /// <summary>
    /// Entry point, loaded in the Dedi and every PfServer. Loads logic/LivingSystems.Core.dll and
    /// logic/LivingSystems.Logic.dll from bytes (so the files stay replaceable while the server runs)
    /// and reloads them on "/ls reload". The logic folder is outside the runtime's probing path, so
    /// the only way the logic finds Core is through our AssemblyResolve handler.
    /// </summary>
    public class LivingSystemsMod : IMod, ModInterface
    {
        public const string Channel = "LivingSystems";
        const string ReloadPacket = "__reload";

        IModApi api;
        ModGameAPI legacy;
        ILogic logic;
        Assembly core;
        IPlayfield currentPlayfield;
        int generation;

        public void Init(IModApi modApi)
        {
            api = modApi;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;

            var app = api.Application;
            if (app.Mode == ApplicationMode.DedicatedServer)
            {
                app.ChatMessageSent += m => Guard("chat", () =>
                {
                    if (m?.Text != null && m.Text.Trim().Equals("/ls reload", StringComparison.OrdinalIgnoreCase)) ReloadEverywhere(m.SenderEntityId);
                    else logic?.OnChat(m);
                });
                api.Network.RegisterReceiverForPlayfieldPackets((s, pf, d) => Guard("packet", () => logic?.OnPacket(s, pf, d)));
            }
            else if (app.Mode == ApplicationMode.PlayfieldServer)
            {
                app.OnPlayfieldLoaded += pf => { currentPlayfield = pf; Guard("loaded", () => logic?.OnPlayfieldLoaded(pf)); };
                // A PfServer loads the NEXT playfield before unloading the previous one (seen on a jump: load at :27,
                // unload of the old one at :48) — only forget the current playfield if it is the one unloading.
                app.OnPlayfieldUnloading += pf => { Guard("unloading", () => logic?.OnPlayfieldUnloading(pf)); if (ReferenceEquals(currentPlayfield, pf)) currentPlayfield = null; };
                app.Update += () => Guard("update", () => logic?.OnUpdate());
                api.Network.RegisterReceiverForDediPackets((s, pf, d) =>
                {
                    if (s == Channel && d != null && Encoding.UTF8.GetString(d) == ReloadPacket) { Load(); return; }
                    Guard("packet", () => logic?.OnPacket(s, pf, d));
                });
            }
            Load();
        }

        void ReloadEverywhere(int pid)
        {
            Load();
            // Tell every playfield server that has a player on it.
            var pfs = api.Application.GetPlayerEntityIds()
                .Select(id => api.Application.GetPlayerDataFor(id)?.PlayfieldName)
                .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            foreach (var pf in pfs) api.Network.SendToPlayfieldServer(Channel, pf, Encoding.UTF8.GetBytes(ReloadPacket));
            Log($"reload #{generation} done on Dedi, sent to {pfs.Count} playfield(s): {string.Join(", ", pfs)}");
            api.Application.SendChatMessage(new MessageData
            {
                SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer,
                RecipientEntityId = pid, SenderNameOverride = "LS",
                Text = $"reloaded logic #{generation} ({(logic == null ? "FAILED, see log" : "ok")}), playfields: {pfs.Count}",
            });
        }

        void Load()
        {
            try { logic?.Stop(); } catch (Exception e) { Log("old logic Stop threw: " + e.Message); }
            logic = null;
            try
            {
                var dir = Path.Combine(Path.GetDirectoryName(typeof(LivingSystemsMod).Assembly.Location) ?? ".", "logic");
                // Core is compiled into the logic dll; an old separate LivingSystems.Core.dll is loaded only if one is still there
                var corePath = Path.Combine(dir, "LivingSystems.Core.dll");
                core = File.Exists(corePath) ? Assembly.Load(File.ReadAllBytes(corePath)) : null;
                var asm = Assembly.Load(File.ReadAllBytes(Path.Combine(dir, "LivingSystems.Logic.dll")));
                var type = asm.GetTypes().First(t => typeof(ILogic).IsAssignableFrom(t) && !t.IsAbstract);
                var l = (ILogic)Activator.CreateInstance(type);
                generation++;
                l.Start(api, legacy, Log, currentPlayfield);
                logic = l;
                Log($"logic #{generation} loaded ({asm.GetName().Version}, {File.GetLastWriteTime(Path.Combine(dir, "LivingSystems.Logic.dll")):HH:mm:ss})");
            }
            catch (Exception e) { Log("logic load FAILED: " + e); }
        }

        Assembly Resolve(object sender, ResolveEventArgs args) =>
            args.Name.StartsWith("LivingSystems.Core,", StringComparison.Ordinal) ? core :
            args.Name.StartsWith("LivingSystems,", StringComparison.Ordinal) ? typeof(LivingSystemsMod).Assembly : null;

        /// <summary>Never let a logic exception escape into the game's own event dispatch.</summary>
        void Guard(string what, Action a)
        {
            try { a(); } catch (Exception e) { Log($"{what} threw: {e}"); }
        }

        void Log(string msg) => api?.Log("[LivingSystems] " + msg);

        public void Shutdown() { Guard("stop", () => logic?.Stop()); Log("shutdown"); }

        public void Game_Start(ModGameAPI legacyApi)
        {
            legacy = legacyApi;
            // The logic may already be running (Init came first); restart it so it has the legacy API.
            if (logic != null) Load();
        }

        public void Game_Event(CmdId eventId, ushort seqNr, object data) => Guard($"Game_Event {eventId}", () => logic?.OnLegacyEvent(eventId, seqNr, data));
        public void Game_Update() { }
        public void Game_Exit() { }
    }
}
