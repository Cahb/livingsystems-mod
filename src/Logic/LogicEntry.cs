using System;
using System.Collections.Generic;
using System.Linq;
using Eleon;
using Eleon.Modding;
using LivingSystems.Core;

namespace LivingSystems
{
    /// <summary>Created by the loader on every (re)load. Applies the save guard, then routes by process.</summary>
    public sealed class LogicEntry : ILogic
    {
        IModApi api;
        Action<string> log;
        DediSpike dedi;
        PfSpike pf;
        FlightDriver fly;
        DediRegistry registry;

        Core.Sim.Galaxy pfGalaxy; bool pfGalaxyTried;
        /// <summary>The save's sector files on this PfServer (for the LCD maps): parsed once, null if unreadable.</summary>
        Core.Sim.Galaxy PfGalaxy()
        {
            if (pfGalaxyTried) return pfGalaxy;
            pfGalaxyTried = true;
            try
            {
                var dir = System.IO.Path.Combine(api.Application.GetPathFor(AppFolder.SaveGame), "Sectors");
                var files = System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "*.yaml") : new string[0];
                pfGalaxy = Core.Sim.Galaxy.Parse(files.Select(f => System.IO.File.ReadAllText(f, System.Text.Encoding.UTF8)));
                log($"LCD map: {pfGalaxy.Stars.Count} stars, {pfGalaxy.SectorPos.Count} placed playfields from {files.Length} file(s)");
            }
            catch (Exception e) { log("LCD map: sector files unreadable: " + e.Message); }
            return pfGalaxy;
        }
        DediSim sim;
        HailDisplay lcd;
        SimLive simLive;

        public void Start(IModApi api, ModGameAPI legacy, Action<string> log, IPlayfield playfield)
        {
            this.api = api; this.log = log;
            string path = null;
            try { path = api.Application.GetPathFor(AppFolder.SaveGame); } catch (Exception e) { log("GetPathFor failed: " + e.Message); }
            var active = SaveGuard.IsAllowed(path);
            log($"logic start mode={api.Application.Mode} legacy={(legacy != null)} save='{path}' -> {(active ? "ACTIVE" : "inert")}");
            if (!active) return;

            gameEvent = (GameEventType t, object a1, object a2, object a3, object a4, object a5) =>
            {
                try { log($"V2 GAMEEVENT [{api.Application.Mode}] {t} a1={Fmt(a1)} a2={Fmt(a2)} a3={Fmt(a3)} a4={Fmt(a4)} a5={Fmt(a5)}"); }
                catch { }
                try { fly?.OnGameEvent(t, a1, a2); } catch (Exception ex) { log("fly game event threw: " + ex); }
            };
            api.GameEvent += gameEvent;

            if (api.Application.Mode == ApplicationMode.DedicatedServer) { registry = new DediRegistry(api, log); dedi = new DediSpike(api, log); dedi.OnSpawned = registry.RegisterSpawn; dedi.OnDestroyed = registry.Forget;
                if (legacy != null) dedi.AttachLegacy(legacy);
                try { sim = new DediSim(api, log, dedi); registry.OnDeparted = sim.OnDeparted; dedi.OnSim = (pid, cmd) => sim.Command(pid, cmd, dedi.Say); dedi.TalkFor = sim.Talk; dedi.ShipDealFor = sim.ShipDeal; dedi.TradeLineFor = sim.TradeLine; dedi.TollFor = sim.PirateToll; dedi.TollPaid = sim.TollPaid; dedi.BountyFor = sim.BountyOfferText; dedi.AcceptBountyFor = sim.AcceptBounty; dedi.CollectFor = sim.CollectBounties; }
                catch (Exception e) { log("sim failed to start: " + e); }
            }
            else if (api.Application.Mode == ApplicationMode.PlayfieldServer) { pf = new PfSpike(api, log); fly = new FlightDriver(api, log); fly.Escort = new EscortRun(fly, log); simLive = new SimLive(fly, log); simLive.OnHail = pf.Hail; fly.Escort.BoundFor = simLive.BoundFor; fly.Escort.Memory = simLive.MemoryOf; fly.Escort.WarpPointFor = simLive.WarpPointOf; fly.Escort.AskFor = simLive.EscortAskOf; pf.ShipStatus = fly.StatusOf; pf.ShipSays = simLive.Says; pf.IsOurShip = fly.IsOurs; pf.FeedEvent = fly.AddEvent; lcd = new HailDisplay(log, fly) { Traffic = simLive.Transponders, GalaxyFor = PfGalaxy }; if (playfield != null) { loadedPfs.Add(playfield); attached = playfield; pf.OnLoaded(playfield); fly.OnLoaded(playfield); lcd.OnLoaded(playfield); simLive.OnLoaded(playfield); } }
        }

