using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ClothesHavePockets
{
    /// <summary>
    /// Keeps one player's pocket inventory in step with their worn clothing.
    ///
    ///     clothing changed  ->  recompute which pocket slots are LIVE, and hand back the
    ///                           contents of anything that came off
    ///
    /// One of these exists per player per side.
    ///
    /// WHAT THIS CLASS NO LONGER DOES, 2026-09-07. It used to copy the contents in and out
    /// of the player entity's WatchedAttributes on every change, because the inventory was
    /// a view and the attributes were the real store. That is gone: the inventory is an
    /// InventoryBasePlayer and the ENGINE saves and syncs it, exactly as it does the
    /// backpack. One owner, one copy. Four separate bugs on 2026-09-07 came from having
    /// two - see docs/fixed-slot-rewrite-plan.md.
    ///
    /// What is left is genuinely small: work out which slots are usable, hand back what
    /// falls out of use, and migrate anyone arriving from 1.1.3.
    /// </summary>
    public class PocketMirror
    {
        private readonly ICoreAPI api;
        private readonly IPlayer player;
        private readonly InventoryPockets inventory;

        /// <summary>
        /// The player's worn clothing. Held so the SlotModified subscription can be removed
        /// again in Dispose - see the comment there, this is the leak that RAM measurements
        /// caught on the previous project.
        /// </summary>
        private readonly InventoryBase characterInventory;

        /// <summary>
        /// Set while a rebuild is running.
        ///
        /// THIS IS THE BUG THE FIRST ATTEMPT AT THIS MOD LEFT A NOTE ABOUT. Writing into a
        /// garment marks the clothing slot dirty; the clothing inventory fires SlotModified;
        /// that triggers a rebuild in the middle of the click that started it. Less
        /// dangerous now that slot objects are never replaced, but re-entering a rebuild is
        /// still wasted work at best and confusing at worst.
        /// </summary>
        private bool suppressRebuild;

        /// <summary>
        /// The pocketed garment last seen in each character slot, by that slot's id.
        ///
        /// Kept so we can tell a garment has been REMOVED. Nothing tells us directly - the
        /// character inventory only says a slot changed - so removal is "there was a
        /// pocketed garment here last time and it is not the same stack now".
        ///
        /// SERVER-SIDE ONLY, and identity is by reference - see the long comment in
        /// HandleRemovedGarments.
        /// </summary>
        private readonly Dictionary<int, ItemStack> lastSeenGarments = new Dictionary<int, ItemStack>();

        private readonly PocketConfig config;

        /// <summary>
        /// True for the FIRST rebuild only, which is deliberately timid: it works out the
        /// mapping and does nothing else.
        ///
        /// THIS IS THE GUARD THAT DESCENDS FROM THE ITEM LOSS REPORTED 2026-08-16 - "lost
        /// everything that was in my pockets when reloading the world". The danger then was
        /// a wholesale rewrite of the saved contents running before the player's clothing
        /// had finished loading. The save is no longer ours to overwrite, so that exact
        /// failure is impossible now, but the same race has a new face: the ORPHAN SWEEP
        /// below hands the player anything sitting in a slot no garment owns, and clothing
        /// that has not loaded yet looks exactly like clothing that was taken off. So the
        /// sweep never runs on the first rebuild. A garment genuinely removed while the
        /// player was offline is dealt with on the next clothing change instead, which is a
        /// far cheaper failure than emptying someone's pockets onto the floor at login.
        /// </summary>
        private bool isFirstRebuild = true;

        /// <summary>
        /// How many pocket slots held something when the log last said so.
        /// </summary>
        private int lastFilledCount;

        public PocketMirror(ICoreAPI api, IPlayer player, InventoryPockets inventory, InventoryBase characterInventory, PocketConfig config)
        {
            this.api = api;
            this.player = player;
            this.inventory = inventory;
            this.characterInventory = characterInventory;
            this.config = config;

            characterInventory.SlotModified += OnClothingChanged;
            inventory.SlotModified += OnPocketChanged;

            // BEFORE the first rebuild, and server only. Addresses are absolute, so the
            // order does not strictly matter, but migrating first means the attach log
            // reports the state the player will actually see.
            if (api.Side == EnumAppSide.Server) MigrateFromWatchedAttributes();

            Rebuild();
            isFirstRebuild = false;

            RecountFilled();
            LogAttachState();
        }

        // ------------------------------------------------------------------ events

        private void OnClothingChanged(int characterSlotId)
        {
            if (suppressRebuild) return;
            Rebuild();
        }

        /// <summary>
        /// A pocket's contents changed. There is nothing to persist any more - the engine
        /// owns this inventory and syncs and saves it itself - so this only keeps the log
        /// counter honest.
        /// </summary>
        private void OnPocketChanged(int pocketSlotId)
        {
            if (suppressRebuild) return;
            RecountFilled();
        }

        /// <summary>Bring <see cref="lastFilledCount"/> back in line with the actual slots.</summary>
        private void RecountFilled()
        {
            lastFilledCount = FilledCount();
        }

        private int FilledCount()
        {
            int filled = 0;
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemSlot slot = inventory[i];
                if (slot != null && !slot.Empty) filled++;
            }

            return filled;
        }

        // ------------------------------------------------------------------ the rebuild

        /// <summary>
        /// Work out which pocket slots are live from what the player is wearing.
        ///
        /// NOTHING IS COPIED ANYWHERE. A pocket's address is fixed by the character slot its
        /// garment is worn in, so a garment coming off cannot move another garment's pockets
        /// and there is no carrying of contents between slots. That is the single change
        /// that removes most of what this class used to be.
        /// </summary>
        public void Rebuild()
        {
            if (suppressRebuild) return;

            suppressRebuild = true;
            try
            {
                // Snapshot rather than enumerating live. A player inventory can be mutated
                // from another thread mid-enumeration, and "Collection was modified" thrown
                // here would take out whatever called us.
                ItemSlot[] clothing;
                try
                {
                    clothing = new ItemSlot[characterInventory.Count];
                    for (int i = 0; i < characterInventory.Count; i++) clothing[i] = characterInventory[i];
                }
                catch (Exception e)
                {
                    api.Logger.Warning("[clotheshavepockets] could not read the character inventory, keeping the previous pockets: {0}", e);
                    return;
                }

                // Which pocketed garments are worn RIGHT NOW, so removals can be spotted
                // against what was worn last time.
                var currentGarments = new Dictionary<int, ItemStack>();
                for (int i = 0; i < clothing.Length; i++)
                {
                    ItemStack worn = clothing[i]?.Itemstack;
                    if (worn == null || PocketDefinition.SlotCountFor(worn) <= 0) continue;

                    if (i >= InventoryPockets.MaxCharacterSlots)
                    {
                        // NAMED, NOT SILENT. A pocket that cannot be addressed is a pocket
                        // the player never gets; saying which garment it was is the only way
                        // anyone could raise it as a bug.
                        api.Logger.Warning(
                            "[clotheshavepockets] {0} is worn in character slot {1}, past the {2} this mod can address - "
                            + "it gets no pockets. Raise MaxCharacterSlots.",
                            worn.GetName(), i, InventoryPockets.MaxCharacterSlots);
                        continue;
                    }

                    currentGarments[i] = worn;
                }

                // SERVER ONLY. Removal detection needs to identify a garment across a
                // change, and the client cannot - every stack it holds is replaced on each
                // sync. The client's slots arrive from the server already correct.
                if (api.Side == EnumAppSide.Server) HandleRemovedGarments(currentGarments);

                var mapping = new int[InventoryPockets.TotalSlots];
                for (int i = 0; i < mapping.Length; i++) mapping[i] = -1;

                foreach (var pair in currentGarments)
                {
                    int characterSlotId = pair.Key;
                    int declared = PocketDefinition.SlotCountFor(pair.Value);
                    int pockets = Math.Min(declared, InventoryPockets.MaxPocketsPerGarment);

                    if (declared > pockets)
                    {
                        api.Logger.Warning(
                            "[clotheshavepockets] {0} declares {1} pockets, more than the {2} this mod can address - "
                            + "giving it {2}. Raise MaxPocketsPerGarment.",
                            pair.Value.GetName(), declared, InventoryPockets.MaxPocketsPerGarment);
                    }

                    for (int index = 0; index < pockets; index++)
                    {
                        int slotId = InventoryPockets.SlotIdFor(characterSlotId, index);
                        if (slotId >= 0) mapping[slotId] = characterSlotId;
                    }
                }

                inventory.SetLiveMapping(mapping);

                lastSeenGarments.Clear();
                foreach (var pair in currentGarments) lastSeenGarments[pair.Key] = pair.Value;

                // See isFirstRebuild for why this cannot run at attach time.
                if (api.Side == EnumAppSide.Server && !isFirstRebuild) SweepOrphanedSlots();

                int filled = FilledCount();
                int live = inventory.LiveSlotIds().Length;

                if (filled != lastFilledCount)
                {
                    api.Logger.VerboseDebug(
                        "[clotheshavepockets] {0} rebuilt {1}'s pockets: {2} live slot(s), {3} -> {4} filled",
                        api.Side, player.PlayerName, live, lastFilledCount, filled);
                }

                lastFilledCount = filled;
            }
            finally
            {
                // finally, not a trailing assignment: an exception escaping the rebuild
                // would otherwise leave the guard stuck on and silently kill every future
                // rebuild for the rest of the session.
                suppressRebuild = false;
            }
        }

        /// <summary>
        /// Hand back anything sitting in a slot that no worn garment owns.
        ///
        /// The normal removal path already empties a garment's pockets as it comes off, so
        /// in ordinary play this finds nothing. It is here for the cases that path cannot
        /// see: a garment removed while the player was offline, a clothing mod uninstalled,
        /// or any future drift between the mapping and the contents. Without it those items
        /// would sit at an address nothing maps to - saved, synced, and permanently
        /// unreachable.
        ///
        /// NEVER ON THE FIRST REBUILD. See isFirstRebuild.
        /// </summary>
        private void SweepOrphanedSlots()
        {
            int swept = 0;

            for (int i = 0; i < inventory.Count; i++)
            {
                if (inventory.GarmentSlotIdFor(i) >= 0) continue;

                ItemSlot slot = inventory[i];
                if (slot == null || slot.Empty) continue;

                ItemStack stack = slot.Itemstack;

                // Clear FIRST, hand over second. A throw in between can then only lose one
                // item; the other order could duplicate it, and a duplicate is permanent
                // and spreads.
                slot.Itemstack = null;
                slot.MarkDirty();

                GiveOrDrop(stack);
                swept++;
            }

            if (swept > 0)
            {
                api.Logger.Notification(
                    "[clotheshavepockets] handed {0} orphaned item(s) back to {1} - they were in pockets no worn garment owns",
                    swept, player.PlayerName);
            }
        }

        // ------------------------------------------------------------------ removal

        private void HandleRemovedGarments(Dictionary<int, ItemStack> currentGarments)
        {
            foreach (var previous in lastSeenGarments)
            {
                ItemStack goneGarment = previous.Value;
                if (goneGarment == null) continue;

                // Still worn ANYWHERE? Then nothing was taken off.
                //
                // Checking only the slot it used to be in would be wrong: a garment moved
                // from one clothing slot to another is still being worn, and emptying its
                // pockets for that would look like the mod randomly spitting items out.
                //
                // REFERENCE IDENTITY, AND ONLY REFERENCE IDENTITY, BECAUSE THIS IS
                // SERVER-ONLY CODE. On the server the engine moves the same ItemStack
                // instance from slot to slot and MUTATES IT IN PLACE, so:
                //
                //   * a condition tick, or any other mod writing to the garment, keeps the
                //     same instance and correctly reads as "still worn"
                //   * a genuine swap really is a different instance and correctly reads as
                //     "removed" - even for two garments of the same type in the same
                //     condition, which no comparison of contents could tell apart
                //
                // 1.1.2 added `|| worn.Equals(api.World, goneGarment)` as a fallback to fix
                // the client, where a sync replaces every instance. That fallback is REMOVED
                // and must not come back: ItemStack.Equals compares the whole attribute tree,
                // so a garment whose condition ticked failed it too. Reported from a real
                // server 2026-08-17, running Clothing Visually Degrades - the pockets went
                // invisible every minute or so, which is this check firing on a garment the
                // player was still wearing.
                bool stillWorn = false;
                foreach (ItemStack worn in currentGarments.Values)
                {
                    if (ReferenceEquals(worn, goneGarment)) { stillWorn = true; break; }
                }

                if (stillWorn) continue;

                api.Logger.VerboseDebug(
                    "[clotheshavepockets] {0} sees {1}'s {2} as no longer worn in character slot {3}",
                    api.Side, player.PlayerName, goneGarment.GetName(), previous.Key);

                HandOutPocketsOf(previous.Key, goneGarment);
            }
        }

        /// <summary>
        /// Hand the player everything in the pocket slots belonging to a garment that has
        /// just come off, and empty those slots.
        ///
        /// The slots are found by ADDRESS now - a garment worn in character slot 3 always
        /// owns pocket slots 15 to 19 - rather than by scanning the inventory for a matching
        /// owner. The mapping has not been recomputed yet when this runs, and with fixed
        /// addresses it does not need to have been.
        /// </summary>
        private void HandOutPocketsOf(int garmentSlotId, ItemStack garment)
        {
            int handedBack = 0;

            for (int index = 0; index < InventoryPockets.MaxPocketsPerGarment; index++)
            {
                int slotId = InventoryPockets.SlotIdFor(garmentSlotId, index);
                if (slotId < 0) continue;

                ItemSlot slot = inventory[slotId];
                if (slot == null || slot.Empty) continue;

                ItemStack stack = slot.Itemstack;

                // Clear FIRST, hand over second. A throw in between can then only lose one
                // item; the other order could duplicate it, and a duplicate is permanent
                // and spreads.
                slot.Itemstack = null;
                slot.MarkDirty();

                GiveOrDrop(stack);
                handedBack++;
            }

            // SAID EVEN WHEN IT IS ZERO. Silence is what made drop-on-death look broken for
            // an hour on 2026-09-07: a line that only appears sometimes cannot be told apart
            // from a handler that never ran at all.
            api.Logger.VerboseDebug(
                "[clotheshavepockets] handed {0} item(s) back to {1} as {2} came off",
                handedBack, player.PlayerName, garment?.GetName());
        }

        private void GiveOrDrop(ItemStack stack)
        {
            if (stack == null || stack.StackSize <= 0) return;

            if (config.TryPlayerInventoryBeforeDropping)
            {
                // TryGiveItemstack returns false when nothing fitted, and reduces the stack
                // in place when only part of it did - so whatever is left after this is
                // exactly what still needs dropping.
                if (player.InventoryManager.TryGiveItemstack(stack, true) && stack.StackSize <= 0) return;
            }

            if (stack.StackSize <= 0) return;

            api.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
        }

        // ------------------------------------------------------------------ migration

        /// <summary>Where 1.1.3 and earlier kept the contents.</summary>
        private const string LegacyContentsKey = "clotheshavepocketscontents";

        /// <summary>
        /// Move a 1.1.3 player's pocket contents out of WatchedAttributes and into the
        /// inventory, once.
        ///
        /// THIS IS THE MOST DANGEROUS CODE IN THE MOD. For the length of this method the
        /// contents exist in two places, and two copies of the contents is the shape of
        /// every item duplication this mod has ever produced. The rules it follows are not
        /// stylistic:
        ///
        ///   * SERVER ONLY. The client never migrates anything.
        ///   * ONLY IF THE INVENTORY IS EMPTY. A non-empty inventory has already been
        ///     migrated, or belongs to someone who has been playing on the new version;
        ///     running again would overwrite what they have.
        ///   * WRITE, THEN CLEAR, THEN MarkPathDirty, in that order. A clear that does not
        ///     land means the next login migrates the same items a second time.
        ///   * The old keys are "(garment slot, pocket index)" and the new addresses are
        ///     `garmentSlot * MaxPocketsPerGarment + pocketIndex`. They line up exactly,
        ///     which is a large part of why the addresses are laid out that way.
        ///
        /// Keep this for at least two releases, then delete it deliberately.
        /// </summary>
        private void MigrateFromWatchedAttributes()
        {
            var attributes = player?.Entity?.WatchedAttributes;
            if (attributes == null) return;

            ITreeAttribute legacy = attributes.GetTreeAttribute(LegacyContentsKey);
            if (legacy == null) return;

            if (FilledCount() > 0)
            {
                api.Logger.Warning(
                    "[clotheshavepockets] {0} has BOTH a pre-1.2.0 saved tree and items already in the new inventory. "
                    + "Leaving both alone - nothing is lost, but this should not happen and is worth reporting.",
                    player.PlayerName);
                return;
            }

            int moved = 0;
            int unresolved = 0;
            int unaddressable = 0;

            // Snapshotted before the loop: the tree is not modified here, but reading the
            // keys out first keeps the iteration independent of it either way.
            var legacyKeys = new List<string>();
            foreach (var pair in legacy) legacyKeys.Add(pair.Key);

            foreach (string key in legacyKeys)
            {
                if (!TryParseLegacyKey(key, out int garmentSlotId, out int pocketIndex))
                {
                    unaddressable++;
                    continue;
                }

                int slotId = InventoryPockets.SlotIdFor(garmentSlotId, pocketIndex);
                if (slotId < 0)
                {
                    api.Logger.Warning(
                        "[clotheshavepockets] {0} had a pocket at garment slot {1}, pocket {2}, which this version "
                        + "cannot address. That item is NOT migrated.",
                        player.PlayerName, garmentSlotId, pocketIndex);
                    unaddressable++;
                    continue;
                }

                ItemStack stack = legacy.GetItemstack(key);
                if (stack == null) continue;

                // An unresolved stack reads as a null Collectible everywhere downstream and
                // is then silently skipped. Better to say so than to write a slot that
                // cannot be clicked.
                if (!stack.ResolveBlockOrItem(api.World))
                {
                    unresolved++;
                    continue;
                }

                ItemSlot slot = inventory[slotId];
                if (slot == null) continue;

                slot.Itemstack = stack;
                slot.MarkDirty();
                moved++;
            }

            // Cleared only after every write above has happened.
            attributes.RemoveAttribute(LegacyContentsKey);
            attributes.MarkPathDirty(LegacyContentsKey);

            api.Logger.Notification(
                "[clotheshavepockets] migrated {0} pocket item(s) for {1} out of the pre-1.2.0 store"
                + "{2}{3}. The old store has been cleared.",
                moved, player.PlayerName,
                unresolved > 0 ? ", " + unresolved + " could not be resolved and were dropped" : "",
                unaddressable > 0 ? ", " + unaddressable + " could not be addressed" : "");
        }

        /// <summary>Reads the old "g&lt;garment&gt;s&lt;index&gt;" key format.</summary>
        private static bool TryParseLegacyKey(string key, out int garmentSlotId, out int pocketIndex)
        {
            garmentSlotId = -1;
            pocketIndex = -1;

            if (string.IsNullOrEmpty(key) || key[0] != 'g') return false;

            int s = key.IndexOf('s', 1);
            if (s < 2) return false;

            return int.TryParse(key.Substring(1, s - 1), out garmentSlotId)
                && int.TryParse(key.Substring(s + 1), out pocketIndex);
        }

        // ------------------------------------------------------------------ housekeeping

        /// <summary>
        /// One line per player per side saying what we found when we attached.
        ///
        /// A Notification rather than VerboseDebug, and unconditional, because this is the
        /// line that would diagnose a repeat of the 2026-08-16 loss report from a player's
        /// log alone. Nothing else in the mod records the state at attach time.
        /// </summary>
        private void LogAttachState()
        {
            int wornWithPockets = 0;
            try
            {
                for (int i = 0; i < characterInventory.Count; i++)
                {
                    ItemStack worn = characterInventory[i]?.Itemstack;
                    if (worn != null && PocketDefinition.SlotCountFor(worn) > 0) wornWithPockets++;
                }
            }
            catch (Exception)
            {
                wornWithPockets = -1;
            }

            api.Logger.Notification(
                "[clotheshavepockets] {0} attached {1}: {2} pocketed garment(s) worn, {3} live pocket slot(s), {4} filled.",
                api.Side, player.PlayerName, wornWithPockets, inventory.LiveSlotIds().Length, lastFilledCount);
        }

        /// <summary>
        /// Drop both subscriptions. MUST be called from every teardown path - player
        /// leaving, client disconnecting, mod disposing.
        ///
        /// An event subscription that is never removed keeps this object, its inventory and
        /// the player alive for the rest of the session. Unlike RegisterEventBusListener
        /// these CAN be unsubscribed, so there is no excuse for leaking them.
        /// </summary>
        public void Dispose()
        {
            characterInventory.SlotModified -= OnClothingChanged;
            inventory.SlotModified -= OnPocketChanged;

            // Matches the OpenInventory done when this player was attached. Left open, the
            // manager keeps a reference to an inventory belonging to a player who has gone.
            try
            {
                player.InventoryManager?.CloseInventory(inventory);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[clotheshavepockets] could not close {0}'s pocket inventory: {1}", player.PlayerName, e);
            }
        }

        // THE ORDER DIAGNOSTIC LIVED HERE and was removed once it had answered its
        // question, 2026-08-09. What it established, so nobody has to add it again:
        //
        //   pockets are built in ASCENDING CHARACTER-INVENTORY SLOT INDEX, and in 1.22.6
        //   lowerbody is slot 3 while upperbodyover is slot 11
        //
        // So trousers appear above the coat, and that is vanilla's numbering rather than
        // anything this mod chooses. That ordering is now baked into the ADDRESSES rather
        // than merely into the display order, which is why the panel still shows trousers
        // first.
    }
}
