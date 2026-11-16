using System;
using System.IO;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core.Registry;

namespace LivingSystems
{
    /// <summary>
    /// Dedi side of the ship registry: the single owner of <c>&lt;save&gt;/Mods/LivingSystems/registry.txt</c>.
    /// PfServers come and go (a playfield unloads when its last player leaves), so they report changes here and
    /// ask for their playfield's orders on load / logic reload.
    ///
    /// Wire (fields joined by '|'; a record line contains no '|'):
    ///   PfServer → Dedi:  regset|&lt;record&gt;    full update (sender's known fields win, unknown ones are kept)
    ///                     regtouch|&lt;record&gt;  register if new, else refresh name/playfield only (never the order)
    ///                     regpatch|&lt;fields&gt;  partial: only the given keys; empty value deletes the key
    ///                     regdel|&lt;entityId&gt;   regget|&lt;playfield&gt;
    ///   Dedi → PfServer:  0|fly|regrecord|&lt;record&gt; (one per ship)   0|fly|regdone
    /// </summary>
    sealed class DediRegistry
    {
        readonly IModApi api;
        readonly Action<string> log;
        readonly string path;
        readonly ShipRegistry registry;

        /// <summary>(entity id, name, playfield) of a ship that just left its playfield at the end of a trip.</summary>
        public Action<int, string, string> OnDeparted;

        public DediRegistry(IModApi api, Action<string> log)
        {
            this.api = api; this.log = log;
            var save = api.Application.GetPathFor(AppFolder.SaveGame);
            path = Path.Combine(save, "Mods", "LivingSystems", "registry.txt");
            registry = ShipRegistry.Load(path, out var skipped);
            log($"registry: {registry.Count} ship(s), {registry.All.Count(r => r.Order != null)} with orders, from '{path}'{(skipped > 0 ? $", {skipped} bad line(s) skipped" : "")}");
        }

        /// <summary>
        /// We just spawned this ship: it is ours by construction (identity = entity id, never the name — a player can
        /// rename anything). Record it and tell its playfield right away.
        /// </summary>
        public void RegisterSpawn(int entityId, string name, string playfield)
        {
            var r = new ShipRecord
            {
                EntityId = entityId, Name = name ?? "", Playfield = playfield ?? "",
                Since = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            };
            if (registry.Set(r)) { Save(); log($"registry: spawned {entityId} '{name}' ({playfield}) registered"); }
            api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, playfield, Wire.Pack(0, "fly", "regrecord", ShipRecord.ToLine(registry.Get(entityId))));
        }

        /// <summary>A structure the Dedi destroyed itself: drop its record (no PfServer sends regdel for those).</summary>
        public void Forget(int entityId) { if (registry.Remove(entityId)) { Save(); log($"registry: {entityId} removed (destroyed by the Dedi)"); } }

        /// <summary>True if the packet was a registry message (handled here).</summary>
        public bool TryHandle(string sender, string playfieldName, byte[] data)
        {
            if (!Wire.IsOurs(sender)) return false;
            var f = Wire.Unpack(data);
            if (f.Length < 2) return false;
            try
            {
                switch (f[0])
                {
                    case "regset":
                    case "regtouch":
                    {
                        var r = ShipRecord.FromLine(string.Join("|", f.Skip(1)));
                        if (r == null) { log($"registry: bad {f[0]} " + string.Join("|", f.Skip(1))); return true; }
                        if (f[0] == "regtouch")
                        {
                            var cur = registry.Get(r.EntityId);
                            if (cur != null) { cur.Name = r.Name; cur.Playfield = r.Playfield; r = cur; }
                        }
                        if (registry.Set(r)) { Save(); log($"registry: {r.EntityId} '{r.Name}' ({r.Playfield}) order={r.Order?.Kind ?? "none"}"); }
                        return true;
                    }
                    case "regpatch":
                    {
                        var line = string.Join("|", f.Skip(1));
                        if (registry.Patch(line))
                        {
                            Save(); log($"registry: patched {line}");
                            // end of a trip (EscortRun → Depart): the ship carries on in the offline sim
                            var pf = ShipRecord.ParseFields(line);
                            if (pf != null && pf.TryGetValue("trip.dest", out var dest) && dest.Length > 0 && int.TryParse(pf["id"], out var pid2))
                            { var rec = registry.Get(pid2); if (rec != null) OnDeparted?.Invoke(pid2, rec.Name, rec.Playfield); }
                        }
                        return true;
                    }
                    case "regdel":
                        if (int.TryParse(f[1], out var id) && registry.Remove(id)) { Save(); log($"registry: {id} removed"); }
                        return true;
                    case "regget":
                    {
                        var pf = f[1];
                        var list = registry.InPlayfield(pf).ToList();
                        foreach (var r in list)
                            api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(0, "fly", "regrecord", ShipRecord.ToLine(r)));
                        api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(0, "fly", "regdone"));
                        log($"registry: sent {list.Count} record(s) to '{pf}'");
                        return true;
                    }
                }
            }
            catch (Exception e) { log("registry: " + f[0] + " threw: " + e); return true; }
            return false;
        }

        void Save()
        {
            try { registry.Save(path); }
            catch (Exception e) { log("registry: SAVE FAILED: " + e.Message); }
        }
    }
}