        /// <summary>"pid|fly|args..." goes to the flight driver; everything else to the spike.</summary>
        bool TryFly(string sender, byte[] data)
        {
            if (!Wire.IsOurs(sender)) return false;
            var f = Wire.Unpack(data);
            if (f.Length < 2 || f[1] != "fly" || !int.TryParse(f[0], out var pid)) return false;
            if (f.Length >= 3 && f[2] == "sim") { try { simLive?.Handle(f.Skip(3).ToArray()); } catch (Exception e) { log("sim live threw: " + e); } return true; }
            try { fly.Handle(pid, f.Skip(2).ToArray()); } catch (Exception e) { log("fly cmd threw: " + e); }
            return true;
        }

        GameEventDelegate gameEvent;
        static string Fmt(object o) => o == null ? "-" : (o is IEntity e ? $"entity {e.Id} '{e.Name}' {e.Type}" : $"{o.GetType().Name}:{o}");

        public void Stop()
        {
            if (gameEvent != null) api.GameEvent -= gameEvent;
            fly?.Stop(); pf?.Stop(); dedi?.Stop(); log?.Invoke("logic stop");
        }

        public void OnLegacyEvent(CmdId id, ushort seqNr, object data) { dedi?.OnUpdate(); dedi?.OnLegacyEvent(id, seqNr, data); }
        public void OnChat(MessageData m) => dedi?.OnChat(m);
        public void OnUpdate()
        {
            pf?.OnUpdate();
            dedi?.OnUpdate();
            // separate guards: a fault in one system doesn't stop the others (it used to stop ALL flight until a reload)
            try { fly?.OnUpdate(); } catch (Exception e) { Throttled("fly update", e); }
            try { fly?.Escort?.Tick(fly.Now); } catch (Exception e) { Throttled("escort tick", e); }
            try { if (fly != null) simLive?.Tick(fly.Now); } catch (Exception e) { Throttled("sim live tick", e); }
            try { if (fly != null) lcd?.OnUpdate(fly.Now); } catch (Exception e) { log("lcd update threw: " + e.Message); }
        }
        readonly Dictionary<string, DateTime> threw = new Dictionary<string, DateTime>();
        void Throttled(string what, Exception e)
        {
            if (threw.TryGetValue(what, out var t) && (DateTime.UtcNow - t).TotalSeconds < 60) return;
            threw[what] = DateTime.UtcNow; log($"{what} threw (once a minute shown): {e}");
        }

        // A PfServer can hold several playfields (a `sector` hop to a star loads it next to the one you're on). We work on
        // the newest; when IT unloads and another is still loaded (you came back), go back to that one — else the mod
        // sleeps on a playfield it never hears about again (no flight, no live ships, no simtick: the Dedi sim froze).
        readonly List<IPlayfield> loadedPfs = new List<IPlayfield>();
        IPlayfield attached;
        public void OnPlayfieldLoaded(IPlayfield p)
        {
            loadedPfs.Remove(p); loadedPfs.Add(p); attached = p;
            pf?.OnLoaded(p); fly?.OnLoaded(p); lcd?.OnLoaded(p); simLive?.OnLoaded(p);
        }
        public void OnPlayfieldUnloading(IPlayfield p)
        {
            loadedPfs.Remove(p);
            if (attached != null && !ReferenceEquals(attached, p)) return;   // not the one we work on: nothing of ours there
            lcd?.OnUnloading(p); fly?.OnUnloading(p); pf?.OnUnloading(p);
            attached = null;
            if (loadedPfs.Count == 0) return;
            var back = loadedPfs[loadedPfs.Count - 1];
            log?.Invoke($"'{p?.Name}' unloading, '{back.Name}' still loaded here: back on it");
            OnPlayfieldLoaded(back);
        }
        public void OnPacket(string sender, string playfieldName, byte[] data)
        {
            dedi?.OnUpdate();   // the Dedi has no frame update: delayed work runs off incoming traffic (≤ ~15 s)
            if (dedi != null && sim != null && Wire.IsOurs(sender))
            {
                var f = Wire.Unpack(data);
                try
                {
                    if (f.Length >= 1 && f[0] == "simtick") { sim.Heartbeat(playfieldName); sim.Tick(); return; }
                    if (sim.TryHandle(f, playfieldName)) return;
                }
                catch (Exception e) { log("sim packet threw: " + e); return; }
            }
            if (dedi != null) { if (registry == null || !registry.TryHandle(sender, playfieldName, data)) dedi.OnPacket(sender, playfieldName, data); }
            else if (fly != null && TryFly(sender, data)) { }
            else pf?.OnPacket(sender, playfieldName, data);
        }
    }
}
