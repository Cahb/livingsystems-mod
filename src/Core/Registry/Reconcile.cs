using System.Collections.Generic;
using System.Linq;

namespace LivingSystems.Core.Registry
{
    public enum ReconcileAction
    {
        /// <summary>Ship here, order runnable now (hold, goto; or follow with the leader in this playfield).</summary>
        Resume,
        /// <summary>Ship here, follow order, leader not in this playfield: hold where it is, keep the order.</summary>
        WaitForLeader,
        /// <summary>Ship here, nothing for us to run (no order, or an order kind this version doesn't know).</summary>
        Idle,
        /// <summary>No ship yet, still within the grace period after load: ask again later.</summary>
        Pending,
        /// <summary>No ship after the grace period: it was destroyed or removed — forget it.</summary>
        Drop,
        /// <summary>An agent without a ship by design (it left the system — <c>state=departed</c>): keep the record, do nothing.</summary>
        Departed,
    }

    public struct ReconcileDecision
    {
        public ShipRecord Record;
        public ReconcileAction Action;
        /// <summary>Resume of a follow order: the leader's current entity id.</summary>
        public int LeaderEntityId;
    }

    /// <summary>
    /// Stored records vs what is actually in a playfield. Pure: the PfServer gathers the facts, this decides.
    /// Load is not a special case: a logic reload, a playfield load and a late-arriving leader all go through here.
    /// </summary>
    public static class Reconciler
    {
        /// <summary>Seconds after a playfield load before a missing ship counts as gone (ships appear over a few seconds).</summary>
        public const double GraceSeconds = 60;

        /// <param name="records">stored records for this playfield</param>
        /// <param name="shipsPresent">entity ids of our («LS») ships currently in the playfield</param>
        /// <param name="playersBySteamId">players currently in the playfield: Steam id → entity id</param>
        /// <param name="secondsSinceLoad">time since the playfield (or our logic) loaded</param>
        public static List<ReconcileDecision> Decide(IEnumerable<ShipRecord> records, ICollection<int> shipsPresent,
            IDictionary<string, int> playersBySteamId, double secondsSinceLoad)
        {
            var result = new List<ReconcileDecision>();
            foreach (var r in records ?? Enumerable.Empty<ShipRecord>())
            {
                if (r.Field("state") == "departed") { result.Add(new ReconcileDecision { Record = r, Action = ReconcileAction.Departed }); continue; }
                if (!shipsPresent.Contains(r.EntityId))
                {
                    result.Add(new ReconcileDecision { Record = r, Action = secondsSinceLoad >= GraceSeconds ? ReconcileAction.Drop : ReconcileAction.Pending });
                    continue;
                }
                var o = r.Order;
                if (o == null) { result.Add(new ReconcileDecision { Record = r, Action = ReconcileAction.Idle }); continue; }
                if (o.Kind == "follow")
                {
                    if (!string.IsNullOrEmpty(o.LeaderSteamId) && playersBySteamId.TryGetValue(o.LeaderSteamId, out var leader))
                        result.Add(new ReconcileDecision { Record = r, Action = ReconcileAction.Resume, LeaderEntityId = leader });
                    else
                        result.Add(new ReconcileDecision { Record = r, Action = ReconcileAction.WaitForLeader });
                    continue;
                }
                result.Add(new ReconcileDecision { Record = r, Action = o.Kind == "hold" || o.Kind == "goto" ? ReconcileAction.Resume : ReconcileAction.Idle });
            }
            return result;
        }
    }
}
