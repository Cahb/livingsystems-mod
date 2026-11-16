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
        const int NpcAdminCore = 1360;   // admin core = indestructible structure
        static readonly HashSet<int> CoreBlocks = new HashSet<int> { 558, 560, 1360, 1361, 1401, 1402 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastTick, lastReport;

        /// <summary>Order status of one of our ships as seen by a player ("following you at 500 m"), from the flight driver.</summary>
    public Func<int, int, string> ShipStatus;
    /// <summary>What a ship says when hailed ("what's up?": where it's headed and why); "" = nothing special.</summary>
    public Func<int, int, string> ShipSays;
    /// <summary>Registered as ours (by entity id, from the flight driver's registry view). Never decide ownership by name.</summary>
    public Func<int, bool> IsOurShip;
    /// <summary>Adds a line to the COMMS feed (flight driver's event list).</summary>
    public Action<string> FeedEvent;

    public PfSpike(IModApi api, Action<string> log)
        {
            this.api = api; this.log = log;
            log("PF spike ready");
        }

        int lastPid;
        readonly Dictionary<int, int> kioskFix = new Dictionary<int, int>();   // kiosk id -> player to notify

        // Kiosk "invulnerability" by regeneration: the block layout recorded at fit-up, restored once a second.
        sealed class BlockRec { public VectorInt3 Pos; public int Type, Shape, Rot; }
        readonly Dictionary<int, List<BlockRec>> kioskBlocks = new Dictionary<int, List<BlockRec>>();
        double lastRegen;
        readonly Dictionary<int, int> kioskTrader = new Dictionary<int, int>();             // kiosk id -> current trader NPC id
        readonly Dictionary<int, HashSet<int>> kioskNpcs = new Dictionary<int, HashSet<int>>(); // every NPC id a kiosk ever claimed
        readonly Dictionary<int, string> npcSig = new Dictionary<int, string>();
        readonly Dictionary<int, Vector3> traderHome = new Dictionary<int, Vector3>();   // where each claimed trader first stood
        readonly Dictionary<int, VectorInt3> kioskTraderCell = new Dictionary<int, VectorInt3>(); // trader block cell in the kiosk grid

        /// <summary>
        /// An NPC belongs to a kiosk only if it stands ON that kiosk's trader block (within one cell, in the kiosk's own grid).
        /// No radius sweeps: a station's own trader nearby must never match.
        /// </summary>
        int? KioskOwning(IEntity npc)
        {
            if (npc == null || npc.Type != EntityType.Trader) return null;
            foreach (var kv in kioskTraderCell)
            {
                if (!playfield.Entities.TryGetValue(kv.Key, out var k) || k?.Structure == null) continue;
                VectorInt3 sp; try { sp = k.Structure.GlobalToStructPos(npc.Position); } catch { continue; }
                var c = kv.Value;
                if (Math.Abs(sp.x - c.x) <= 1 && Math.Abs(sp.z - c.z) <= 1 && sp.y >= c.y - 1 && sp.y <= c.y + 2) return kv.Key;
            }
            return null;
        }

        void Claim(int kioskId, IEntity npc)
        {
            if (!kioskNpcs.TryGetValue(kioskId, out var set)) kioskNpcs[kioskId] = set = new HashSet<int>();
            if (!set.Add(npc.Id)) return;
            // Name it with our tag too, so it is recognisable in game and in logs.
            api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("npcname", npc.Id, StructureTag.Apply(KioskNames.Npc)), playfield.Name);
            log($"kiosk {kioskId}: claimed trader NPC {npc.Id} (was '{npc.Name}')");
        }

        /// <summary>A new trader on a kiosk means the previous one died (the block respawns it): remove the old body.</summary>
        void OnTraderLoaded(IEntity npc)
        {
            var kid = KioskOwning(npc);
            if (!kid.HasValue) return;
            if (kioskTrader.TryGetValue(kid.Value, out var old) && old != npc.Id && playfield.Entities.ContainsKey(old)
                && kioskNpcs.TryGetValue(kid.Value, out var mine) && mine.Contains(old))
            {
                playfield.RemoveEntity(old);
                log($"kiosk {kid.Value}: trader {old} replaced by {npc.Id}, old body removed");
            }
            Claim(kid.Value, npc);
            kioskTrader[kid.Value] = npc.Id;
        }

        /// <summary>Remove only NPC ids this kiosk claimed. Nothing else is ever touched.</summary>
        void ClearKioskNpcs(int kioskId)
        {
            if (kioskNpcs.TryGetValue(kioskId, out var set))
                foreach (var id in set)
                    if (playfield.Entities.ContainsKey(id)) { playfield.RemoveEntity(id); log($"kiosk {kioskId}: removed its trader NPC {id}"); }
            kioskBlocks.Remove(kioskId); kioskTrader.Remove(kioskId); kioskNpcs.Remove(kioskId); kioskTraderCell.Remove(kioskId);
        }


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
        // Nothing of ours survives a load: for the first minute, sweep kiosks and force TRADE_PAD_ACTIVATE levers OFF.
        cleanupUntil = clock.Elapsed.TotalSeconds + 60;
        lastSweep = 0;
        foreach (var l in levers.Values) l.SeenSinceLoad = false;
        log("load cleanup: kiosks swept for 60 s; trade levers ON at first sighting are reset");
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
                else if (e != null && (int)e.Type == 13)
                {
                    OnTraderLoaded(e);
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
                if (u == null || (u.Type != EntityType.Trader && !kioskTrader.ContainsValue(u.Id))) return;
                var mine = kioskTrader.ContainsValue(u.Id) ? " (OUR kiosk trader)" : "";
                log($"ENTITY UNLOADED: {u.Id} '{u.Name}' {u.Type} belongsTo={Safe(() => u.BelongsTo)} at {u.Position}{mine}; still in Entities: {playfield.Entities.ContainsKey(u.Id)}");
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
            FixKiosks();
            PollLevers();
            PollComms();
            SweepOrphans();
            if (kioskBlocks.Count > 0 && clock.Elapsed.TotalSeconds - lastRegen >= 1.0)
            {
                lastRegen = clock.Elapsed.TotalSeconds;
                RegenKiosks();
            }
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

    /// <summary>
    /// After a kiosk copy spawns and is ready: admin core first (swapping the core RESETS the faction), then
    /// Polaris, then the trader on top of the core (exports drop trader blocks). If it was called to a TRADE_PAD,
    /// align it with the host ship and move it so its core sits exactly where the pad request wanted it.
    /// Finally record the layout for regeneration.
    /// </summary>
    private void FixKiosks()
    {
        if (kioskFix.Count == 0) return;
        foreach (var id in kioskFix.Keys.ToList())
        {
            if (!playfield.Entities.TryGetValue(id, out var e) || e?.Structure == null) continue;
            var st = e.Structure;
            bool ready; try { ready = st.IsReady; } catch { ready = false; }
            if (!ready) continue;
            var pid = kioskFix[id]; kioskFix.Remove(id);
            wantFaction.Remove(id);

            VectorInt3 mn, mx; try { mn = st.MinPos; mx = st.MaxPos; } catch { continue; }
            VectorInt3? core = null;
            for (int x = mn.x; x <= mx.x && core == null; x++)
            for (int y = mn.y + 128; y <= mx.y + 128 && core == null; y++)
            for (int z = mn.z; z <= mx.z && core == null; z++)
            {
                int t, sh, r; bool a;
                try { st.GetBlock(x, y, z).Get(out t, out sh, out r, out a); } catch { continue; }
                if (CoreBlocks.Contains(t)) core = new VectorInt3(x, y, z);
            }
            if (!core.HasValue) { log($"kiosk {id}: no core found"); Reply(pid, $"kiosk {id}: no core found"); continue; }

            int ct, csh, cr; bool ca; st.GetBlock(core.Value).Get(out ct, out csh, out cr, out ca);
            if (ct != NpcAdminCore) st.GetBlock(core.Value).Set(NpcAdminCore, null, cr, true);
            st.SetFaction(FactionGroup.Polaris, 0);

            var top = new VectorInt3(core.Value.x, core.Value.y + 1, core.Value.z);
            int tt, tsh, tr; bool ta; st.GetBlock(top).Get(out tt, out tsh, out tr, out ta);
            if (tt != TraderBlock) st.GetBlock(top).Set(TraderBlock, 0, tr, true);
            kioskTraderCell[id] = top;
            log($"kiosk {id}: core {ct}({ItemName(ct)})->admin, Polaris, trader on {top.x},{top.y},{top.z} (was {tt})");

            // Pad alignment: rotate like the host, then shift so the core lands on the requested world point.
            if (padRequest.TryGetValue(pid, out var req) && playfield.Entities.TryGetValue(req.HostId, out var host))
            {
                padRequest.Remove(pid);
                e.Rotation = host.Rotation;
                var target = req.Target;
                try
                {
                    var pp = host.Structure.GetDevicePositions("TRADE_PAD");
                    if (pp != null && pp.Count > 0) target = host.Structure.StructToGlobalPos(new VectorInt3(pp[0].x, pp[0].y + 2, pp[0].z));
                }
                catch { }
                if (Vector3.Distance(target, req.Target) > 0.5f) log($"kiosk {id}: host moved {Vector3.Distance(target, req.Target):0.00} m since the request; using the current pad position");
                req.Target = target;
                var coreWorld = st.StructToGlobalPos(core.Value);
                var delta = req.Target - coreWorld;
                e.Position = e.Position + delta;
                var after = st.StructToGlobalPos(core.Value);
                VectorInt3 padNow = default(VectorInt3);
                try { var pp = host.Structure.GetDevicePositions("TRADE_PAD"); if (pp != null && pp.Count > 0) padNow = pp[0]; } catch { }
                kioskHost[id] = new HostSnap { HostId = req.HostId, Pos = host.Position, Rot = host.Rotation, Pid = pid,
                                               Faction = host.Faction, Pad = padNow, Expires = clock.Elapsed.TotalSeconds + KioskLifetime,
                                               PartnerId = tradePartner.TryGetValue(pid, out var pp0) ? pp0 : 0 };
                // The shift is the prefab's pivot offset, constant in kiosk-local space: learn it so the next
                // kiosk spawns already in place (no visible slide). Accumulate, in case the first guess was partial.
                // (No offset learning: a ship nudged between request and fit-up polluted it and the error accumulated,
                //  spawning kiosks inside the hull. Align fresh every time instead.)
                log($"kiosk {id}: aligned to host {req.HostId}: shift {delta} ({delta.magnitude:0.00} m), core now {after}, target {req.Target}, error {Vector3.Distance(after, req.Target):0.00} m");
            }

            var recs = new List<BlockRec>();
            for (int x = mn.x; x <= mx.x; x++)
            for (int y = mn.y + 128; y <= mx.y + 128; y++)
            for (int z = mn.z; z <= mx.z; z++)
            {
                int t, sh, r; bool a;
                try { st.GetBlock(x, y, z).Get(out t, out sh, out r, out a); } catch { continue; }
                if (t != 0) recs.Add(new BlockRec { Pos = new VectorInt3(x, y, z), Type = t, Shape = sh, Rot = r });
            }
            kioskBlocks[id] = recs;
            Reply(pid, "Kiosk docked. The trader is beaming over…");
        }
    }

    /// <summary>
    /// Call a kiosk onto host's TRADE_PAD: space only, one per ship, 3x3x3 clear above the pad in the ship's
    /// frame. Returns the message for the player.
    /// </summary>
    /// <summary>Player id -> the NPC ship/station they were approved to trade with (via hail).</summary>
    readonly Dictionary<int, int> tradePartner = new Dictionary<int, int>();
    const float PartnerRange = 1300f;   // kiosk ends beyond this; the Dedi approves within 1100 (hysteresis, no edge flicker)

    /// <summary>NPC-owned = not a player faction and not unowned (group 0 is player factions, 1 = Player).</summary>
    string PartnerProblem(int partnerId, IEntity host)
    {
        if (!playfield.Entities.TryGetValue(partnerId, out var pn) || pn == null) return "your trade partner is gone";
        if (!IsNpcOwned(pn)) return "your trade partner changed hands";
        if (Vector3.Distance(pn.Position, host.Position) > PartnerRange) return "your trade partner is out of range";
        return null;
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

    string DeployPad(int pid, IEntity e)
    {
        // A kiosk is now always somebody's: the partner approved via /ls hail, still in range and still NPC-owned.
        if (!tradePartner.TryGetValue(pid, out var partnerId) || !playfield.Entities.TryGetValue(partnerId, out var partner) || partner == null)
            return "No trade partner. Use /ls hail, pick a ship, and Request trade first.";
        if (!IsNpcOwned(partner)) return $"'{partner.Name}' is no longer an NPC vessel.";
        if (Vector3.Distance(partner.Position, e.Position) > PartnerRange)
            return $"'{partner.Name}' is {Vector3.Distance(partner.Position, e.Position):0} m away — come within {PartnerRange:0} m.";
        if (!string.Equals(playfield.PlayfieldType, "Space", StringComparison.OrdinalIgnoreCase))
            return $"trade kiosks only deploy in space (this is {playfield.PlayfieldType})";
        var st = e.Structure;
        List<VectorInt3> pads; try { pads = st.GetDevicePositions("TRADE_PAD"); } catch { pads = null; }
        if (pads == null || pads.Count == 0) return $"no TRADE_PAD on '{e.Name}'";
        if (KioskOfHost(e.Id).HasValue) return $"'{e.Name}' already has a kiosk alongside";
        var pad = pads[0];
        var blocked = new List<string>();
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = 1; dy <= 3; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            int t, sh, r; bool a;
            try { st.GetBlock(pad.x + dx, pad.y + dy, pad.z + dz).Get(out t, out sh, out r, out a); } catch { continue; }
            if (t != 0 && blocked.Count < 5) blocked.Add($"{ItemName(t)} at +{dx},+{dy},+{dz}");
        }
        if (blocked.Count > 0) return $"TRADE_PAD on '{e.Name}' is blocked: " + string.Join("; ", blocked);
        var world = st.StructToGlobalPos(new VectorInt3(pad.x, pad.y + 2, pad.z));
        padRequest[pid] = new PadReq { HostId = e.Id, Target = world };
        var eul = e.Rotation.eulerAngles;

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        api.Network.SendToDedicatedServer(LivingSystemsMod.Channel,
            Wire.Pack("padspawn", pid, world.x.ToString(ci), world.y.ToString(ci), world.z.ToString(ci), eul.x.ToString(ci), eul.y.ToString(ci), eul.z.ToString(ci)),
            playfield.Name);
        log($"pad: '{e.Name}' TRADE_PAD at {pad.x},{pad.y},{pad.z} -> kiosk at {world} rot {eul}");
        FeedEvent?.Invoke($"kiosk called to {StructureTag.Strip(e.Name)}");
        return "Hailing a trade kiosk… it docks at your TRADE_PAD, trader en route (a few seconds).";
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

    int? KioskOfHost(int hostId)
    {
        foreach (var kv in kioskHost) if (kv.Value.HostId == hostId && playfield.Entities.ContainsKey(kv.Key)) return kv.Key;
        return null;
    }

    void RemoveKiosk(int kioskId, int pid, string why)
    {
        log($"kiosk {kioskId}: removing, {why}");
        FeedEvent?.Invoke("trade kiosk withdrawn");
        kioskFix.Remove(kioskId); wantFaction.Remove(kioskId);
        ClearKioskNpcs(kioskId); kioskHost.Remove(kioskId);
        if (playfield.Entities.ContainsKey(kioskId)) playfield.RemoveEntity(kioskId);
        api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("kioskgone", pid, kioskId), playfield.Name);
    }

    // ---- TRADE_PAD_ACTIVATE lever: level-triggered (switch state = desired state), debounced ----
    sealed class LeverState { public double BusyUntil; public double NextScan; public bool NeedsReset; public bool SeenSinceLoad; }
    readonly Dictionary<int, LeverState> levers = new Dictionary<int, LeverState>();   // host id -> state
    double lastLeverPoll, lastLeverScan;
    readonly List<int> leverHosts = new List<int>();
    const string LeverName = "TRADE_PAD_ACTIVATE";
    string lastLeverHosts = "";
    readonly Dictionary<int, bool> lastLever = new Dictionary<int, bool>();
    readonly Dictionary<int, string> lastLeverRaw = new Dictionary<int, string>();

    /// <summary>A lever/switch is a signal SENDER: its name is the signal name, not a device custom name.</summary>
    bool HasLever(IStructure st)
    {
        try
        {
            var sigs = st.GetBlockSignals(LeverName);
            if (sigs != null && sigs.Any(x => string.Equals(x.Name, LeverName, StringComparison.OrdinalIgnoreCase))) return true;
            var cp = st.GetControlPanelSignals();
            if (cp != null && cp.Any(x => string.Equals(x.Name, LeverName, StringComparison.OrdinalIgnoreCase))) return true;
            var dev = st.GetDevicePositions(LeverName);
            return dev != null && dev.Count > 0;
        }
        catch { return false; }
    }
    const double LeverCooldown = 5.0;

    void PollLevers()
    {
        var now = clock.Elapsed.TotalSeconds;
        if (now - lastLeverPoll < 0.5) return;
        lastLeverPoll = now;
        var inCleanup = now < cleanupUntil;
        if (now - lastLeverScan > (inCleanup ? 2 : 5))   // rescan which structures have the lever
        {
            lastLeverScan = now;
            leverHosts.Clear();
            var players = playfield.Players?.Values.Where(p => p != null).ToList() ?? new List<IPlayer>();
            foreach (var e in playfield.Entities.Values)
            {
                if (e?.Structure == null || StructureTag.IsTagged(e.Name) || e.Type == EntityType.Player) continue;
                if (!inCleanup && !players.Any(p => Vector3.Distance(p.Position, e.Position) < 300f)) continue;
                if (HasLever(e.Structure)) leverHosts.Add(e.Id);
            }
            var names = string.Join(", ", leverHosts.Select(id => playfield.Entities.TryGetValue(id, out var h) ? h.Name : id.ToString()));
            if (names != lastLeverHosts) { lastLeverHosts = names; log($"lever scan: {leverHosts.Count} ship(s) with {LeverName}: {names}"); }
        }
        foreach (var hostId in leverHosts)
        {
            if (!playfield.Entities.TryGetValue(hostId, out var host) || host?.Structure == null) continue;
            if (!levers.TryGetValue(hostId, out var ls)) levers[hostId] = ls = new LeverState();
            if (now < ls.BusyUntil) continue;                         // deploying / cooling down: ignore flips
            // Read both: the named signal, and the lever block's own switch state. Whichever follows the flips wins.
            bool? sig = null, sw = null;
            try { sig = host.Structure.GetSignalState(LeverName); } catch { }
            try
            {
                var dp = host.Structure.GetDevicePositions(LeverName);
                if (dp != null && dp.Count > 0) sw = host.Structure.GetBlock(dp[0]).GetSwitchState();
            }
            catch { }
            var raw = $"signal={(sig.HasValue ? sig.Value.ToString() : "n/a")} switch={(sw.HasValue ? sw.Value.ToString() : "n/a")}";
            if (!lastLeverRaw.TryGetValue(hostId, out var prevRaw) || prevRaw != raw) { lastLeverRaw[hostId] = raw; log($"lever raw on '{host.Name}': {raw}"); }
            bool? on = (sw == true || sig == true) ? true : (sw.HasValue || sig.HasValue ? false : (bool?)null);
            if (!on.HasValue) continue;
            if (!lastLever.TryGetValue(hostId, out var was) || was != on.Value) { lastLever[hostId] = on.Value; log($"lever on '{host.Name}' is {(on.Value ? "ON" : "OFF")}"); }
            if (ls.NeedsReset) { if (!on.Value) ls.NeedsReset = false; continue; }   // after a movement detach: OFF then ON again
            // A lever is judged ONCE after a load: ON at its first sighting = stale state from the save -> OFF.
            // Flips after that are real requests, even inside the cleanup window.
            if (!ls.SeenSinceLoad)
            {
                ls.SeenSinceLoad = true;
                if (on.Value && !KioskOfHost(hostId).HasValue) { log($"load cleanup: '{host.Name}' lever was ON from the save"); LeverOff(hostId); continue; }
            }
            var kiosk = KioskOfHost(hostId);
            var deploying = padRequest.Values.Any(r => r.HostId == hostId);
            var pid = playfield.Players.Values.Where(p => p != null).OrderBy(p => Vector3.Distance(p.Position, host.Position)).Select(p => p.Id).FirstOrDefault();
            if (on.Value && !kiosk.HasValue && !deploying)
            {
                ls.BusyUntil = now + LeverCooldown;
                var msg = DeployPad(pid, host);
                log($"lever ON on '{host.Name}': {msg}");
                if (pid != 0) Reply(pid, msg);
                // Refused (no partner, too far, pad blocked, not in space...) -> lever back OFF, so its position
                // always means "a kiosk is out or coming" and it doesn't retry/spam every cooldown.
                if (!msg.StartsWith("Hailing a trade kiosk")) LeverOff(hostId);
            }
            else if (!on.Value && kiosk.HasValue)
            {
                ls.BusyUntil = now + LeverCooldown;
                RemoveKiosk(kiosk.Value, pid, "lever OFF");
                if (pid != 0) Reply(pid, "Kiosk dismissed.");
            }
        }
    }


    // Pad requests and kiosk -> host binding (a kiosk detaches when its host ship moves).
    sealed class PadReq { public int HostId; public Vector3 Target; }
    sealed class HostSnap { public int HostId, Pid, PartnerId; public Vector3 Pos; public Quaternion Rot; public FactionData Faction; public VectorInt3 Pad; public double Expires; }
    readonly Dictionary<int, PadReq> padRequest = new Dictionary<int, PadReq>();
    readonly Dictionary<int, HostSnap> kioskHost = new Dictionary<int, HostSnap>();
    Vector3 kioskPivotLocal = Vector3.zero;
    const double KioskLifetime = 15 * 60;
    readonly HashSet<int> everKiosk = new HashSet<int>();
    double cleanupUntil;
    EntityDelegate entityUnloaded;
    IPlayfield entityUnloadedOn;   // after a playfield load / logic reload: stale state from the save is cleaned, not acted on   // every kiosk id we spawned, alive or not
    double lastSweep;

    /// <summary>
    /// Orphans: a «LS» kiosk we are not tracking, or a trader NPC whose BelongsTo is a kiosk WE spawned that no
    /// longer exists (e.g. dismissed before its NPC appeared). Nothing else is ever touched.
    /// </summary>
    void SweepOrphans()
    {
        var now = clock.Elapsed.TotalSeconds;
        if (now - lastSweep < 2) return;
        lastSweep = now;
        foreach (var e in playfield.Entities.Values.ToList())
        {
            if (e == null) continue;
            if (KioskNames.IsKiosk(e.Name) && !kioskHost.ContainsKey(e.Id) && !kioskFix.ContainsKey(e.Id) && !kioskBlocks.ContainsKey(e.Id))
            {
                log($"sweep: untracked kiosk {e.Id}, removing");
                everKiosk.Add(e.Id);   // so its NPC (BelongsTo = this id) is swept next round even if we never claimed it
                ClearKioskNpcs(e.Id); playfield.RemoveEntity(e.Id);
                api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("kioskgone", 0, e.Id), playfield.Name);
                continue;
            }
            if (e.Type == EntityType.Trader && int.TryParse(Safe(() => e.BelongsTo), out var owner) && everKiosk.Contains(owner) && !playfield.Entities.ContainsKey(owner))
            {
                log($"sweep: trader {e.Id} '{e.Name}' belonged to vanished kiosk {owner}, removing");
                playfield.RemoveEntity(e.Id);
            }
        }
    }

    /// <summary>Keep the lever honest: whenever a kiosk goes away for any reason other than the lever, flip it OFF.</summary>
    void LeverOff(int hostId)
    {
        if (!playfield.Entities.TryGetValue(hostId, out var host) || host?.Structure == null) return;
        try
        {
            var dp = host.Structure.GetDevicePositions(LeverName);
            if (dp != null && dp.Count > 0) { host.Structure.GetBlock(dp[0]).SetSwitchState(false); log($"lever on '{host.Name}' flipped OFF by the mod"); }
        }
        catch (Exception ex) { log("LeverOff threw: " + ex.Message); }
        if (levers.TryGetValue(hostId, out var ls)) { ls.NeedsReset = false; ls.BusyUntil = clock.Elapsed.TotalSeconds + LeverCooldown; }
    }   // learned correction from requested core point to spawn point, kiosk-local

        void RegenKiosks()
        {
            foreach (var id in kioskBlocks.Keys.ToList())
            {
                if (!playfield.Entities.TryGetValue(id, out var e) || e?.Structure == null) { kioskBlocks.Remove(id); continue; }
                if (!kioskTrader.ContainsKey(id))
                {
                    var first = playfield.Entities.Values.FirstOrDefault(x => x != null && x.Type == EntityType.Trader && KioskOwning(x) == id);
                    if (first != null) { kioskTrader[id] = first.Id; Claim(id, first); }
                }
                // Detach when the host ship moved/turned (or vanished): the kiosk does not follow ships.
                if (kioskHost.TryGetValue(id, out var hs))
                {
                    string why = null;
                    if (!playfield.Entities.TryGetValue(hs.HostId, out var host) || host == null || host.Structure == null) why = "your ship is gone";
                    else if (Vector3.Distance(host.Position, hs.Pos) > 3f) why = $"your ship moved {Vector3.Distance(host.Position, hs.Pos):0} m";
                    else if (Quaternion.Angle(host.Rotation, hs.Rot) > 3f) why = $"your ship turned {Quaternion.Angle(host.Rotation, hs.Rot):0}°";
                    else if (host.Faction.Group != hs.Faction.Group || host.Faction.Id != hs.Faction.Id) why = "your ship changed hands (core lost?)";
                    else if (clock.Elapsed.TotalSeconds > hs.Expires) why = "the kiosk's time is up";
                    else if (hs.PartnerId != 0) why = PartnerProblem(hs.PartnerId, host);
                    else
                    {
                        List<VectorInt3> pp = null; try { pp = host.Structure.GetDevicePositions("TRADE_PAD"); } catch { }
                        if (pp == null || pp.Count == 0) why = "your TRADE_PAD is gone";
                        else if (pp[0].x != hs.Pad.x || pp[0].y != hs.Pad.y || pp[0].z != hs.Pad.z) why = "your TRADE_PAD moved";
                        else if (leverHosts.Contains(hs.HostId) && !HasLever(host.Structure)) why = "your trade lever is gone";
                    }
                    if (why != null)
                    {
                        log($"kiosk {id}: detaching, {why}");
                        LeverOff(hs.HostId);
                        ClearKioskNpcs(id); kioskHost.Remove(id);
                        playfield.RemoveEntity(id);
                        api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("kioskgone", hs.Pid, id), playfield.Name);
                        Reply(hs.Pid, $"Kiosk detached: {why}.");
                        continue;
                    }
                }
                // Trader death ends the call: hostile action means no trade. No health getter in the API, so "dead" =
                // gone, not a Trader any more, no longer belongs to this kiosk, or moved >1.2 m from where it stood
                // (a living trader stays put; a corpse ragdolls and falls).
                if (kioskTrader.TryGetValue(id, out var liveId))
                {
                    string dead = null;
                    if (!playfield.Entities.TryGetValue(liveId, out var npcNow) || npcNow == null) dead = "trader gone";
                    else if (npcNow.Type != EntityType.Trader) dead = $"trader type now {npcNow.Type}";
                    else if (Safe(() => npcNow.BelongsTo) != id.ToString()) dead = $"trader belongsTo {Safe(() => npcNow.BelongsTo)}";
                    // (Position is NOT a death signal: live traders hop ~1.6 m on their own, corpses stay put. Verified.)
                    if (dead != null)
                    {
                        var who = kioskHost.TryGetValue(id, out var hsd) ? hsd.Pid : lastPid;
                        log($"kiosk {id}: ending call, {dead}");
                        ClearKioskNpcs(id); kioskHost.Remove(id);
                        playfield.RemoveEntity(id);
                        api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("kioskgone", who, id), playfield.Name);
                        Reply(who, "You opened fire on the trader. The kiosk withdraws.");
                        continue;
                    }
                }
                // Diagnostic: what changes on a trader NPC when it dies? (API has no health getter.)
                if (kioskTrader.TryGetValue(id, out var tid))
                {
                    string sig;
                    if (!playfield.Entities.TryGetValue(tid, out var npc) || npc == null) sig = "GONE from Entities";
                    else sig = $"type={npc.Type} name='{npc.Name}' proxy={Safe(() => npc.IsProxy)} local={Safe(() => npc.IsLocal)} belongsTo={Safe(() => npc.BelongsTo)} pos={npc.Position}";
                    if (kioskTraderCell.TryGetValue(id, out var cell))
                    {
                        try
                        {
                            var tb = e.Structure.GetBlock(cell); int bt, bs, br; bool ba; tb.Get(out bt, out bs, out br, out ba);
                            sig += $" | block type={bt} active={ba} dmg={Safe(() => tb.GetDamage())} hp={Safe(() => tb.GetHitPoints())}";
                        }
                        catch (Exception ex) { sig += " | block read failed: " + ex.Message; }
                    }
                    if (!npcSig.TryGetValue(tid, out var prev) || prev != sig) { npcSig[tid] = sig; log($"kiosk {id}: trader {tid} {sig}"); }
                }
                var st = e.Structure; int healed = 0, rebuilt = 0;
                FactionGroup fg; try { fg = e.Faction.Group; } catch { fg = FactionGroup.Polaris; }
                if (fg != FactionGroup.Polaris) { st.SetFaction(FactionGroup.Polaris, 0); log($"kiosk {id}: faction was {fg}, re-set Polaris"); }
                foreach (var b in kioskBlocks[id])
                {
                    try
                    {
                        var blk = st.GetBlock(b.Pos);
                        int t, sh, r; bool a; blk.Get(out t, out sh, out r, out a);
                        if (t != b.Type) { blk.Set(b.Type, b.Shape, b.Rot, true); rebuilt++; }
                        else if (blk.GetDamage() > 0) { blk.SetDamage(0); healed++; }
                    }
                    catch { }
                }
                if (healed + rebuilt > 0) log($"kiosk {id}: regen healed {healed}, rebuilt {rebuilt}");
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
                        .Where(t => t.d <= 5000f).OrderBy(t => t.d).ToList();
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
                    list = list.Take(5).ToList();
                    var fields = new List<object> { "hailist", pid };
                    foreach (var t in list)
                        fields.Add($"{t.x.Id}~{t.x.Name.Replace("~", "-").Replace("|", "/")}~{(int)t.x.Faction.Group}~{t.x.Faction.Id}~{t.d:0}~{(withTrader.Contains(t.x.Id) ? 1 : 0)}~{t.x.Type}~{Safe(() => t.x.Structure.BlockCount)}~{Safe(() => t.x.Structure.CoreType)}~{(ShipStatus?.Invoke(t.x.Id, pid) ?? "").Replace("~", "-").Replace("|", "/")}~{(ShipSays?.Invoke(t.x.Id, pid) ?? "").Replace("~", "-").Replace("|", "/")}~{(t == nose ? 1 : 0)}");
                    api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack(fields.ToArray()), playfield.Name);
                    log($"hail: {list.Count} contacts for {pid}");
                    break;
                }
                case "partner":
                    // Dedi approved a trade: bind this player's next kiosk to that partner.
                    if (args.Length >= 1 && int.TryParse(args[0], out var partnerId))
                    {
                        tradePartner[pid] = partnerId; log($"partner for {pid}: {partnerId}");
                        var pn = playfield.Entities != null && playfield.Entities.TryGetValue(partnerId, out var pe) && pe != null ? StructureTag.Strip(pe.Name) : partnerId.ToString();
                        FeedEvent?.Invoke($"trade approved: {pn}");
                    }
                    break;
                case "padinfo":
                {
                    // What exactly is the TRADE_PAD block (and what's around it)? Type id/name, shape, rotation, colours, textures.
                    if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) { Reply(pid, "can't find you"); return; }
                    foreach (var e in playfield.Entities.Values)
                    {
                        var st = e?.Structure; if (st == null || e.Id == pid || StructureTag.IsTagged(e.Name)) continue;
                        if (Vector3.Distance(e.Position, pl.Position) > 300f) continue;
                        List<VectorInt3> pads; try { pads = st.GetDevicePositions("TRADE_PAD"); } catch { continue; }
                        if (pads == null || pads.Count == 0) continue;
                        var pad = pads[0];
                        string Desc(VectorInt3 c)
                        {
                            var b = st.GetBlock(c); int t, sh, r; bool a; b.Get(out t, out sh, out r, out a);
                            if (t == 0) return "air";
                            int ct, cb, cn, cs, cw, ce; b.GetColors(out ct, out cb, out cn, out cs, out cw, out ce);
                            int tt, tb, tn, ts, tw, te; b.GetTextures(out tt, out tb, out tn, out ts, out tw, out te);
                            return $"{ItemName(t)} (id {t}) shape={sh} rot={r} active={a} name='{Safe(() => b.CustomName)}' colours[t,b,n,s,w,e]={ct},{cb},{cn},{cs},{cw},{ce} tex={tt},{tb},{tn},{ts},{tw},{te}";
                        }
                        var lines = new List<string> { $"pad at {pad.x},{pad.y},{pad.z}: {Desc(pad)}" };
                        foreach (var d in new[] { new VectorInt3(0, -1, 0), new VectorInt3(0, 1, 0), new VectorInt3(1, 0, 0), new VectorInt3(-1, 0, 0), new VectorInt3(0, 0, 1), new VectorInt3(0, 0, -1) })
                            lines.Add($"  {d.x:+0;-0;0},{d.y:+0;-0;0},{d.z:+0;-0;0}: {Desc(new VectorInt3(pad.x + d.x, pad.y + d.y, pad.z + d.z))}");
                        foreach (var l in lines) log("padinfo " + l);
                        Reply(pid, lines[0]);
                        return;
                    }
                    Reply(pid, "no TRADE_PAD within 300 m");
                    break;
                }
                case "purge":
                {
                    // Dev: every «LS» structure in this playfield, removed here and destroyed globally by the Dedi.
                    var mine = playfield.Entities.Values.Where(x => x != null && x.Structure != null && StructureTag.IsTagged(x.Name)).ToList();
                    foreach (var x in mine)
                    {
                        ClearKioskNpcs(x.Id); kioskHost.Remove(x.Id); kioskFix.Remove(x.Id);
                        playfield.RemoveEntity(x.Id);
                        api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("kioskgone", pid, x.Id), playfield.Name);
                    }
                    log($"purge: {mine.Count} «LS» structures removed");
                    Reply(pid, $"purged {mine.Count} «LS» structures here (also destroyed globally)");
                    break;
                }
                case "kioskoff":
                    if (args.Length >= 1 && int.TryParse(args[0], out var koid))
                    {
                        if (kioskHost.TryGetValue(koid, out var kh)) LeverOff(kh.HostId);
                        RemoveKiosk(koid, pid, "kiosk off");
                    }
                    break;
                case "kioskfix":
                    if (args.Length >= 1 && int.TryParse(args[0], out var kfid)) { kioskFix[kfid] = pid; everKiosk.Add(kfid); log($"will fit trader on kiosk {kfid} when ready"); }
                    break;
                case "wantfaction":
                    if (args.Length >= 3 && int.TryParse(args[0], out var wid) && byte.TryParse(args[1], out var wg) && int.TryParse(args[2], out var wf))
                    { wantFaction[wid] = new KeyValuePair<byte, int>(wg, wf); log($"will SetFaction({(FactionGroup)wg}, {wf}) on {wid} when ready"); }
                    break;
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
                case "makekiosk":
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

                    if (verb == "record")
                    {
                        Reply(pid, $"{target.Id} '{target.Name}' {target.Type}: {blocks.Count} blocks, core {(core.HasValue ? $"{ItemName(coreType)} at {core.Value.x},{core.Value.y},{core.Value.z}" : "NOT FOUND")}. Full dump in playfield log.");
                        return;
                    }

                    // makekiosk: core -> NPC admin core, block on top of the core (the seat) -> Trader, then export.
                    if (!core.HasValue) { Reply(pid, "no core found, can't make a kiosk"); return; }
                    var c = core.Value;
                    st.GetBlock(c).Set(NpcAdminCore, null, coreRot, true);
                    var top = new VectorInt3(c.x, c.y + 1, c.z);
                    int tt, tsh, tr; bool ta; st.GetBlock(top).Get(out tt, out tsh, out tr, out ta);
                    st.GetBlock(top).Set(TraderBlock, 0, tr, true);
                    int nc, ncs, ncr; bool nca; st.GetBlock(c).Get(out nc, out ncs, out ncr, out nca);
                    int nt, nts, ntr; bool nta; st.GetBlock(top).Get(out nt, out nts, out ntr, out nta);
                    log($"makekiosk {target.Id}: core {coreType}->{nc} at {c.x},{c.y},{c.z}; top {tt}({ItemName(tt)})->{nt} at {top.x},{top.y},{top.z}");
                    // Ask the Dedi to export it into the mod's prefabs folder.
                    api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("export", pid, target.Id, (int)target.Type), playfield.Name);
                    Reply(pid, $"kiosk made from {target.Id}: core {ItemName(coreType)}->{ItemName(nc)}, top {ItemName(tt)}->{ItemName(nt)}. Exporting as LS_Kiosk…");
                    return;
                }
                case "pad":
                {
                    if (!playfield.Players.TryGetValue(pid, out var pl) || pl == null) { Reply(pid, "can't find you"); return; }
                    foreach (var e in playfield.Entities.Values)
                    {
                        var st = e?.Structure; if (st == null || e.Id == pid || StructureTag.IsTagged(e.Name)) continue;
                        if (Vector3.Distance(e.Position, pl.Position) > 300f) continue;
                        List<VectorInt3> pads; try { pads = st.GetDevicePositions("TRADE_PAD"); } catch { continue; }
                        if (pads == null || pads.Count == 0) continue;
                        Reply(pid, DeployPad(pid, e));
                        return;
                    }
                    Reply(pid, "no device named TRADE_PAD on any structure within 300 m");
                    break;
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
