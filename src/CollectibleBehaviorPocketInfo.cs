using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ClothesHavePockets
{
    /// <summary>
    /// Adds "Pockets: 2" to a garment's tooltip.
    ///
    /// WHY A BEHAVIOUR AT ALL. `CollectibleObject.GetHeldItemInfo` is the only non-Harmony
    /// way into an item's tooltip, and it is virtual on the collectible - which a mod
    /// cannot override for someone else's item. A CollectibleBehavior can, because the
    /// base implementation walks every behaviour and calls this on each.
    ///
    /// WHY IT IS NOT ADDED BY A PATCH. The obvious route is `"op": "add", "path":
    /// "/behaviors/-"` on each clothing file, which is what the old Player Inventory Lib
    /// patches did. Three reasons not to:
    ///
    ///   * it doubles the patch count - every garment would need a behaviour entry as well
    ///     as its pockets attribute, and the two could drift apart
    ///   * `/behaviors/-` requires a `behaviors` array to already exist. Vanilla's
    ///     wearables have one, but a modded garment may not, and the op fails when the
    ///     parent is missing
    ///   * it would not cover clothing from mods we have not patched
    ///
    /// Instead the mod system attaches this to every collectible that declares pockets,
    /// once, at AssetsFinalize. Any garment with the attribute gets the tooltip, including
    /// ones from mods this project has never heard of.
    /// </summary>
    public class CollectibleBehaviorPocketInfo : CollectibleBehavior
    {
        public CollectibleBehaviorPocketInfo(CollectibleObject collObj) : base(collObj)
        {
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

            // Read from the STACK, not from collObj. Same answer today, but it means a
            // future per-stack override - a garment whose pockets were altered by crafting,
            // say - shows the real number rather than the type's default.
            int slots = PocketDefinition.SlotCountFor(inSlot?.Itemstack);
            if (slots <= 0) return;

            dsc.AppendLine(slots == 1
                ? Lang.Get("clotheshavepockets:pocket-count-one")
                : Lang.Get("clotheshavepockets:pocket-count", slots));
        }
    }
}
