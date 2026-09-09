using Vintagestory.API.Common;

namespace ClothesHavePockets
{
    /// <summary>
    /// Reads how many pockets a garment has.
    ///
    /// THE WHOLE CONFIGURATION SURFACE OF THIS MOD IS THIS ONE ATTRIBUTE. A wearable
    /// declares it (or has it patched on) as:
    ///
    ///     "attributes": { "clotheshavepockets": { "slots": 2 } }
    ///
    /// and that is it. No central list of item codes, so any clothing mod can be
    /// supported by a patch file without this mod knowing the item exists.
    ///
    /// Patches must use "addmerge" at "/attributes", never "add" - "add" is a straight
    /// assignment (Tavis.JsonPatch's AddReplaceOperation is literally
    /// "jToken[last] = value") and would destroy whatever else was under attributes,
    /// including vanilla's own entries. That mistake shipped on the previous project and
    /// was invisible for a release. "addmerge" creates the key when absent and merges
    /// alongside when present, so one form is correct whatever the target looks like.
    /// </summary>
    public static class PocketDefinition
    {
        /// <summary>The attribute key a garment declares its pockets under.</summary>
        public const string AttributeKey = "clotheshavepockets";

        /// <summary>
        /// How many pockets this stack's garment has. 0 for anything that is not a
        /// pocketed garment, which is almost everything.
        /// </summary>
        public static int SlotCountFor(ItemStack stack)
        {
            return SlotCountFor(stack?.Collectible);
        }

        /// <summary>
        /// The same question asked of the collectible directly, for callers that have no
        /// stack - notably the startup pass that decides which items get the tooltip.
        ///
        /// Safe to ask per VARIANT: `xByType` is resolved during asset loading, so each
        /// variant is its own CollectibleObject carrying its own already-resolved `slots`.
        /// </summary>
        public static int SlotCountFor(CollectibleObject collectible)
        {
            var attributes = collectible?.Attributes;
            if (attributes == null) return 0;

            var pockets = attributes[AttributeKey];
            if (pockets == null || !pockets.Exists) return 0;

            int slots = pockets["slots"].AsInt(0);

            if (slots < 0) return 0;

            // NOT CLAMPED HERE. The only real limit is InventoryPockets.MaxPocketsPerGarment,
            // the stride between one garment's block of pocket addresses and the next, and
            // PocketMirror.Rebuild enforces it - naming the garment in a warning, which is
            // the whole point. A second clamp in this method used to squash the number first,
            // so the warning reported the clamped value instead of what the JSON actually
            // asked for. One cap, enforced in one place, reported honestly.
            return slots;
        }
    }
}
