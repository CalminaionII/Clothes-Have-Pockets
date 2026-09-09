using Vintagestory.API.Common;

namespace ClothesHavePockets
{
    /// <summary>
    /// One pocket slot. Knows its own id, and refuses everything while it is INERT.
    ///
    /// WHY A SLOT THAT KNOWS ITS OWN ID. The inventory is a fixed 100 slots, but only the
    /// ones backed by a worn garment are usable. Which those are changes every time the
    /// player dresses. Rather than swapping slot OBJECTS in and out - which is what broke
    /// the GUI repeatedly before, because a composed slot grid holds references to the
    /// objects it was built from - the objects are created once and never replaced. Each
    /// one asks the inventory whether it is currently live.
    ///
    /// IT MUST REFUSE EVERYTHING WHILE INERT. An item placed in a slot with no garment
    /// behind it would sit at an address nothing maps to, invisible and unreachable the
    /// moment the mapping changed. Every route in is closed rather than only the obvious
    /// one, for the same reason its predecessor closed them all: silently eating a
    /// player's items is worse than any crash it might replace. That predecessor was
    /// ItemSlotVanished, retired to backup/ on 2026-09-07 - it guarded slots that had
    /// stopped EXISTING, which a constant slot count made impossible. This guards slots
    /// that exist but are not currently owned by a worn garment.
    ///
    /// Survival rules otherwise - this derives from ItemSlotSurvival so that creative-only
    /// items behave in a pocket exactly as they do in a backpack.
    /// </summary>
    public class ItemSlotPocket : ItemSlotSurvival
    {
        private readonly InventoryPockets pockets;

        /// <summary>This slot's own index in the inventory. Fixed for the slot's lifetime.</summary>
        public readonly int SlotId;

        public ItemSlotPocket(InventoryPockets inventory, int slotId) : base(inventory)
        {
            pockets = inventory;
            SlotId = slotId;
        }

        /// <summary>Whether a garment currently worn owns this slot.</summary>
        public bool Live => pockets != null && pockets.IsLive(SlotId);

        public override bool CanTake() => Live && base.CanTake();

        public override bool CanHold(ItemSlot sourceSlot) => Live && base.CanHold(sourceSlot);

        public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
            => Live && base.CanTakeFrom(sourceSlot, priority);

        public override int TryPutInto(ItemSlot sinkSlot, ref ItemStackMoveOperation op)
            => Live ? base.TryPutInto(sinkSlot, ref op) : 0;
    }
}
