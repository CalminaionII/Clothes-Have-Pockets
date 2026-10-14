using ProtoBuf;

namespace ClothesHavePockets
{
    /// <summary>
    /// The client telling the server how it wants its own pockets handled. Sent once when
    /// the player is in the world and again whenever the client's config reloads.
    ///
    /// This exists because a client-owned setting lives in the CLIENT's config file and the
    /// server never reads that file - so anything the server has to act on for a particular
    /// player has to be told to it. One packet, one direction, no reply.
    /// </summary>
    [ProtoContract]
    public class PocketPreferencesPacket
    {
        /// <summary>
        /// When a garment comes off, try the player's inventory before dropping the
        /// contents. Mirrors PocketConfig.TryPlayerInventoryBeforeDropping.
        ///
        /// IsRequired, AND NO FIELD INITIALISER. protobuf-net leaves a value off the wire
        /// when it equals the type's default - false, for a bool - and the receiver then
        /// keeps whatever its constructor set. With `= true` here, "false" arrived as
        /// "true" every single time (2026-09-12: the log said the client sent False and the
        /// server logged handed-back). Required means it is always written.
        /// </summary>
        [ProtoMember(1, IsRequired = true)]
        public bool HandBackToInventory;
    }
}
