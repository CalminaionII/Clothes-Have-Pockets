using Vintagestory.API.Common;

namespace ClothesHavePockets
{
    /// <summary>
    /// The mod's settings, saved to ModConfig/ClothesHavePockets.json.
    ///
    /// RENAME A PROPERTY, NEVER RETYPE ONE. Changing an int to a bool (or the reverse)
    /// throws on deserialise, Load() catches the throw and hands back a fresh default
    /// object, and **every other setting the player had is silently wiped**. That cost a
    /// release on a previous project. If the meaning of a setting has to change, add a new
    /// property under a new name and leave the old one alone.
    /// </summary>
    public class PocketConfig
    {
        /// <summary>
        /// Where a pocket's contents go when the garment comes off.
        ///
        /// **true** - try the player's own inventory first, and drop only what does not
        /// fit. Loses far fewer items to someone changing clothes over water or a ravine.
        ///
        /// **false** - always straight to the floor at the player's feet.
        /// </summary>
        public bool TryPlayerInventoryBeforeDropping = true;

        /// <summary>
        /// Whether pocket contents drop when the player dies.
        ///
        /// **true** - they drop at the death spot with everything else, which is the point:
        /// vanilla leaves your WORN clothing on your body when you die, so without this the
        /// pockets are four slots that survive a death the rest of your inventory does not.
        /// Calm's call, 2026-08-18, on seeing it happen on the test server: "it should drop
        /// on death as can be overpowered".
        ///
        /// **false** - contents ride through death with the clothing.
        ///
        /// A SERVER RUNNING "keep your items on death" SHOULD SET THIS FALSE. The mod cannot
        /// read that intent reliably, so it is a setting rather than a guess - on a server
        /// where nothing else drops, pockets alone dropping would be a nasty surprise.
        /// </summary>
        public bool DropPocketsOnDeath = true;

        /// <summary>
        /// Whether the Pockets panel opens and closes together with the inventory screen.
        ///
        /// **true** - it rides the inventory dialog, as it always has. The hotkey still
        /// works as well.
        ///
        /// **false** - the panel is unbound from the inventory entirely and is only ever
        /// opened with its hotkey. His words, 2026-08-09: "a config to unbind the pockets
        /// from the inventory crafting box".
        /// </summary>
        public bool OpenWithInventory = true;

        /// <summary>
        /// Unbind the panel automatically when Immersive Inventory Slots
        /// (`jeansimmersiveinventoryslots`) is installed, as though
        /// <see cref="OpenWithInventory"/> were false.
        ///
        /// That mod exists to make you put your backpack down before you can reach your
        /// items. A panel of always-available slots sitting on that same screen undoes
        /// most of the point of it, so the polite default is to get out of its way and
        /// leave the pockets on their hotkey.
        ///
        /// Set this false to keep the panel attached even with that mod installed. It only
        /// ever REMOVES the attachment - with `OpenWithInventory` already false this
        /// setting does nothing.
        ///
        /// Deliberately keyed on the mod being ENABLED rather than on reading its config.
        /// Parsing another mod's settings file means depending on names they are free to
        /// change without telling anyone, and the failure would be silent.
        /// </summary>
        public bool UnbindFromInventoryWithImmersiveInventorySlots = true;

        // THERE IS NO "KEEP THE CONTENTS INSIDE THE GARMENT" OPTION, removed 2026-08-09.
        //
        // It existed as DropContentsWhenClothingRemoved = false and it did not work: it
        // needed contents to live on the garment's itemstack, and reconciling that with a
        // player-owned inventory is where every item duplication and item loss in this
        // log came from. His call once the drop behaviour was working - "the rest works
        // fine and that isnt necessary".
        //
        // Do not reintroduce it as a flag over the top of the current storage. The two
        // models are genuinely different and the failure mode of mixing them is silent
        // duplication, which is permanent and spreads. If it is ever wanted again it needs
        // its own deliberate design, not a boolean.

        public const string FileName = "ClothesHavePockets.json";

        /// <summary>
        /// Load, falling back to defaults, and always write the file back so a player can
        /// see every setting that exists rather than having to guess the property names.
        /// </summary>
        /// <summary>
        /// Re-read the file and copy the values onto THIS object.
        ///
        /// IN PLACE, AND THAT IS THE WHOLE POINT. Every PocketMirror and every
        /// InventoryPockets was handed the one config instance the mod system loaded at
        /// StartPre and holds a reference to it. Replacing that reference here would leave
        /// all of them reading the old object, and the reload would appear to do nothing -
        /// silently, which is the worst kind of nothing. Copying the fields across means
        /// everyone sees the new values without anything having to be re-wired.
        ///
        /// NO StoreModConfig CALL. Whatever just wrote the file - Integrated Mod Manager, or
        /// the player in a text editor - is the authority; writing it straight back would
        /// race them.
        ///
        /// Returns false if the file could not be read, in which case NOTHING is changed:
        /// the previous settings stay live rather than being replaced by defaults. A
        /// half-applied reload is worse than no reload.
        /// </summary>
        public bool ReloadInPlace(ICoreAPI api)
        {
            PocketConfig fresh;

            try
            {
                fresh = api.LoadModConfig<PocketConfig>(FileName);
            }
            catch (System.Exception e)
            {
                api.Logger.Error("[clotheshavepockets] could not re-read {0}, keeping the settings already loaded: {1}", FileName, e);
                return false;
            }

            if (fresh == null) return false;

            TryPlayerInventoryBeforeDropping = fresh.TryPlayerInventoryBeforeDropping;
            DropPocketsOnDeath = fresh.DropPocketsOnDeath;
            OpenWithInventory = fresh.OpenWithInventory;
            UnbindFromInventoryWithImmersiveInventorySlots = fresh.UnbindFromInventoryWithImmersiveInventorySlots;

            return true;
        }

        public static PocketConfig LoadOrCreate(ICoreAPI api)
        {
            PocketConfig config = null;

            try
            {
                config = api.LoadModConfig<PocketConfig>(FileName);
            }
            catch (System.Exception e)
            {
                // A malformed file must not stop the mod loading. Say so loudly, though -
                // the player has just silently lost their settings and the next save will
                // overwrite the file they were trying to edit.
                api.Logger.Error("[clotheshavepockets] could not read {0}, using defaults: {1}", FileName, e);
            }

            if (config == null) config = new PocketConfig();

            api.StoreModConfig(config, FileName);

            return config;
        }
    }
}
