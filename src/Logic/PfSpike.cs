using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// PfServer half of the F00 spike: finds our tagged structures, drives them each frame in one
    /// of four movement modes, reports their state, and removes them on playfield unload.
    /// </summary>
    sealed class PfSpike
    {
        readonly IModApi api;
        readonly Action<string> log;
        IPlayfield playfield;
        string mode = "none";
        float speed;
        string brake = "none";          // a | b | c | d, see Handle("stop")
        double brakeUntil;
        readonly Dictionary<int, Vector3> lastPos = new Dictionary<int, Vector3>();
        readonly Dictionary<int, Vector3> velocity = new Dictionary<int, Vector3>();
        double lastSample;

        // goto / follow
        string nav = "none";            // goto | follow
        // turn/slide: Move gets a SHIP-LOCAL vector (hypothesis from the goto test). turnw/slidew: world vector, as before.
        // turn*: rotate the nose toward the target and thrust forward. slide*: never rotate, thrust straight at it.
        string steer = "turn";
        float navSpeed = 30f, stopDist = 100f;
        const float Decel = 4f;
        Vector3 navTarget;
        int navPlayer;
        readonly HashSet<int> arrived = new HashSet<int>();
        readonly Dictionary<int, float> turnRate = new Dictionary<int, float>();   // current angular speed, deg/s
        const float MaxTurnDeg = 20f, TurnAccelDeg = 12f;

        // spawn → SetFaction once ready (legacy spawn ignores faction)
        readonly Dictionary<int, KeyValuePair<byte, int>> wantFaction = new Dictionary<int, KeyValuePair<byte, int>>();

        // perf: frame times over a rolling window
        readonly Queue<float> frames = new Queue<float>();
        float frameSum;
        Dictionary<int, string> itemNames;

        // NPC_HOLOGRAM swaps: what the player's marker block was, so we can put it back.
        sealed class Swapped { public int EntityId; public VectorInt3 Pos; public int Type, Shape, Rot; public bool Active; }
        readonly List<Swapped> holos = new List<Swapped>();
        const string HoloName = "NPC_HOLOGRAM";
        const int TraderBlock = 1314;
        static readonly HashSet<int> CoreBlocks = new HashSet<int> { 558, 560, 1360, 1361, 1401, 1402 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastTick, lastReport;

        /// <summary>Order status of one of our ships as seen by a player ("following you at 500 m"), from the flight driver.</summary>
    public Func<int, int, string> ShipStatus;
    /// <summary>What a ship says when hailed ("what's up?": where it's headed and why); "" = nothing special.</summary>
    public Func<int, int, string> ShipSays;
    /// <summary>Registered as ours (by entity id, from the flight driver's registry view). Never decide ownership by name.</summary>
    public Func<int, bool> IsOurShip;
    /// <summary>The player's own hired tug (0 = none): its own line heads the hail list, so the scan leaves it out.</summary>
    public Func<int, int> HiredTugOf;
    /// <summary>Adds a line to the COMMS feed (flight driver's event list).</summary>
    public Action<string> FeedEvent;

    public PfSpike(IModApi api, Action<string> log)
        {
            this.api = api; this.log = log;
            log("PF spike ready");
        }

        int lastPid;

        // ---- restored from our own last good build of this file after a bad scripted edit ----
    private readonly HashSet<int> spawnedV2 = new HashSet<int>();
    private readonly Dictionary<int, double> reportCoreAt = new Dictionary<int, double>();
    private EntityDelegate entityLoaded;
    private IPlayfield entityLoadedOn;

    private IEnumerable<IEntity> Ours()
    {
        IPlayfield obj = playfield;
        object obj2;
        if (obj == null)
        {
            obj2 = null;
        }
        else
        {
            Dictionary<int, IEntity> entities = obj.Entities;
            obj2 = ((entities != null) ? Enumerable.ToList<IEntity>(Enumerable.Where<IEntity>((IEnumerable<IEntity>)entities.Values, (Func<IEntity, bool>)((IEntity e) => e != null && (StructureTag.IsTagged(e.Name) || spawnedV2.Contains(e.Id))))) : null);
        }
        IEnumerable<IEntity> enumerable = (IEnumerable<IEntity>)obj2;
        return enumerable ?? Enumerable.Empty<IEntity>();
    }

    public void Stop()
    {
        //IL_0042: Unknown result type (might be due to invalid IL or missing references)
        RestoreHolos("logic stop");
        if (entityLoadedOn != null && entityLoaded != null)
        {
            entityLoadedOn.OnEntityLoaded -= entityLoaded;
            if (entityUnloadedOn != null && entityUnloaded != null) entityUnloadedOn.OnEntityUnloaded -= entityUnloaded;
        }
        foreach (IEntity item in Ours())
        {
            try
            {
                item.Move(Vector3.zero);
            }
            catch
            {
            }
        }
    }

    public void OnLoaded(IPlayfield pf)
    {
        //IL_0030: Unknown result type (might be due to invalid IL or missing references)
        //IL_003a: Expected O, but got Unknown
        //IL_00e5: Unknown result type (might be due to invalid IL or missing references)
        playfield = pf;
        lastSweep = 0;   // retired trade kiosks left in a save: swept (SweepOrphans)
        if (entityLoadedOn != null && entityLoaded != null)
        {
            entityLoadedOn.OnEntityLoaded -= entityLoaded;
        }
        entityLoaded = (EntityDelegate)delegate(IEntity e)
        {
            //IL_0035: Unknown result type (might be due to invalid IL or missing references)
            //IL_003c: Invalid comparison between Unknown and I4
            try
            {
                if (StructureTag.IsTagged((e != null) ? e.Name : null))
                {
                    log("entity loaded: " + Describe(e));
                }

            }
            catch (Exception ex)
            {
                log("OnEntityLoaded threw: " + ex.Message);
            }
        };
        pf.OnEntityLoaded += entityLoaded;
        // Death hunt: does the game UNLOAD a trader NPC when it dies (keeping a separate corpse)?
        if (entityUnloadedOn != null && entityUnloaded != null) entityUnloadedOn.OnEntityUnloaded -= entityUnloaded;
        entityUnloaded = (IEntity u) =>
        {
            try
            {
                if (u == null || u.Type != EntityType.Trader) return;
                log($"ENTITY UNLOADED: {u.Id} '{u.Name}' {u.Type} belongsTo={Safe(() => u.BelongsTo)} at {u.Position}; still in Entities: {playfield.Entities.ContainsKey(u.Id)}");
            }
            catch (Exception ex) { log("OnEntityUnloaded threw: " + ex.Message); }
        };
        pf.OnEntityUnloaded += entityUnloaded; entityUnloadedOn = pf;
        entityLoadedOn = pf;
        SendItemMapping();
        List<IEntity> list = Enumerable.ToList<IEntity>(Ours());
        log($"playfield loaded '{pf.Name}' ({pf.PlayfieldType}), {pf.Entities?.Count} entities, {list.Count} tagged from save");
        foreach (IEntity item in list)
        {
            log($"  from save: {Describe(item)} at {item.Position}");
        }
    }

    public void OnUnloading(IPlayfield pf)
    {
        //IL_0054: Unknown result type (might be due to invalid IL or missing references)
        RestoreHolos("playfield unloading");
        foreach (IEntity item in Ours())
        {
            log($"unloading '{pf.Name}', saving with it: {item.Id} '{item.Name}' at {item.Position}");
        }
        mode = "none";
    }

    public void OnUpdate()
    {
        //IL_0208: Unknown result type (might be due to invalid IL or missing references)
        //IL_020d: Unknown result type (might be due to invalid IL or missing references)
        //IL_0254: Unknown result type (might be due to invalid IL or missing references)
        //IL_0232: Unknown result type (might be due to invalid IL or missing references)
        //IL_0234: Unknown result type (might be due to invalid IL or missing references)
        //IL_0236: Unknown result type (might be due to invalid IL or missing references)
        //IL_023d: Unknown result type (might be due to invalid IL or missing references)
        //IL_04a9: Unknown result type (might be due to invalid IL or missing references)
        //IL_04b4: Unknown result type (might be due to invalid IL or missing references)
        //IL_04c4: Unknown result type (might be due to invalid IL or missing references)
        //IL_04cb: Unknown result type (might be due to invalid IL or missing references)
        //IL_04d6: Unknown result type (might be due to invalid IL or missing references)
        //IL_04dc: Unknown result type (might be due to invalid IL or missing references)
        //IL_04e1: Unknown result type (might be due to invalid IL or missing references)
        //IL_0332: Unknown result type (might be due to invalid IL or missing references)
        //IL_033e: Unknown result type (might be due to invalid IL or missing references)
        //IL_04ef: Unknown result type (might be due to invalid IL or missing references)
        //IL_04fa: Unknown result type (might be due to invalid IL or missing references)
        //IL_036e: Unknown result type (might be due to invalid IL or missing references)
        //IL_0374: Unknown result type (might be due to invalid IL or missing references)
        //IL_0385: Unknown result type (might be due to invalid IL or missing references)
        //IL_038b: Unknown result type (might be due to invalid IL or missing references)
        //IL_0392: Unknown result type (might be due to invalid IL or missing references)
        //IL_0397: Unknown result type (might be due to invalid IL or missing references)
        //IL_039c: Unknown result type (might be due to invalid IL or missing references)
        //IL_03a0: Unknown result type (might be due to invalid IL or missing references)
        //IL_0598: Unknown result type (might be due to invalid IL or missing references)
        //IL_0508: Unknown result type (might be due to invalid IL or missing references)
        //IL_0513: Unknown result type (might be due to invalid IL or missing references)
        //IL_03d0: Unknown result type (might be due to invalid IL or missing references)
        //IL_03d7: Unknown result type (might be due to invalid IL or missing references)
        //IL_03e8: Unknown result type (might be due to invalid IL or missing references)
        //IL_03ef: Unknown result type (might be due to invalid IL or missing references)
        //IL_03f4: Unknown result type (might be due to invalid IL or missing references)
        //IL_03f9: Unknown result type (might be due to invalid IL or missing references)
        //IL_05c2: Unknown result type (might be due to invalid IL or missing references)
        //IL_05c4: Unknown result type (might be due to invalid IL or missing references)
        double totalSeconds = clock.Elapsed.TotalSeconds;
        float num = (float)(totalSeconds - lastTick);
        lastTick = totalSeconds;
        if (playfield == null || num <= 0f || num > 5f)
        {
            return;
        }
        frames.Enqueue(num);
        frameSum += num;
        while (frameSum > 10f && frames.Count > 1)
        {
            frameSum -= frames.Dequeue();
        }
        try
        {
            ApplyWantedFactions();
            PollComms();
            SweepOrphans();
            foreach (int item in Enumerable.ToList<int>((IEnumerable<int>)reportCoreAt.Keys))
            {
                if (!(clock.Elapsed.TotalSeconds < reportCoreAt[item]))
                {
                    reportCoreAt.Remove(item);
                    if (playfield.Entities.TryGetValue(item, out var value))
                    {
                        string text = Describe(value);
                        log("spawn2 result " + text);
                        Reply((navPlayer != 0) ? navPlayer : lastPid, text);
                    }
                }
            }
            List<IEntity> list = Enumerable.ToList<IEntity>(Ours());
            if (list.Count == 0)
            {
                return;
            }
            if (totalSeconds - lastSample >= 1.0)
            {
                float num2 = (float)(totalSeconds - lastSample);
                lastSample = totalSeconds;
                foreach (IEntity item2 in list)
                {
                    Vector3 position = item2.Position;
                    if (lastPos.TryGetValue(item2.Id, out var value2))
                    {
                        velocity[item2.Id] = (position - value2) / num2;
                    }
                    lastPos[item2.Id] = position;
                }
                IEntity val = list[0];
                if (velocity.TryGetValue(val.Id, out var value3) && (mode != "none" || brake != "none" || nav != "none" || value3.magnitude > 0.5f))
                {
                    Action<string> action = log;
                    string text2 = $"[{mode}/{brake}/{nav}-{steer}] {val.Id} speed={value3.magnitude:0.0} m/s vel={value3} at {val.Position}";
                    object obj;
                    if (!(nav != "none"))
                    {
                        obj = "";
                    }
                    else
                    {
                        object arg = Vector3.Distance(val.Position, navTarget);
                        Vector3 forward = val.Forward;
                        Vector3 val2 = navTarget - val.Position;
                        obj = $" to-target={arg:0} m fwd·dir={Vector3.Dot(forward, val2.normalized):0.00}";
                    }
                    action(text2 + (string)obj + ((value3.magnitude > 0.5f) ? $" vel·nose={Vector3.Dot(value3.normalized, val.Forward):0.00} vel·shipUp={Vector3.Dot(value3.normalized, val.Rotation * Vector3.up):0.00}" : "") + $" frame={num * 1000f:0}ms");
                }
            }
            foreach (IEntity item3 in list)
            {
                switch (mode)
                {
                case "fwdtick":
                    item3.MoveForward(speed);
                    break;
                case "move":
                    item3.Move(item3.Forward * speed);
                    break;
                case "pos":
                    item3.Position += item3.Forward * speed * num;
                    break;
                case "lfwd":
                    item3.Move(Vector3.forward * speed);
                    break;
                case "lup":
                    item3.Move(Vector3.up * speed);
                    break;
                }
                if (nav != "none")
                {
                    Steer(item3, num);
                }
                if (!(brake != "none"))
                {
                    continue;
                }
                velocity.TryGetValue(item3.Id, out var value4);
                switch (brake)
                {
                case "a":
                    item3.Move(Vector3.zero);
                    break;
                case "b":
                    item3.MoveForward(0f);
                    break;
                case "d":
                    if (value4.magnitude > 0.5f)
                    {
                        item3.Move(-value4);
                    }
                    break;
                }
            }
            if (brake != "none" && totalSeconds > brakeUntil)
            {
                log("brake '" + brake + "' window over");
                brake = "none";
            }
        }
        catch (Exception arg2)
        {
            log($"update threw, stopping: {arg2}");
            mode = "none";
            brake = "none";
        }
    }

    private void RestoreHolos(string why)
    {
        //IL_005e: Unknown result type (might be due to invalid IL or missing references)
        foreach (Swapped item in Enumerable.ToList<Swapped>((IEnumerable<Swapped>)holos))
        {
            try
            {
                if (playfield != null && playfield.Entities.TryGetValue(item.EntityId, out var value) && ((value != null) ? value.Structure : null) != null)
                {
                    value.Structure.GetBlock(item.Pos).Set((int?)item.Type, (int?)item.Shape, (int?)item.Rot, (bool?)item.Active);
                    log($"holo restored ({why}): {item.EntityId} at {item.Pos.x},{item.Pos.y},{item.Pos.z} -> type {item.Type}");
                }
            }
            catch (Exception ex)
            {
                log("holo restore threw: " + ex.Message);
            }
            holos.Remove(item);
        }
    }


    /// <summary>Half the hull's bounding-box diagonal in metres (large blocks 2 m, SV blocks 0.5 m); 20 m if unknown.</summary>
    internal static float HullRadius(IEntity x)
    {
        try
        {
            var mn = x.Structure.MinPos; var mx = x.Structure.MaxPos;
            var dx = mx.x - mn.x + 1; var dy = mx.y - mn.y + 1; var dz = mx.z - mn.z + 1;
            var block = x.Type == EntityType.SV || x.Type == EntityType.HV ? 0.5f : 2f;
            return 0.5f * block * Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        catch { return 20f; }
    }

    static bool IsNpcOwned(IEntity x)
    {
        try { var g = (int)x.Faction.Group; return g >= 2 && x.Faction.Group != FactionGroup.Admin; } catch { return false; }
    }

    const string CommsName = "COMMS";

    /// <summary>The structure the player is aboard (pilot, passenger, or with a block under their feet) if it has a COMMS device.</summary>
    IEntity ShipWithComms(int pid)
    {
        if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) return null;
        foreach (var e in playfield.Entities.Values)
        {
            var st = e?.Structure; if (st == null || e.Id == pid) continue;
            if (Vector3.Distance(e.Position, pl.Position) > 300f) continue;
            bool aboard = false;
            try { aboard = st.Pilot?.Id == pid || (st.GetPassengers()?.Any(p => p?.Id == pid) ?? false); } catch { }
            if (!aboard)
            {
                try
                {
                    var sp = st.GlobalToStructPos(pl.Position);
                    for (int dy = 1; dy <= 3 && !aboard; dy++) { int t, sh, r; bool a; st.GetBlock(sp.x, sp.y - dy, sp.z).Get(out t, out sh, out r, out a); aboard = t != 0; }
                }
                catch { }
            }
            if (!aboard) continue;
            List<VectorInt3> dev; try { dev = st.GetDevicePositions(CommsName); } catch { dev = null; }
            if (dev != null && dev.Count > 0) return e;
        }
        return null;
    }

    /// <summary>COMMS lever = push-button: ON opens the hail dialog for the pilot (or nearest player within 10 m), then resets.</summary>
    double lastCommsPoll;
    void PollComms()
    {
        var now = clock.Elapsed.TotalSeconds;
        if (now - lastCommsPoll < 0.3) return;
        lastCommsPoll = now;
        foreach (var e in playfield.Entities.Values.ToList())
        {
            var st = e?.Structure; if (st == null || StructureTag.IsTagged(e.Name)) continue;
            List<VectorInt3> dev; try { dev = st.GetDevicePositions(CommsName); } catch { continue; }
            if (dev == null || dev.Count == 0) continue;
            bool? on = null; try { on = st.GetBlock(dev[0]).GetSwitchState(); } catch { }
            if (on != true) continue;
            try { st.GetBlock(dev[0]).SetSwitchState(false); } catch { }   // momentary
            int who = 0;
            try { who = st.Pilot?.Id ?? 0; } catch { }
            if (who == 0)
            {
                var at = st.StructToGlobalPos(dev[0]);
                who = playfield.Players.Values.Where(p => p != null && Vector3.Distance(p.Position, at) < 10f)
                        .OrderBy(p => Vector3.Distance(p.Position, at)).Select(p => p.Id).FirstOrDefault();
            }
            log($"COMMS pressed on '{e.Name}' -> player {who}");
            if (who != 0) Handle(who, "hail", new string[0]);
        }
    }

    EntityDelegate entityUnloaded;
    IPlayfield entityUnloadedOn;
    double lastSweep;

    /// <summary>The trade kiosk is retired: any «LS» kiosk left in a save (and the trader NPC aboard it) is removed.</summary>
    void SweepOrphans()
    {
        var now = clock.Elapsed.TotalSeconds;
        if (now - lastSweep < 2) return;
        lastSweep = now;
        foreach (var e in playfield.Entities.Values.ToList())
        {
            if (e == null || !KioskNames.IsKiosk(e.Name)) continue;
            log($"sweep: leftover trade kiosk {e.Id}, removing");
            foreach (var n in playfield.Entities.Values.Where(x => x != null && x.Type == EntityType.Trader && Safe(() => x.BelongsTo) == e.Id.ToString()).ToList()) playfield.RemoveEntity(n.Id);
            playfield.RemoveEntity(e.Id);
            api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("kioskgone", 0, e.Id), playfield.Name);
        }
    }

        void ApplyWantedFactions()
        {
            if (wantFaction.Count == 0) return;
            foreach (var id in wantFaction.Keys.ToList())
            {
                if (!playfield.Entities.TryGetValue(id, out var e) || e?.Structure == null) continue;
                bool ready; try { ready = e.Structure.IsReady; } catch { ready = false; }
                if (!ready) continue;
                var f = wantFaction[id]; wantFaction.Remove(id);
                e.Structure.SetFaction((FactionGroup)f.Key, f.Value);
                log($"auto SetFaction({(FactionGroup)f.Key}, {f.Value}) on {id}");
            }
        }

        /// <summary>The Dedi's GetBlockAndItemMapping() is empty, so ship ours over: "items|name=id|name=id...".</summary>
        void SendItemMapping()
        {
            try
            {
                var m = api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>();
                var fields = new List<object> { "items" };
                foreach (var kv in m) if (kv.Key.IndexOf('|') < 0) fields.Add(kv.Key + "=" + kv.Value);
                var ok = api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack(fields.ToArray()), playfield?.Name);
                log($"sent item mapping to Dedi: {m.Count} names, {fields.Count - 1} sent, ok={ok}");
            }
            catch (Exception ex) { log("SendItemMapping threw: " + ex.Message); }
        }

        string ItemName(int id)
        {
            if (itemNames == null)
            {
                itemNames = new Dictionary<int, string>();
                foreach (var kv in api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>())
                    if (!itemNames.ContainsKey(kv.Value)) itemNames[kv.Value] = kv.Key;
            }
            return itemNames.TryGetValue(id, out var n) ? n : "#" + id;
        }

        /// <summary>All containers on any structure (ours or vanilla): count, total items, per-item totals.</summary>
        void CargoOf(IEntity e, out int containers, out int items, out Dictionary<int, int> byItem)
        {
            containers = 0; items = 0; byItem = new Dictionary<int, int>();
            var s = e?.Structure; if (s == null) return;
            foreach (var kind in new[] { DeviceTypeName.Container, DeviceTypeName.AmmoCntr, DeviceTypeName.Fridge, DeviceTypeName.HarvestCntr })
            {
                IDevicePosList list; try { list = s.GetDevices(kind); } catch { continue; }
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    IContainer c; try { c = s.GetDevice<IContainer>(list.GetAt(i)); } catch { continue; }
                    if (c == null) continue;
                    containers++;
                    List<ItemStack> content; try { content = c.GetContent(); } catch { continue; }
                    foreach (var st in content ?? new List<ItemStack>())
                    {
                        items += st.count;
                        byItem[st.id] = (byItem.TryGetValue(st.id, out var n) ? n : 0) + st.count;
                    }
                }
            }
        }

        /// <summary>
        /// Asteroid probe. list [r] — entities within r m (default 3000) that aren't ships: type, structure or not, bounds ·
        /// add &lt;size&gt; — IPlayfield.AddVoxelArea 40 m ahead of you (undocumented: hole? fill? nothing?) · move &lt;areaId&gt; —
        /// MoveVoxelArea to 40 m ahead · remove &lt;areaId&gt; · kill &lt;id&gt; — RemoveEntity on it (the "mined out" fallback).
        /// </summary>
        void RockProbe(int pid, string[] args)
        {
            var sub = args.Length > 0 ? args[0] : "list";
            var me = PlayerPos(pid);
            Vector3 Ahead()
            {
                var p = playfield.Players.TryGetValue(pid, out var pl) ? pl : null;
                var rot = p?.DrivingEntity != null ? p.DrivingEntity.Rotation : (p != null ? p.Rotation : Quaternion.identity);
                return (me ?? Vector3.zero) + rot * Vector3.forward * 40f;
            }
            int Arg(int i, int def) => args.Length > i && int.TryParse(args[i], out var v) ? v : def;
            switch (sub)
            {
                case "list":
                {
                    var r = Arg(1, 3000);
                    var hits = playfield.Entities.Values.Where(e => e != null && e.Type != EntityType.CV && e.Type != EntityType.SV && e.Type != EntityType.HV
                                                                    && e.Type != EntityType.Player && me.HasValue && (e.Position - me.Value).magnitude <= r)
                                                       .OrderBy(e => (e.Position - me.Value).magnitude).Take(15).ToList();
                    foreach (var e in hits)
                    {
                        var st = e.Structure;
                        var line = $"{e.Id} '{e.Name}' {e.Type} {(e.Position - me.Value).magnitude:0} m, structure={(st != null ? "yes" : "no")}" +
                                   (st != null ? $" blocks={Safe(() => st.BlockCount)} bounds={Safe(() => st.MinPos)}..{Safe(() => st.MaxPos)}" : "");
                        log("rock " + line); Reply(pid, line);
                    }
                    if (hits.Count == 0) Reply(pid, $"nothing but ships within {r} m");
                    break;
                }
                case "add":
                {
                    var at = Ahead(); var size = Arg(1, 5);
                    var ground = playfield.GetTerrainHeightAt(at.x, at.z);   // planets: snap to the ground 40 m ahead (space: no terrain)
                    if (!float.IsNaN(ground) && !float.IsInfinity(ground) && Math.Abs(ground) > 0.01f && args.Length < 3) at.y = ground;
                    var id = playfield.AddVoxelArea(at, size);
                    log($"rock add: AddVoxelArea({at}, {size}) → {id}"); Reply(pid, $"AddVoxelArea size {size} at 40 m ahead → id {id}. Look for a change; /ls rock move {id} | remove {id}");
                    break;
                }
                case "move":
                {
                    var ok = playfield.MoveVoxelArea(Arg(1, -1), Ahead());
                    log($"rock move {Arg(1, -1)} → {ok}"); Reply(pid, $"MoveVoxelArea {Arg(1, -1)} to 40 m ahead → {ok}");
                    break;
                }
                case "remove":
                {
                    var ok = playfield.RemoveVoxelArea(Arg(1, -1));
                    log($"rock remove {Arg(1, -1)} → {ok}"); Reply(pid, $"RemoveVoxelArea {Arg(1, -1)} → {ok}");
                    break;
                }
                case "kill":
                {
                    var id = Arg(1, -1);
                    if (!playfield.Entities.TryGetValue(id, out var e) || e == null) { Reply(pid, $"no entity {id} here"); break; }
                    if (e.Type == EntityType.CV || e.Type == EntityType.SV || e.Type == EntityType.HV || e.Type == EntityType.Player) { Reply(pid, "that's a ship — not with this probe"); break; }
                    var what = $"{id} '{e.Name}' ({e.Type})";   // read before: the entity is dead after RemoveEntity
                    playfield.RemoveEntity(id);
                    log($"rock kill: RemoveEntity {what}"); Reply(pid, $"RemoveEntity {what} sent — gone? Back after a reload?");
                    break;
                }
                case "height":
                {   // landing probe: the terrain height under you and under the spot 40 m ahead (planets)
                    var a = Ahead();
                    var h0 = me.HasValue ? playfield.GetTerrainHeightAt(me.Value.x, me.Value.z) : float.NaN;
                    var h1 = playfield.GetTerrainHeightAt(a.x, a.z);
                    var line = $"terrain under you {h0:0.0} (you at y {me?.y:0.0}, {me?.y - h0:0.0} m up); 40 m ahead {h1:0.0} — playfield '{playfield.Name}' type {playfield.PlayfieldType}";
                    log("rock " + line); Reply(pid, line);
                    break;
                }
                default: Reply(pid, "/ls rock list [r] | add <size> | move <areaId> | remove <areaId> | kill <id> | height"); break;
            }
        }

        Vector3? PlayerPos(int pid)
        {
            if (playfield?.Players == null || !playfield.Players.TryGetValue(pid, out var p) || p == null) return null;
            var ride = p.DrivingEntity;      // if they are flying something, chase the ship, not the pilot's seat
            return ride != null ? ride.Position : p.Position;
        }

        void Steer(IEntity e, float dt)
        {
            if (nav == "follow") { var pp = PlayerPos(navPlayer); if (pp.HasValue) navTarget = pp.Value; }
            var to = navTarget - e.Position;
            var dist = to.magnitude;
            if (dist < 0.01f) { e.Move(Vector3.zero); return; }
            var dir = to / dist;
            var want = Steering.ApproachSpeed(dist, navSpeed, stopDist, Decel);
            var local = steer == "turn" || steer == "slide";
            Vector3 thrust;   // desired velocity, world space

            if (steer.StartsWith("slide"))
            {
                thrust = dir * want;
            }
            else
            {
                // Angular speed model: accelerate the turn, cap it, ease off as the nose comes round.
                var rot = e.Rotation;
                var look = Quaternion.LookRotation(dir, Vector3.up);
                var angle = Quaternion.Angle(rot, look);
                turnRate.TryGetValue(e.Id, out var w);
                var wTarget = Mathf.Min(MaxTurnDeg, Mathf.Sqrt(2f * TurnAccelDeg * angle));
                w = Mathf.MoveTowards(w, wTarget, TurnAccelDeg * dt);
                turnRate[e.Id] = w;
                e.Rotation = Quaternion.RotateTowards(rot, look, w * dt);
                // Thrust along the nose, scaled by alignment: turns in an arc instead of spinning in place.
                var nose = e.Rotation * Vector3.forward;
                thrust = nose * want * Steering.AlignmentFactor(Vector3.Dot(nose, dir));
            }

            e.Move(local ? Quaternion.Inverse(e.Rotation) * thrust : thrust);

            if (want <= 0.01f)
            {
                if (arrived.Add(e.Id))
                {
                    velocity.TryGetValue(e.Id, out var v);
                    log($"[{nav}/{steer}] {e.Id} arrived: {dist:0} m from target, speed {v.magnitude:0.0}");
                    if (nav == "goto") Reply(navPlayer, $"{e.Id} arrived, {dist:0} m from target");
                }
            }
            else if (want > 1f) arrived.Remove(e.Id);
        }

        // ---- commands from Dedi -------------------------------------------------------------------

        public void OnPacket(string sender, string playfieldName, byte[] data)
        {
            if (!Wire.IsOurs(sender)) return;
            var f = Wire.Unpack(data);
            if (f.Length < 2 || !int.TryParse(f[0], out var pid)) { log("bad packet " + string.Join("|", f)); return; }
            if (playfield == null) { Reply(pid, "PF has no playfield yet"); return; }
            lastPid = pid;
            try { Handle(pid, f[1], f.Skip(2).ToArray()); }
            catch (Exception ex) { log($"cmd {f[1]} threw: {ex}"); Reply(pid, $"{f[1]} failed: {ex.Message}"); }
        }

        /// <summary>Hail from the cockpit switch (LS_HAIL): same as /ls hail.</summary>
        public void Hail(int pid) => Handle(pid, "hail", new string[0]);

        void Handle(int pid, string verb, string[] args)
        {
            var ours = Ours().ToList();
            switch (verb)
            {
                case "move":
                {
                    var m = args.Length > 0 ? args[0].ToLowerInvariant() : "pos";
                    if (Array.IndexOf(SpikeCommand.MoveModes, m) < 0) { Reply(pid, "modes: " + string.Join(", ", SpikeCommand.MoveModes)); return; }
                    speed = args.Length > 1 && float.TryParse(args[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s) ? Mathf.Clamp(s, 0f, 300f) : 20f;
                    mode = m;
                    if (m == "fwd") foreach (var e in ours) e.MoveForward(speed);   // once, to see if it persists
                    Reply(pid, $"mode={m} speed={speed} on {ours.Count} ship(s)");
                    break;
                }
                case "comms":
                {
                    // Comms are ship equipment: allowed if the player is aboard (pilot, passenger, or standing inside)
                    // a structure that has a device named COMMS.
                    var ship = ShipWithComms(pid);
                    if (ship == null) { Reply(pid, "No comms equipment here. You need to be aboard a ship with a COMMS device."); return; }
                    log($"comms: {pid} via '{ship.Name}'");
                    Handle(pid, "hail", new string[0]);
                    return;
                }
                case "hail":
                {
                    // NPC ships/stations within 5 km: id, name, faction group/id, distance, has-own-trader.
                    if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) { Reply(pid, "can't find you"); return; }
                    var me = pl.DrivingEntity != null ? pl.DrivingEntity.Position : pl.Position;
                    var withTrader = new HashSet<int>(playfield.Entities.Values
                        .Where(x => x != null && x.Type == EntityType.Trader)
                        .Select(x => int.TryParse(Safe(() => x.BelongsTo), out var b) ? b : 0));
                    var list = playfield.Entities.Values
                        .Where(x => x?.Structure != null && (x.Type == EntityType.CV || x.Type == EntityType.SV || x.Type == EntityType.BA)
                                    && !KioskNames.IsKiosk(x.Name) && (IsNpcOwned(x) || (IsOurShip?.Invoke(x.Id) ?? false)))   // ours (by id): listed in any faction
                        .Select(x => new { x, d = Vector3.Distance(me, x.Position) })
                        .Where(t => t.d <= 5000f && t.x.Id != (HiredTugOf?.Invoke(pid) ?? 0)).OrderBy(t => t.d).ToList();
                    // what the scan leaves out within 5 km, and why (a cut POI once vanished from the list: this says which rule)
                    var shown = new HashSet<int>(list.Select(t => t.x.Id));
                    var skipped = playfield.Entities.Values
                        .Where(x => x != null && x.Id != pid && !shown.Contains(x.Id) && x.Type != EntityType.Player && x.Type != EntityType.Proxy && Vector3.Distance(me, x.Position) <= 5000f)
                        .Select(x => $"{x.Id} '{Safe(() => x.Name)}' type={x.Type} group={Safe(() => ((int)x.Faction.Group).ToString())} structure={(x.Structure != null ? "yes" : "null")}{(x.Id == (HiredTugOf?.Invoke(pid) ?? 0) ? " (your tug)" : "")}")
                        .Take(12).ToList();
                    if (skipped.Count > 0) log($"hail: left out for {pid}: {string.Join("; ", skipped)}");
                    // target by nose: the closest of OUR ships within ~15° of where you're pointing goes first, flagged —
                    // the Dedi opens it straight away instead of the contacts pager
                    var facing = (pl.DrivingEntity != null ? pl.DrivingEntity.Rotation : pl.Rotation) * Vector3.forward;
                    // Position is the structure's pivot, not its middle: up close a big hull's pivot can sit well off where you
                    // aim, so the cone widens by the hull's angular size; of several, the one nearest the crosshair wins
                    var aimed = list.Where(t => (IsOurShip?.Invoke(t.x.Id) ?? false) && t.d > 1f)
                        .Select(t => new { t, off = Vector3.Angle(facing, t.x.Position - me) - Mathf.Atan2(HullRadius(t.x), t.d) * Mathf.Rad2Deg })
                        .OrderBy(a => a.off).ToList();
                    var nose = aimed.Count > 0 && aimed[0].off <= 15f ? aimed[0].t : null;
                    if (aimed.Count > 0) log($"hail aim {pid}: {StructureTag.Strip(aimed[0].t.x.Name)} {aimed[0].t.d:0} m, {aimed[0].off:0.0}° off (hull allowance incl.) -> {(nose != null ? "opened" : "contacts list")}");
                    if (nose != null) { list.Remove(nose); list.Insert(0, nose); }
                    list = list.Take(8).ToList();
                    var fields = new List<object> { "hailist", pid };
                    foreach (var t in list)
                        fields.Add($"{t.x.Id}~{t.x.Name.Replace("~", "-").Replace("|", "/")}~{(int)t.x.Faction.Group}~{t.x.Faction.Id}~{t.d:0}~{(withTrader.Contains(t.x.Id) ? 1 : 0)}~{t.x.Type}~{Safe(() => t.x.Structure.BlockCount)}~{Safe(() => t.x.Structure.CoreType)}~{(ShipStatus?.Invoke(t.x.Id, pid) ?? "").Replace("~", "-").Replace("|", "/")}~{(ShipSays?.Invoke(t.x.Id, pid) ?? "").Replace("~", "-").Replace("|", "/")}~{(t == nose ? 1 : 0)}");
                    api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack(fields.ToArray()), playfield.Name);
                    log($"hail: {list.Count} contacts for {pid}");
                    break;
                }
                case "purge":
                {
                    // Dev: every «LS» structure in this playfield, removed here and destroyed globally by the Dedi.
                    var mine = playfield.Entities.Values.Where(x => x != null && x.Structure != null && StructureTag.IsTagged(x.Name)).ToList();
                    foreach (var x in mine)
                    {
                        playfield.RemoveEntity(x.Id);
                        api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("kioskgone", pid, x.Id), playfield.Name);
                    }
                    log($"purge: {mine.Count} «LS» structures removed");
                    Reply(pid, $"purged {mine.Count} «LS» structures here (also destroyed globally)");
                    break;
                }
                case "wantfaction":
                    if (args.Length >= 3 && int.TryParse(args[0], out var wid) && byte.TryParse(args[1], out var wg) && int.TryParse(args[2], out var wf))
                    { wantFaction[wid] = new KeyValuePair<byte, int>(wg, wf); log($"will SetFaction({(FactionGroup)wg}, {wf}) on {wid} when ready"); }
                    break;
                case "rock": RockProbe(pid, args); break;
                case "scan":
                {
                    var me = PlayerPos(pid);
                    var radius = args.Length > 0 && float.TryParse(args[0], out var rr) ? rr : 3000f;
                    var hits = playfield.Entities.Values
                        .Where(e => e != null && e.Structure != null && e.Id != pid && (e.Type == EntityType.CV || e.Type == EntityType.SV || e.Type == EntityType.HV || e.Type == EntityType.BA))
                        .Select(e => new { e, d = me.HasValue ? Vector3.Distance(me.Value, e.Position) : 0f })
                        .Where(x => x.d <= radius).OrderBy(x => x.d).ToList();
                    foreach (var x in hits)
                    {
                        CargoOf(x.e, out var nc, out var ni, out var by);
                        var top = string.Join(", ", by.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{ItemName(kv.Key)}x{kv.Value}"));
                        var line = $"{x.e.Id} '{x.e.Name}' {x.e.Type} {Safe(() => x.e.Faction.Group)} {x.d:0} m, blocks={Safe(() => x.e.Structure.BlockCount)}, containers={nc}, items={ni}" + (top.Length > 0 ? $" [{top}]" : "");
                        log("scan " + line);
                        Reply(pid, line);
                    }
                    if (hits.Count == 0) Reply(pid, $"no structures within {radius:0} m");
                    break;
                }
                case "cargo":
                {
                    if (args.Length == 0 || !int.TryParse(args[0], out var cid) || !playfield.Entities.TryGetValue(cid, out var ce)) { Reply(pid, "usage: /ls cargo <entity id from /ls scan>"); return; }
                    CargoOf(ce, out var nc, out var ni, out var by);
                    foreach (var kv in by.OrderByDescending(kv => kv.Value)) log($"cargo {cid}: {ItemName(kv.Key)} ({kv.Key}) x{kv.Value}");
                    Reply(pid, $"{cid} '{ce.Name}': {nc} containers, {ni} items. " +
                        string.Join(", ", by.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{ItemName(kv.Key)}x{kv.Value}")));
                    break;
                }
                case "npc":
                {
                    // Can a vanilla trader NPC be spawned anywhere, e.g. inside one of our ships? (v2 SpawnEntity by class name)
                    if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) { Reply(pid, "can't find you"); return; }
                    var cls = args.Length > 0 ? args[0] : "TraderHuman";
                    var fwd = pl.Forward; fwd.y = 0; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                    var pos = pl.Position + fwd.normalized * 3f;
                    var rot = Quaternion.LookRotation(-fwd.normalized, Vector3.up);   // facing you
                    int id; try { id = playfield.SpawnEntity(cls, pos, rot); } catch (Exception ex) { Reply(pid, $"SpawnEntity('{cls}') threw: {ex.Message}"); return; }
                    log($"npc '{cls}' -> id {id} at {pos}");
                    Reply(pid, $"SpawnEntity('{cls}') -> {id}. Look 3 m ahead; press F on it if it's a trader.");
                    break;
                }
                case "traderblock":
                {
                    // Place a vanilla Trader block (RE2 id 1314, TraderType default) inside the structure the player is in.
                    if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) { Reply(pid, "can't find you"); return; }
                    var blockId = args.Length > 0 && int.TryParse(args[0], out var bid) ? bid : 1314;
                    IEntity host = null; VectorInt3 at = default(VectorInt3);
                    // "Inside" = a structure within 300 m that has a solid block 1-3 blocks under the player's feet.
                    // (MinPos/MaxPos are not in the same frame as GlobalToStructPos; logged below for the record.)
                    foreach (var e in playfield.Entities.Values)
                    {
                        var st = e?.Structure; if (st == null || e.Id == pid) continue;
                        if (Vector3.Distance(e.Position, pl.Position) > 300f) continue;
                        VectorInt3 sp; try { sp = st.GlobalToStructPos(pl.Position); } catch { continue; }
                        int under = 0;
                        for (int dy = 1; dy <= 3 && under == 0; dy++)
                        {
                            int t, sh, r; bool a;
                            try { st.GetBlock(sp.x, sp.y - dy, sp.z).Get(out t, out sh, out r, out a); } catch { continue; }
                            under = t;
                        }
                        VectorInt3 mn = default(VectorInt3), mx = default(VectorInt3);
                        try { mn = st.MinPos; mx = st.MaxPos; } catch { }
                        log($"traderblock probe {e.Id} '{e.Name}': player at struct {sp.x},{sp.y},{sp.z}, bounds {mn.x},{mn.y},{mn.z}..{mx.x},{mx.y},{mx.z}, block under={under}");
                        if (under == 0) continue;
                        host = e;
                        at = st.GlobalToStructPos(pl.Position + pl.Forward * 2f);
                        break;
                    }
                    if (host == null) { Reply(pid, "no structure under your feet within 300 m (see server log)"); return; }
                    var s2 = host.Structure;
                    var blk = s2.GetBlock(at);
                    int t0, sh0, r0; bool a0; blk.Get(out t0, out sh0, out r0, out a0);
                    if (t0 != 0) { Reply(pid, $"block at {at.x},{at.y},{at.z} in {host.Id} is not empty (type {t0}); step back and retry"); return; }
                    blk.Set(blockId, 0, 0, true);
                    int t1, sh1, r1; bool a1; s2.GetBlock(at).Get(out t1, out sh1, out r1, out a1);
                    log($"traderblock: {host.Id} '{host.Name}' at {at.x},{at.y},{at.z}: type {t0} -> {t1}");
                    Reply(pid, $"placed block {blockId} in {host.Id} '{StructureTag.Strip(host.Name)}' at {at.x},{at.y},{at.z}: now type {t1}. Look in front of you and press F.");
                    break;
                }
                case "spawn2":
                {
                    // v2 IPlayfield.SpawnPrefab: does it keep the prefab's own NPC core and faction (legacy spawn gives NoFaction)?
                    if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) { Reply(pid, "can't find you"); return; }
                    if (args.Length == 0) { Reply(pid, "usage: /ls spawn2 <prefab> [distance]"); return; }
                    var d = args.Length > 1 && float.TryParse(args[1], out var dd) ? dd : 80f;
                    var fwd = pl.Forward; fwd.y = 0; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                    var pos = pl.Position + fwd.normalized * d;
                    int id;
                    try { id = playfield.SpawnPrefab(args[0], pos); }
                    catch (Exception ex) { Reply(pid, $"SpawnPrefab threw: {ex.Message}"); return; }
                    spawnedV2.Add(id);
                    reportCoreAt[id] = clock.Elapsed.TotalSeconds + 3;   // report core/faction once it is ready
                    log($"spawn2 '{args[0]}' -> id {id} at {pos}");
                    Reply(pid, $"SpawnPrefab('{args[0]}') -> id {id}, {d:0} m ahead; core/faction report in ~3 s");
                    break;
                }
                case "record":
                {
                    // Target: explicit id, else the nearest structure within 150 m that is not ours and not a player.
                    if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) { Reply(pid, "can't find you"); return; }
                    IEntity target = null;
                    if (args.Length > 0 && int.TryParse(args[0], out var tid)) playfield.Entities.TryGetValue(tid, out target);
                    if (target == null)
                        target = playfield.Entities.Values
                            .Where(e => e?.Structure != null && !StructureTag.IsTagged(e.Name) && !spawnedV2.Contains(e.Id) && e.Type != EntityType.Player)
                            .Select(e => new { e, d = Vector3.Distance(e.Position, pl.Position) })
                            .Where(x => x.d < 150f).OrderBy(x => x.d).Select(x => x.e).FirstOrDefault();
                    if (target == null) { Reply(pid, "no structure within 150 m (or give an id)"); return; }

                    var st = target.Structure; VectorInt3 mn, mx;
                    try { mn = st.MinPos; mx = st.MaxPos; } catch (Exception ex) { Reply(pid, "bounds failed: " + ex.Message); return; }
                    var blocks = new List<string>(); VectorInt3? core = null; int coreType = 0, coreRot = 0;
                    for (int x = mn.x; x <= mx.x; x++)
                    for (int y = mn.y + 128; y <= mx.y + 128; y++)
                    for (int z = mn.z; z <= mx.z; z++)
                    {
                        int t, sh, r; bool a;
                        try { st.GetBlock(x, y, z).Get(out t, out sh, out r, out a); } catch { continue; }
                        if (t == 0) continue;
                        blocks.Add($"{x},{y},{z} type={t}({ItemName(t)}) shape={sh} rot={r} active={a}");
                        if (CoreBlocks.Contains(t) && core == null) { core = new VectorInt3(x, y, z); coreType = t; coreRot = r; }
                    }
                    log($"record {target.Id} '{target.Name}' {target.Type} bounds {mn.x},{mn.y},{mn.z}..{mx.x},{mx.y},{mx.z}: {blocks.Count} blocks");
                    foreach (var b in blocks) log("  " + b);


                    Reply(pid, $"{target.Id} '{target.Name}' {target.Type}: {blocks.Count} blocks, core {(core.HasValue ? ItemName(coreType) : "NOT FOUND")}. Full dump in playfield log.");
                    return;
                }
                case "find":
                {
                    // Scan whole structures for a block type (default: Trader 1314). Struct grid y has a +128 offset vs MinPos/MaxPos.
                    var want = args.Length > 0 && int.TryParse(args[0], out var wt) ? wt : TraderBlock;
                    var which = args.Length > 1 ? args[1] : "ours";
                    var targets = playfield.Entities.Values.Where(e => e?.Structure != null && (
                        which == "all" ? e.Type != EntityType.Player :
                        int.TryParse(which, out var only) ? e.Id == only : StructureTag.IsTagged(e.Name))).ToList();
                    foreach (var e in targets)
                    {
                        var st = e.Structure; VectorInt3 mn, mx;
                        try { mn = st.MinPos; mx = st.MaxPos; } catch { continue; }
                        var hits = new List<string>(); long scanned = 0;
                        for (int x = mn.x; x <= mx.x; x++)
                        for (int y = mn.y + 128; y <= mx.y + 128; y++)
                        for (int z = mn.z; z <= mx.z; z++)
                        {
                            scanned++;
                            int t, sh, r; bool a;
                            try { st.GetBlock(x, y, z).Get(out t, out sh, out r, out a); } catch { continue; }
                            if (t == want && hits.Count < 10) hits.Add($"{x},{y},{z}");
                        }
                        var line = $"{e.Id} '{e.Name}' {e.Type}: {hits.Count} block(s) of type {want} ({scanned} cells) {string.Join(" ", hits)}";
                        log("find " + line);
                        Reply(pid, line);
                    }
                    if (targets.Count == 0) Reply(pid, "no matching structures");
                    break;
                }
                case "tp":
                {
                    // Teleport the player next to a structure (default: their biggest own ship nearby).
                    if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) { Reply(pid, "can't find you"); return; }
                    IEntity target = null;
                    if (args.Length > 0 && int.TryParse(args[0], out var tid)) playfield.Entities.TryGetValue(tid, out target);
                    if (target == null) { Reply(pid, "usage: /ls tp <entity id from /ls scan>"); return; }
                    var dest = target.Position + target.Rotation * Vector3.up * 30f;   // above its deck, clear of the hull
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    // IPlayer.Teleport throws inside the game (verified); ask the Dedi to use legacy Request_Entity_Teleport.
                    api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("tpto", pid, dest.x.ToString(ci), dest.y.ToString(ci), dest.z.ToString(ci)), playfield.Name);
                    log($"tp {pid} -> {target.Id} '{target.Name}' at {dest} (via Dedi legacy teleport)");
                    Reply(pid, $"teleporting you 30 m above '{target.Name}'…");
                    break;
                }
                case "holo":
                {
                    if (args.Length > 0 && args[0].ToLowerInvariant() == "off")
                    {
                        var n = holos.Count; RestoreHolos("holo off");
                        Reply(pid, n == 0 ? "no hologram active" : $"hologram off, {n} block(s) restored");
                        return;
                    }
                    // Any structure within 300 m that has a device named NPC_HOLOGRAM (normally: your own ship).
                    if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) { Reply(pid, "can't find you"); return; }
                    foreach (var e in playfield.Entities.Values)
                    {
                        var st = e?.Structure; if (st == null || e.Id == pid) continue;
                        if (Vector3.Distance(e.Position, pl.Position) > 300f) continue;
                        List<VectorInt3> spots; try { spots = st.GetDevicePositions(HoloName); } catch { continue; }
                        if (spots == null || spots.Count == 0) continue;
                        // Never touch the marker itself: swapping it loses its name, device group, LCD text
                        // and signals (verified). Use the first EMPTY cell next to it; restore = back to air.
                        var marker = spots[0];
                        var candidates = new[]
                        {
                            new VectorInt3(marker.x, marker.y + 1, marker.z), new VectorInt3(marker.x, marker.y, marker.z + 1),
                            new VectorInt3(marker.x, marker.y, marker.z - 1), new VectorInt3(marker.x + 1, marker.y, marker.z),
                            new VectorInt3(marker.x - 1, marker.y, marker.z),
                        };
                        foreach (var at in candidates)
                        {
                            var blk = st.GetBlock(at);
                            int t, sh, r; bool a; blk.Get(out t, out sh, out r, out a);
                            if (t == TraderBlock) { Reply(pid, "hologram already active"); return; }
                            if (t != 0) continue;
                            holos.Add(new Swapped { EntityId = e.Id, Pos = at, Type = 0, Shape = 0, Rot = 0, Active = false });
                            blk.Set(TraderBlock, 0, 0, true);
                            int t2, sh2, r2; bool a2; st.GetBlock(at).Get(out t2, out sh2, out r2, out a2);
                            log($"holo on: {e.Id} '{e.Name}' marker at {marker.x},{marker.y},{marker.z}, trader in empty cell {at.x},{at.y},{at.z} -> type {t2}");
                            Reply(pid, $"incoming transmission… trader next to your {HoloName} block (marker untouched). '/ls holo off' to end the call.");
                            return;
                        }
                        Reply(pid, $"no empty cell next to your {HoloName} block (above, front, back, left, right). Clear one and retry.");
                        return;
                    }
                    Reply(pid, $"no device named {HoloName} on any structure within 300 m");
                    break;
                }
                case "perf":
                {
                    var arr = frames.ToArray();
                    var avg = arr.Length > 0 ? arr.Average() * 1000 : 0; var max = arr.Length > 0 ? arr.Max() * 1000 : 0;
                    var slow = arr.Count(f => f > 0.1f);
                    var blocks = 0; foreach (var e in ours) { try { blocks += e.Structure?.BlockCount ?? 0; } catch { } }
                    var line = $"last {frameSum:0}s: frame avg {avg:0} ms (~{(avg > 0 ? 1000 / avg : 0):0} fps), max {max:0} ms, {slow} frames >100 ms. " +
                               $"{playfield.Entities.Count} entities, {ours.Count} ours with {blocks} blocks";
                    log("perf " + line);
                    Reply(pid, line);
                    break;
                }
                case "goto":
                case "follow":
                {
                    var pp = PlayerPos(pid);
                    if (!pp.HasValue) { Reply(pid, "can't find you in this playfield"); return; }
                    navSpeed = args.Length > 0 && float.TryParse(args[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var ns) ? Mathf.Clamp(ns, 1f, 300f) : 30f;
                    var sm = args.Length > 1 ? args[1].ToLowerInvariant() : "turn";
                    steer = sm == "slide" || sm == "slidew" || sm == "turnw" ? sm : "turn";
                    stopDist = args.Length > 2 && float.TryParse(args[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var sd) ? Mathf.Clamp(sd, 20f, 2000f) : 100f;
                    navTarget = pp.Value; navPlayer = pid; nav = verb; mode = "none"; brake = "none"; arrived.Clear(); turnRate.Clear();
                    var d = ours.Count > 0 ? Vector3.Distance(ours[0].Position, navTarget) : 0f;
                    Reply(pid, $"{verb} {steer} at {navSpeed} m/s, stop {stopDist} m short; first ship is {d:0} m away");
                    break;
                }
                case "faction":
                {
                    SpikeCommand.ParseFaction(args.Length > 0 ? args[0] : "polaris", out var g, out var fid);
                    foreach (var e in ours) e.Structure?.SetFaction((FactionGroup)g, fid);
                    Reply(pid, $"SetFaction({(FactionGroup)g}, {fid}) on {ours.Count} ship(s); check /ls info");
                    break;
                }
                case "stop":
                {
                    // a: Move(zero) each frame  b: MoveForward(0) each frame  c: MoveStop() once  d: Move(-velocity) each frame
                    var how = args.Length > 0 ? args[0].ToLowerInvariant() : "c";
                    mode = "none"; nav = "none";
                    foreach (var e in ours) e.MoveStop();
                    brake = how == "c" ? "none" : how;
                    brakeUntil = clock.Elapsed.TotalSeconds + 10;
                    Reply(pid, $"stop '{how}' on {ours.Count} ship(s) for 10 s; watch it and tell me");
                    break;
                }
                case "clear":
                    // only registered ships (by id) — never anything that merely carries our name tag
                    ours = playfield.Entities.Values.Where(x => x != null && (IsOurShip?.Invoke(x.Id) ?? false)).ToList();
                    // RemoveEntity does NOT stick (the save restores the ship on restart). Have the
                    // Dedi destroy them with the legacy Request_Entity_Destroy while they are still loaded.
                    api.Network.SendToDedicatedServer(LivingSystemsMod.Channel,
                        Wire.Pack(new object[] { "destroy", pid }.Concat(ours.Select(e => (object)e.Id)).ToArray()), playfield.Name);
                    foreach (var e in ours)
                        api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("regdel", e.Id), playfield.Name);
                    mode = "none";
                    Reply(pid, $"destroying {ours.Count} «LS» ship(s)");
                    break;
                case "list":
                {
                    playfield.Entities.TryGetValue(pid, out var me);
                    var lines = ours.Select(e => $"{e.Id} '{StructureTag.Strip(e.Name)}' {e.Type} " +
                        (me != null ? $"{Vector3.Distance(me.Position, e.Position):0} m" : ""));
                    Reply(pid, ours.Count == 0 ? "no tagged ships here" : string.Join("; ", lines));
                    break;
                }
                case "info":
                {
                    var blocks = 0;
                    foreach (var e in ours)
                    {
                        var d = Describe(e);
                        log("info " + d);
                        Reply(pid, d);
                        try { blocks += e.Structure?.BlockCount ?? 0; } catch { }
                    }
                    Reply(pid, $"{ours.Count} ship(s), {blocks} blocks total, {playfield.Entities.Count} entities in playfield");
                    break;
                }
                default: Reply(pid, "unknown " + verb); break;
            }
        }

        /// <summary>
        /// Every getter is read separately and guarded: on a freshly spawned, not-yet-ready structure
        /// the game's own getters throw (DamageLevel divides by zero), and an exception escaping into
        /// OnEntityLoaded propagates into the game's EntityAdded.
        /// </summary>
        static string Safe<T>(Func<T> get, string fmt = null)
        {
            try { var v = get(); return fmt != null && v is IFormattable f ? f.ToString(fmt, null) : $"{v}"; }
            catch (Exception ex) { return "!" + ex.GetType().Name; }
        }

        string Describe(IEntity e)
        {
            var s = e.Structure;
            var st = s == null ? "no structure" :
                $"ready={Safe(() => s.IsReady)} powered={Safe(() => s.IsPowered)} " +
                $"shield={Safe(() => s.ShieldLevel)}({(Safe(() => s.IsShieldActive) == "True" ? "on" : "off")}) " +
                $"dmg={Safe(() => s.DamageLevel, "0.00")} fuel={Safe(() => s.Fuel, "0")} blocks={Safe(() => s.BlockCount)} " +
                $"devices={Safe(() => s.DeviceCount)} power={Safe(() => s.PowerConsumption)}/{Safe(() => s.PowerOutCapacity)} " +
                $"core={Safe(() => s.CoreType)} class={Safe(() => s.SizeClass)}";
            return $"{e.Id} '{e.Name}' {e.Type} faction={Safe(() => e.Faction.Group)}/{Safe(() => e.Faction.Id)} {st}";
        }

        void Reply(int pid, string text) =>
            api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("say", pid, text), playfield?.Name);
    }
}
