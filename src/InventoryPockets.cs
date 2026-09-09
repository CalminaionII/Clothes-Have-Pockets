using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ClothesHavePockets
{
    /// <summary>
    /// The player's pocket slots, as one inventory the ENGINE owns.
    ///
    /// WHY A REAL PLAYER INVENTORY. The engine already knows how to sync a player's
    /// inventory, save it, route a click on one of its slots from client to server, tick
    /// the transition states of what is inside it, and stop a player reaching into someone
    /// else's. Deriving from InventoryBasePlayer gets all of that. It is also what lets
    /// OTHER MODS see the pockets: Brain Freeze reaches them "through the player reference
    /// it has", in its author's words, and anything keyed the same way would skip a plain
    /// InventoryBase entirely. That compatibility is the reason this rewrite happened.
    ///
    /// THE CONTENTS LIVE HERE, AND NOWHERE ELSE. This is the 2026-09-07 rewrite and it is
    /// the whole point of it. Until then the contents lived in the player entity's
    /// WatchedAttributes and this inventory was a VIEW of them, which meant two owners for
    /// one inventory: the engine believed it owned an InventoryBasePlayer, and the mod
    /// believed WatchedAttributes did. That produced four separate bugs in four hours - a
    /// client crash on undressing, drop-on-death silently doing nothing, items lost on the
    /// first removal after a death, and a CTD on connect. All four were the same conflict
    /// seen from different angles. See docs/fixed-slot-rewrite-plan.md.
    ///
    /// THE SLOT COUNT IS A CONSTANT, and that is the load-bearing decision. The engine
    /// addresses inventory updates by slot INDEX; an inventory whose count changes under it
    /// crashes the client with "slot 2 but max is 1". So every slot that could ever exist
    /// exists from the start, and clothing decides only which of them are LIVE.
    /// </summary>
    public class InventoryPockets : InventoryBasePlayer
    {
        /// <summary>
        /// The inventory class name. Combined with the player UID this becomes the
        /// InventoryID, which MUST match between client and server or the two sides are
        /// talking about different inventories and slot clicks go nowhere.
        ///
        /// It must also be REGISTERED with the engine's class registry, or a connecting
        /// client throws before the world loads - see ClothesHavePocketsModSystem.
        /// </summary>
        public const string InventoryClassName = "clotheshavepockets";

        /// <summary>
        /// How many character (clothing) slots can carry pockets. Vanilla has about 15;
        /// this is deliberately generous so a clothing mod adding slots does not silently
        /// lose pockets off the end.
        /// </summary>
        public const int MaxCharacterSlots = 20;

        /// <summary>
        /// The most pockets any single garment may declare, and the STRIDE between one
        /// garment's block of addresses and the next.
        ///
        /// CHANGING THIS RE-ADDRESSES EVERY EXISTING POCKET, so it is set high once rather
        /// than raised when needed. A pocket's address is `characterSlot * this + index`;
        /// raise it and a saved item decodes to a different character slot and pocket. It
        /// went 5 -> 10 on 2026-09-07, before release, the moment tops were given 6 - which
        /// is exactly the sort of ordinary content change that must never cost a migration
        /// again. Ten is far beyond anything a garment plausibly wants, and it makes an
        /// address readable at a glance: slot 37 is character slot 3, pocket 7.
        ///
        /// IF IT EVER HAS TO CHANGE AFTER RELEASE, it needs the same treatment the
        /// WatchedAttributes migration got: read the old addresses, write the new ones,
        /// once, server-side. The orphan sweep alone is not a migration - it hands items
        /// back to the player rather than putting them where they belong.
        /// </summary>
        public const int MaxPocketsPerGarment = 10;

        /// <summary>
        /// The fixed size of this inventory: 200 slots, most of them inert most of the time.
        ///
        /// A CONSTANT IN CODE AND DELIBERATELY NOT CONFIGURABLE. This number has to be
        /// identical on client and server or the two are describing different inventories,
        /// and a per-side config file cannot guarantee that - an operator editing one copy
        /// would produce exactly the desynchronisation this design exists to end. Empty
        /// slots cost almost nothing to sync or save, so there is no reason to trim it.
        /// </summary>
        public const int TotalSlots = MaxCharacterSlots * MaxPocketsPerGarment;

        /// <summary>
        /// Created once in the constructor and NEVER replaced. A composed slot grid holds
        /// references to these objects; handing it fresh ones is what made items render as
        /// invisible until the window was reopened, reported 2026-08-09.
        /// </summary>
        private readonly ItemSlot[] slots = new ItemSlot[TotalSlots];

        /// <summary>
        /// Which character slot's garment owns each pocket slot, or -1 for inert.
        /// Recomputed whenever the player's clothing changes.
        /// </summary>
        private readonly int[] garmentSlotIds = new int[TotalSlots];

        /// <summary>
        /// Read only by <see cref="DropAll"/>, which the ENGINE calls and which therefore
        /// has no other route to the mod's settings.
        /// </summary>
        private PocketConfig config;

        /// <summary>
        /// Hand this inventory the mod's settings.
        ///
        /// NOT readonly, and not a constructor argument only, because the instance that
        /// matters is often NOT the one this mod built. The engine restores a saved player
        /// inventory through the class registry, which can only use the two-argument
        /// constructor and so produces one with no config. That restored instance is the
        /// one holding the player's items, so AttachInventory keeps it and calls this
        /// rather than replacing it - see the comment there, replacing it threw away
        /// everyone's pockets on every login, 2026-09-07.
        /// </summary>
        public void AttachConfig(PocketConfig config)
        {
            this.config = config;
        }

        /// <summary>
        /// Raised when the set of LIVE slots changes - the player dressed, undressed, or a
        /// mapping arrived from the server. The panel recomposes on this.
        ///
        /// Note what it no longer has to guard against. It used to fire on a change of slot
        /// COUNT, and a composed grid could be left asking for an id that had stopped
        /// existing between the change and the recompose. The count is now constant, so
        /// `inventory[id]` is always valid and that crash cannot happen; this event is now
        /// only about drawing the right number of boxes.
        /// </summary>
        public event Action LiveSlotsChanged;

        public InventoryPockets(string playerUid, ICoreAPI api, PocketConfig config)
            : base(InventoryClassName, playerUid, api)
        {
            this.config = config;
            InitSlots();
        }

        /// <summary>
        /// The constructor the CLASS REGISTRY uses. Not called by this mod.
        ///
        /// Registration is not optional: a player inventory is sent to the client in the
        /// player-data packet, and an unregistered class name throws inside
        /// ClientPlayer.AddOrUpdateInventory while the connecting screen is still up. The
        /// instance built here has no config; AttachInventory replaces it with the real one
        /// as soon as the player is attached.
        /// </summary>
        public InventoryPockets(string inventoryID, ICoreAPI api)
            : base(inventoryID, api)
        {
            InitSlots();
        }

        private void InitSlots()
        {
            for (int i = 0; i < TotalSlots; i++)
            {
                slots[i] = new ItemSlotPocket(this, i);
                garmentSlotIds[i] = -1;
            }
        }

        /// <summary>Constant. See <see cref="TotalSlots"/> for why that matters so much.</summary>
        public override int Count => TotalSlots;

        /// <summary>
        /// Always valid, for every id in range, for the whole life of the inventory.
        ///
        /// The old implementation handed out a placeholder for ids past the end, because
        /// the count moved and vanilla's GuiElementItemSlotGridBase dereferences
        /// `inventory[id]` without a null check. With a fixed count there is no past the
        /// end, so the range check here is a genuine bounds guard rather than a workaround.
        /// </summary>
        public override ItemSlot this[int slotId]
        {
            get
            {
                if (slotId < 0 || slotId >= TotalSlots) return null;
                return slots[slotId];
            }
            set
            {
                if (slotId < 0 || slotId >= TotalSlots || value == null) return;
                slots[slotId] = value;
            }
        }

        /// <summary>
        /// The pockets exist because the player is wearing the clothes, not because a
        /// window is open. (InventoryBasePlayer already says this; kept for the reader.)
        /// </summary>
        public override bool RemoveOnClose => false;

        /// <summary>
        /// Where a garment worn in the given character slot keeps its pockets.
        ///
        /// THE ADDRESS COMES FROM WHERE THE GARMENT IS WORN, never from how many pockets
        /// came before it. That is what makes the contents stay put: taking a coat off can
        /// no longer shift the trousers' pockets to different indices, so nothing has to be
        /// moved between slots and there is no shuffling logic left to get wrong. It is
        /// also what lets 1.1.3's contents migrate straight across - the old keys were
        /// already "(garment slot, pocket index)".
        /// </summary>
        public static int SlotIdFor(int characterSlotId, int pocketIndex)
        {
            if (characterSlotId < 0 || characterSlotId >= MaxCharacterSlots) return -1;
            if (pocketIndex < 0 || pocketIndex >= MaxPocketsPerGarment) return -1;

            return characterSlotId * MaxPocketsPerGarment + pocketIndex;
        }

        /// <summary>Whether a currently worn garment owns this slot.</summary>
        public bool IsLive(int slotId)
        {
            if (slotId < 0 || slotId >= TotalSlots) return false;
            return garmentSlotIds[slotId] >= 0;
        }

        /// <summary>
        /// The character slot id the given pocket slot belongs to, or -1 if inert.
        /// </summary>
        public int GarmentSlotIdFor(int pocketSlotId)
        {
            if (pocketSlotId < 0 || pocketSlotId >= TotalSlots) return -1;
            return garmentSlotIds[pocketSlotId];
        }

        /// <summary>
        /// Every live slot id, in order. This is what the panel composes its grid over, so
        /// the player sees two boxes in trousers rather than a hundred.
        /// </summary>
        public int[] LiveSlotIds()
        {
            int count = 0;
            for (int i = 0; i < TotalSlots; i++) if (garmentSlotIds[i] >= 0) count++;

            var ids = new int[count];
            int at = 0;
            for (int i = 0; i < TotalSlots; i++) if (garmentSlotIds[i] >= 0) ids[at++] = i;

            return ids;
        }

        /// <summary>
        /// Replace the live mapping. Called only by PocketMirror, only when the player's
        /// clothing has actually changed.
        ///
        /// CALLERS MUST NOT DO THIS DURING A CLICK ON ONE OF THESE SLOTS. Marking the
        /// clothing slot dirty from inside a pocket click re-triggers the rebuild
        /// mid-click; the mirror guards against it and this method cannot.
        /// </summary>
        public void SetLiveMapping(int[] newGarmentSlotIds)
        {
            if (newGarmentSlotIds == null || newGarmentSlotIds.Length != TotalSlots) return;

            bool changed = false;
            for (int i = 0; i < TotalSlots; i++)
            {
                if (garmentSlotIds[i] != newGarmentSlotIds[i]) changed = true;
                garmentSlotIds[i] = newGarmentSlotIds[i];
            }

            if (changed) LiveSlotsChanged?.Invoke();
        }

        protected override ItemSlot NewSlot(int slotId)
        {
            return new ItemSlotPocket(this, slotId);
        }

        public override void FromTreeAttributes(ITreeAttribute tree)
        {
            // The slots are filled IN PLACE - the array passed in is the one the grid holds
            // references to, and replacing it is the old invisible-items bug. The count is
            // constant, so unlike the previous design there is nothing to read back about
            // how many slots there are.
            SlotsFromTreeAttributes(tree, slots);

            bool changed = false;
            for (int i = 0; i < TotalSlots; i++)
            {
                int owner = tree.GetInt("garmentSlotId" + i, -1);
                if (garmentSlotIds[i] != owner) changed = true;
                garmentSlotIds[i] = owner;
            }

            // The mapping is authored on the server and travels with the contents, so the
            // client draws exactly the boxes the server thinks exist rather than working it
            // out again from clothing and risking a different answer.
            if (changed) LiveSlotsChanged?.Invoke();
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            SlotsToTreeAttributes(slots, tree);

            for (int i = 0; i < TotalSlots; i++)
            {
                tree.SetInt("garmentSlotId" + i, garmentSlotIds[i]);
            }
        }

        /// <summary>
        /// Drop the pockets where the player died - but only if the server wants that.
        ///
        /// CALLED BY THE ENGINE, not by this mod, because these are a player inventory now.
        /// On 2026-09-07 that showed up as "drop on death isn't working": vanilla had
        /// already emptied the slots before the mod's own PlayerDeath handler looked at
        /// them, so it found nothing and said nothing. The handler was not broken, it was
        /// second. This is where the setting has to be honoured, or DropPocketsOnDeath:
        /// false silently stops meaning anything.
        ///
        /// base.DropAll is a real drop - it spawns item entities with the player's despawn
        /// timer - so when the setting is on there is nothing to improve on.
        /// </summary>
        public override void DropAll(Vec3d pos, int maxStackSize = 0)
        {
            if (config != null && !config.DropPocketsOnDeath) return;

            base.DropAll(pos, maxStackSize);
        }
    }
}
