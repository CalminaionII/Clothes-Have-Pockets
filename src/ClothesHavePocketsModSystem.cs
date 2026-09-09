using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace ClothesHavePockets
{
    /// <summary>
    /// Wiring. Creates a pocket inventory per player on each side, keeps a mirror running
    /// against their clothing, and hangs the panel off the vanilla inventory dialog.
    ///
    /// NO HARMONY ANYWHERE IN THIS MOD, deliberately - see the csproj comment. Everything
    /// below is public VintagestoryAPI surface.
    /// </summary>
    public class ClothesHavePocketsModSystem : ModSystem
    {
        /// <summary>
        /// Per-player mirrors, keyed by player UID. Instance state, not static: a static
        /// here would carry one world's players into the next, and in singleplayer client
        /// and server would share it. That exact class of bug turned up four times in one
        /// audit on the previous project.
        /// </summary>
        private readonly Dictionary<string, PocketMirror> mirrors = new Dictionary<string, PocketMirror>();

        private ICoreClientAPI capi;
        private GuiDialogPocketPanel panel;
        private InventoryPockets clientInventory;

        /// <summary>The vanilla inventory dialog, found by type name rather than by reference.</summary>
        private GuiDialog vanillaInventoryDialog;

        private long clientTickListenerId;

        private PocketConfig config;

        /// <summary>
        /// Loaded once, before either side starts, so both sides read the same settings.
        /// On a dedicated server only the server's file exists and only it is consulted -
        /// dropping is a server-side action, so that is the copy that decides.
        /// </summary>
        public override void StartPre(ICoreAPI api)
        {
            config = PocketConfig.LoadOrCreate(api);
        }

        /// <summary>
        /// Register the tooltip behaviour's class name.
        ///
        /// REQUIRED EVEN THOUGH NOTHING IN JSON EVER NAMES IT, and leaving it out crashed
        /// every client on connect, 2026-08-09:
        ///
        ///   ArgumentNullException: Value cannot be null. (Parameter 'key')
        ///     at Vintagestory.Common.ItemTypeNet.ReadItemTypePacket
        ///     at ClientSystemStartup.LoadItemTypes
        ///
        /// A collectible's behaviours travel to the client inside the ITEM TYPE PACKET, and
        /// they travel as class NAMES. A behaviour attached in code is still in that list,
        /// so the server looks up its registered name, finds none, and writes null - then
        /// the client does a dictionary lookup on that null and throws before the world
        /// finishes loading.
        ///
        /// So "I create it myself, nothing reads it from JSON, therefore it needs no
        /// registration" is wrong. Registration is what gives the type a name on the wire.
        /// Must happen on both sides: the server needs it to write, the client to read.
        /// </summary>
        public override void Start(ICoreAPI api)
        {
            api.RegisterCollectibleBehaviorClass("clotheshavepocketsinfo", typeof(CollectibleBehaviorPocketInfo));

            // REQUIRED SINCE THE INVENTORY BECAME AN InventoryBasePlayer, 2026-09-07, and
            // leaving it out is a CRASH ON CONNECT rather than a warning:
            //
            //   Don't know how to instantiate inventory of class 'clotheshavepockets'
            //   did you forget to register a mapping?
            //     at Vintagestory.Client.NoObf.ClientPlayer.AddOrUpdateInventory
            //
            // A player inventory is saved with the player and SENT TO THE CLIENT in the
            // player-data packet, which arrives while the connecting screen is still up -
            // long before this mod attaches anything. The client builds it from the class
            // registry, and an unregistered class name throws there.
            //
            // Both sides, for the same reason the behaviour class above needs both.
            RegisterInventoryClass(api);
        }

        /// <summary>
        /// Register the pocket inventory's class name with the engine's class registry.
        ///
        /// THERE IS NO PUBLIC API FOR THIS. `RegisterInventoryClass` is not on `ICoreAPI`
        /// and not on `IClassRegistryAPI`; it exists only on the concrete
        /// `Vintagestory.Common.ClassRegistry` in VintagestoryLib. Checked against the
        /// public repos 2026-09-07 - this is not a case of looking in the wrong place.
        ///
        /// BY REFLECTION, NOT BY A COMPILE-TIME REFERENCE, and deliberately so. A direct
        /// call would bind this mod to a non-public type at load time, and the day its
        /// signature moves the mod stops loading with a TypeLoadException that names
        /// nothing useful. Reflection turns that same day into the warning below, with the
        /// mod still running and the reason on screen.
        ///
        /// It is still a dependency on engine internals, which this mod has otherwise
        /// avoided completely - no Harmony, public surface only. It is here because
        /// `InventoryBasePlayer` leaves no alternative: a player inventory is sent to the
        /// client in the player-data packet, and an unregistered class name throws inside
        /// `ClientPlayer.AddOrUpdateInventory` before any mod code runs. Registering is not
        /// an optimisation, it is the price of the base class.
        /// </summary>
        private static void RegisterInventoryClass(ICoreAPI api)
        {
            try
            {
                object holder = FindInventoryRegistrar(api.ClassRegistry, out MethodInfo method, out string path);

                if (holder == null || method == null)
                {
                    api.Logger.Error(
                        "[clotheshavepockets] could not find RegisterInventoryClass anywhere on {0} or the objects it "
                        + "holds. Clients will CRASH ON CONNECT with \"Don't know how to instantiate inventory of class "
                        + "'clotheshavepockets'\". The engine's class registry has changed shape.",
                        api.ClassRegistry?.GetType().FullName ?? "(null registry)");
                    return;
                }

                method.Invoke(holder, new object[] { InventoryPockets.InventoryClassName, typeof(InventoryPockets) });

                api.Logger.Notification(
                    "[clotheshavepockets] {0}: inventory class registered via {1}.", api.Side, path);
            }
            catch (Exception e)
            {
                api.Logger.Error(
                    "[clotheshavepockets] registering the inventory class failed, clients will crash on connect: {0}", e);
            }
        }

        /// <summary>
        /// Find the object that actually carries `RegisterInventoryClass(string, Type)`.
        ///
        /// SEARCHED FOR RATHER THAN NAMED, and the first attempt is why. `api.ClassRegistry`
        /// looks like the obvious home but is a `ClassRegistryAPI` - a WRAPPER - and the
        /// method lives on the `ClassRegistry` it holds. Worse, `as ClassRegistry` COMPILES
        /// against it and returns null at runtime, so a direct cast fails silently and the
        /// only symptom is every client crashing on connect. 2026-09-07.
        ///
        /// So: look on the registry itself, then one level down through whatever it holds,
        /// and report the path that worked. One level is enough for the shape the engine has
        /// today and stops this from being an unbounded walk of the object graph.
        /// </summary>
        private static object FindInventoryRegistrar(object registry, out MethodInfo method, out string path)
        {
            method = null;
            path = null;
            if (registry == null) return null;

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            method = registry.GetType().GetMethod("RegisterInventoryClass", new[] { typeof(string), typeof(Type) });
            if (method != null)
            {
                path = registry.GetType().Name;
                return registry;
            }

            foreach (FieldInfo field in registry.GetType().GetFields(Flags))
            {
                object held;
                try { held = field.GetValue(registry); } catch (Exception) { continue; }
                if (held == null) continue;

                MethodInfo found = held.GetType().GetMethod("RegisterInventoryClass", new[] { typeof(string), typeof(Type) });
                if (found == null) continue;

                method = found;
                path = registry.GetType().Name + "." + field.Name + " (" + held.GetType().Name + ")";
                return held;
            }

            foreach (PropertyInfo property in registry.GetType().GetProperties(Flags))
            {
                if (property.GetIndexParameters().Length > 0) continue;

                object held;
                try { held = property.GetValue(registry); } catch (Exception) { continue; }
                if (held == null) continue;

                MethodInfo found = held.GetType().GetMethod("RegisterInventoryClass", new[] { typeof(string), typeof(Type) });
                if (found == null) continue;

                method = found;
                path = registry.GetType().Name + "." + property.Name + " (" + held.GetType().Name + ")";
                return held;
            }

            return null;
        }

        /// <summary>
        /// Give every garment that declares pockets a tooltip saying how many.
        ///
        /// AssetsFinalize, because by then every type is resolved and every `xByType` has
        /// been collapsed - so each VARIANT is its own CollectibleObject with its own slot
        /// count, and a file that varies pockets per variant is handled with no extra work.
        /// Asking any earlier would read the unresolved JSON.
        ///
        /// Attached in code rather than patched onto each file: it needs no `behaviors`
        /// array to exist, cannot drift out of step with the pockets attribute, and covers
        /// clothing from mods this project has never seen. See CollectibleBehaviorPocketInfo.
        /// </summary>
        public override void AssetsFinalize(ICoreAPI api)
        {
            int tagged = 0;
            int alreadyPresent = 0;

            foreach (CollectibleObject collectible in api.World.Collectibles)
            {
                if (collectible == null) continue;
                if (PocketDefinition.SlotCountFor(collectible) <= 0) continue;

                // ATTACH ONCE. Registering the class (which the client crash forced) means
                // the behaviour now also travels to the client inside the item type packet
                // - so on the client it can arrive twice: once from the server's list and
                // once from this pass. The tooltip then printed "Pockets: 2" on two lines,
                // because GetHeldItemInfo is called for every behaviour in the list.
                //
                // Guarding here rather than skipping the client pass outright: which route
                // delivers it depends on load order and on whether there is a real server,
                // so "already there" is the only condition that is true in every case.
                if (collectible.GetBehavior<CollectibleBehaviorPocketInfo>() != null)
                {
                    alreadyPresent++;
                    continue;
                }

                var behaviors = collectible.CollectibleBehaviors;
                Array.Resize(ref behaviors, behaviors.Length + 1);
                behaviors[behaviors.Length - 1] = new CollectibleBehaviorPocketInfo(collectible);
                collectible.CollectibleBehaviors = behaviors;

                tagged++;
            }

            api.Logger.Notification(
                "[clotheshavepockets] {0}: {1} item(s) declare pockets, {2} already had the tooltip behaviour.",
                api.Side, tagged, alreadyPresent);
        }

        // ------------------------------------------------------------------ server

        public override void StartServerSide(ICoreServerAPI api)
        {
            mirrors.Clear();

            // PlayerNowPlaying, NOT PlayerJoin. PlayerJoin fires earlier in the sequence, and
            // the mirror's first act is to read the player's worn clothing - so attaching
            // there means reading an inventory that may not have finished loading. That is
            // the race behind the item loss reported 2026-08-16; the load-only first rebuild
            // in PocketMirror is what actually makes it harmless, and this makes it rare.
            //
            // NowPlaying means the player is fully loaded and in the world, which is the
            // earliest point their clothing is certainly there to be read.
            ListenForConfigReloads(api);

            api.Event.PlayerNowPlaying += OnServerPlayerNowPlaying;
            api.Event.PlayerDisconnect += OnServerPlayerLeave;

            // NO PlayerDeath SUBSCRIPTION ANY MORE, 2026-09-07. The pockets are a player
            // inventory now, so the ENGINE drops them on death and it does so before any
            // handler here would run - which is exactly why "drop on death isn't working"
            // was reported: this mod's handler found the slots already empty and said
            // nothing. The setting is honoured in InventoryPockets.DropAll instead, which
            // is the call the engine actually makes.
        }

        private void OnServerPlayerNowPlaying(IServerPlayer player)
        {
            AttachInventory(player, player.Entity.Api);
        }

        private void OnServerPlayerLeave(IServerPlayer player)
        {
            DetachInventory(player.PlayerUID);
        }

        // ------------------------------------------------------------------ client

        /// <summary>
        /// The modid of Immersive Inventory Slots, whose whole purpose is to stop you
        /// reaching your items from the crafting screen. See PocketConfig.
        /// </summary>
        private const string ImmersiveInventorySlotsModId = "jeansimmersiveinventoryslots";

        private const string HotkeyCode = "clotheshavepockets";

        /// <summary>
        /// Whether the panel rides the inventory dialog. False means hotkey only.
        /// </summary>
        private bool attachedToInventory;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            mirrors.Clear();

            // The hotkey is registered in BOTH modes, so the pockets are always reachable
            // even when the panel is attached and the player would normally just open the
            // inventory. O is a default, not a decision - it is rebindable like any other.
            api.Input.RegisterHotKey(
                HotkeyCode,
                Lang.Get("clotheshavepockets:hotkey-pockets"),
                GlKeys.O,
                HotkeyType.GUIOrOtherControls);

            // The handler has to be set explicitly. Returning a ToggleKeyCombinationCode
            // from the dialog does NOT make the engine toggle it - checked against
            // vanilla's WorldMapManager, which registers the hotkey and then wires
            // SetHotKeyHandler to its own toggle. Relying on the code alone would give a
            // hotkey that silently does nothing.
            api.Input.SetHotKeyHandler(HotkeyCode, OnHotkeyPockets);

            // LevelFinalize rather than here: the vanilla dialogs are not in LoadedGuis
            // yet at StartClientSide, and the player's own character inventory does not
            // exist either.
            ListenForConfigReloads(api);

            api.Event.LevelFinalize += OnClientLevelFinalize;
            api.Event.LeaveWorld += OnClientLeaveWorld;
        }

        private void OnClientLevelFinalize()
        {
            IPlayer player = capi.World.Player;

            clientInventory = AttachInventory(player, capi);
            if (clientInventory == null) return;

            panel = new GuiDialogPocketPanel(capi, clientInventory);

            bool immersiveSlotsInstalled = capi.ModLoader.IsModEnabled(ImmersiveInventorySlotsModId);

            attachedToInventory = config.OpenWithInventory
                && !(immersiveSlotsInstalled && config.UnbindFromInventoryWithImmersiveInventorySlots);

            if (immersiveSlotsInstalled)
            {
                capi.Logger.Notification(
                    "[clotheshavepockets] Immersive Inventory Slots is installed, so the pocket panel is {0}. " +
                    "Open it with the Pockets hotkey. Set UnbindFromInventoryWithImmersiveInventorySlots to false to keep it attached.",
                    attachedToInventory ? "still attached to the inventory by config" : "unbound from the inventory");
            }

            if (attachedToInventory)
            {
                AttachToVanillaInventoryDialog();
                panel.ParentDialog = vanillaInventoryDialog;
            }

            // Putting a pocketed garment ON while the inventory is already open has to
            // bring the panel back. Without this the panel only ever opens at the moment
            // the inventory dialog opens, so taking all your clothes off closed it and
            // putting them back on did nothing until you closed and reopened the
            // inventory - reported from the first play test.
            clientInventory.LiveSlotsChanged += OnClientLiveSlotsChanged;

            // The pocket count changes when clothing changes, and the composer has to be
            // rebuilt when it does. Polling once a second is enough - it only matters at
            // human speed, and the check is a single integer comparison when nothing moved.
            clientTickListenerId = capi.Event.RegisterGameTickListener(OnClientTick, 1000);
        }

        /// <summary>
        /// Find the vanilla survival inventory dialog and ride its open/close.
        ///
        /// MATCHED BY TYPE NAME ON PURPOSE. GuiDialogInventory lives in VintagestoryLib
        /// (Vintagestory.Client.NoObf) which is not public source; referencing the type at
        /// compile time would tie this mod to an internal class. Its OnOpened and OnClosed
        /// are public events on GuiDialog, which is all we actually need from it.
        ///
        /// If the name ever changes this fails SOFTLY - the panel simply never appears -
        /// so it is logged rather than thrown, and the log line is the whole diagnosis.
        /// </summary>
        private void AttachToVanillaInventoryDialog()
        {
            vanillaInventoryDialog = capi.Gui.LoadedGuis
                .FirstOrDefault(dlg => dlg.GetType().Name == "GuiDialogInventory");

            if (vanillaInventoryDialog == null)
            {
                capi.Logger.Warning(
                    "[clotheshavepockets] could not find the vanilla inventory dialog (GuiDialogInventory) " +
                    "among {0} loaded GUIs, so the pocket panel will not appear. The type may have been " +
                    "renamed in this game version.",
                    capi.Gui.LoadedGuis.Count);
                return;
            }

            vanillaInventoryDialog.OnOpened += OnVanillaInventoryOpened;
            vanillaInventoryDialog.OnClosed += OnVanillaInventoryClosed;
        }

        /// <summary>
        /// Whether the panel should be showing at all right now.
        ///
        /// NOT IN CREATIVE. His call, 2026-08-09. In creative the player has every item in
        /// the game two keystrokes away, so a window offering four more slots is clutter on
        /// a screen that is already busy.
        ///
        /// Spectator is included on the same reasoning - a spectator is not carrying
        /// anything. If that turns out to be unwanted it is one condition to delete.
        ///
        /// The pockets themselves are untouched: contents stay on the player and come back
        /// the moment the mode changes. This hides the window, it does not empty anything.
        /// </summary>
        private bool PanelAllowedNow()
        {
            EnumGameMode mode = capi?.World?.Player?.WorldData?.CurrentGameMode ?? EnumGameMode.Survival;

            return mode != EnumGameMode.Creative && mode != EnumGameMode.Spectator;
        }

        private void OnVanillaInventoryOpened()
        {
            if (panel == null) return;
            if (!PanelAllowedNow()) return;

            // Opens with the inventory ALWAYS, pockets or not. It used to skip opening
            // when there were no pockets, which made the window's presence depend on state
            // the player cannot see and was the reason putting clothes back on appeared to
            // do nothing. The panel shows "nothing you are wearing has pockets" instead.
            if (!panel.IsOpened()) panel.TryOpen();
        }

        private void OnVanillaInventoryClosed()
        {
            if (panel != null && panel.IsOpened()) panel.TryClose();
        }

        /// <summary>
        /// Open the panel if pockets have just appeared and the inventory is showing.
        /// Closing and recomposing are the panel's own business; only OPENING needs to
        /// know whether the parent dialog is up, which is why it lives here.
        /// </summary>
        private void OnClientLiveSlotsChanged()
        {
            if (panel == null || clientInventory == null) return;
            if (panel.IsOpened()) return;
            if (!PanelAllowedNow()) return;

            // Only auto-opens in attached mode. Unbound, the panel appears when the player
            // asks for it and never on its own - popping up because a garment went on
            // would be exactly the intrusion that unbinding is meant to avoid.
            if (!attachedToInventory) return;

            if (vanillaInventoryDialog != null && vanillaInventoryDialog.IsOpened())
            {
                panel.TryOpen();
            }
        }

        private bool OnHotkeyPockets(KeyCombination combination)
        {
            if (panel == null) return false;

            // Consumed rather than passed on even when refused, so the key does not fall
            // through to something else the moment the player switches to creative.
            if (!PanelAllowedNow() && !panel.IsOpened()) return true;

            panel.Toggle();
            return true;
        }

        private void OnClientTick(float dt)
        {
            // Game mode can change mid-session, by command or by an admin. Nothing raises
            // an event for it, so the panel is closed here rather than being left open in
            // creative until something else happens to shut it.
            if (panel != null && panel.IsOpened() && !PanelAllowedNow())
            {
                panel.TryClose();
                return;
            }

            panel?.RecomposeIfLiveSlotsChanged();

            // Backstop for the open path too, for the same reason the recompose has one.
            OnClientLiveSlotsChanged();
        }

        private void OnClientLeaveWorld()
        {
            if (vanillaInventoryDialog != null)
            {
                vanillaInventoryDialog.OnOpened -= OnVanillaInventoryOpened;
                vanillaInventoryDialog.OnClosed -= OnVanillaInventoryClosed;
                vanillaInventoryDialog = null;
            }

            if (clientTickListenerId != 0)
            {
                capi.Event.UnregisterGameTickListener(clientTickListenerId);
                clientTickListenerId = 0;
            }

            if (clientInventory != null)
            {
                clientInventory.LiveSlotsChanged -= OnClientLiveSlotsChanged;
            }

            if (capi?.World?.Player != null) DetachInventory(capi.World.Player.PlayerUID);

            panel?.Detach();
            panel = null;
            clientInventory = null;
        }

        // ------------------------------------------------------------------ both sides

        /// <summary>
        /// Create this player's pocket inventory and start mirroring their clothing into
        /// it. Runs on both sides with the same InventoryID, which is what lets the engine
        /// route slot clicks between them.
        /// </summary>
        /// <summary>
        /// The event Integrated Mod Manager raises after it has rewritten our config file.
        /// Its own documentation gives the name as `imm.&lt;modid&gt;`.
        /// </summary>
        private const string ImmConfigChangedEvent = "imm.clotheshavepockets";

        /// <summary>
        /// Pick up config changes made in game, without a restart.
        ///
        /// NO DEPENDENCY ON IMM, AND NONE IS NEEDED. `RegisterEventBusListener` is vanilla
        /// API and an event nobody raises simply never fires, so this costs nothing at all
        /// when Integrated Mod Manager is not installed. That is the same bargain the rest
        /// of the mod makes - it reacts to other mods being present, it never requires them.
        ///
        /// Registered on BOTH sides because the settings are split across them: dropping is
        /// decided by the server, the panel by the client, and each side has its own copy of
        /// the file.
        /// </summary>
        private void ListenForConfigReloads(ICoreAPI api)
        {
            // THE SIDE'S API IS CAPTURED HERE, NOT STORED IN A FIELD. In singleplayer both
            // sides share this ModSystem instance, so a field would hold whichever side
            // started last and the other side's reload would log - or write - as the wrong
            // one. That exact class of bug turned up four times in one audit on the previous
            // project, which is why `mirrors` is instance state with a comment saying so.
            api.Event.RegisterEventBusListener(
                delegate (string eventName, ref EnumHandling handling, IAttribute data)
                {
                    ReloadConfig(api);
                },
                0.5, ImmConfigChangedEvent);
        }

        private void ReloadConfig(ICoreAPI api)
        {
            if (api == null || config == null) return;

            // In place - see PocketConfig.ReloadInPlace. Every mirror and every inventory
            // holds a reference to this one object; replacing it here would leave them all
            // reading the old settings and the reload would silently do nothing.
            if (!config.ReloadInPlace(api)) return;

            // WHAT ACTUALLY TAKES EFFECT, said plainly rather than implied. The two dropping
            // settings are read at the moment they are used - a death, a garment coming off -
            // so they are live from now on. The two panel settings are read ONCE, when the
            // panel is wired to the vanilla inventory dialog at LevelFinalize; changing them
            // live would mean subscribing and unsubscribing from that dialog's events at
            // runtime, and double-subscribing it is exactly the class of bug that has taken
            // this panel down before. Not worth it for a setting changed once in a session.
            api.Logger.Notification(
                "[clotheshavepockets] {0}: settings reloaded. Dropping settings are live now; "
                + "OpenWithInventory and UnbindFromInventoryWithImmersiveInventorySlots need a "
                + "world reload to take effect.",
                api.Side);
        }

        private InventoryPockets AttachInventory(IPlayer player, ICoreAPI api)
        {
            if (player == null) return null;

            var characterInventory = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName) as InventoryBase;
            if (characterInventory == null)
            {
                api.Logger.Warning("[clotheshavepockets] {0} has no character inventory, no pockets for them.", player.PlayerName);
                return null;
            }

            // KEEP THE INVENTORY THE ENGINE ALREADY RESTORED. DO NOT REPLACE IT.
            //
            // This one line lost every player's pockets on every login, 2026-09-07, and the
            // reason is worth stating plainly because the old code looked completely
            // reasonable:
            //
            //     var inventory = new InventoryPockets(player.PlayerUID, api, config);
            //     player.InventoryManager.Inventories[inventory.InventoryID] = inventory;
            //
            // A player inventory is SAVED, and the engine rebuilds it from the savegame
            // through the class registry while the player's data loads - before
            // PlayerNowPlaying fires. That restored instance is holding the player's items.
            // Overwriting the dictionary entry with a fresh empty one threw them away, every
            // time, silently. It was harmless right up until the rewrite made this inventory
            // the real store rather than a view of WatchedAttributes.
            //
            // The restored instance comes from the two-argument constructor and so has no
            // config, which is the only thing it is missing. AttachConfig fixes that.
            string inventoryId = InventoryPockets.InventoryClassName + "-" + player.PlayerUID;

            InventoryPockets inventory = null;
            if (player.InventoryManager.Inventories.TryGetValue(inventoryId, out IInventory existing))
            {
                inventory = existing as InventoryPockets;

                if (inventory == null)
                {
                    api.Logger.Warning(
                        "[clotheshavepockets] {0} already has an inventory called {1} that is a {2}, not ours. "
                        + "Replacing it, which may lose whatever was in it.",
                        player.PlayerName, inventoryId, existing.GetType().Name);
                }
            }

            if (inventory == null)
            {
                inventory = new InventoryPockets(player.PlayerUID, api, config);
                inventory.LateInitialize(inventory.InventoryID, api);

                player.InventoryManager.Inventories[inventory.InventoryID] = inventory;

                api.Logger.Notification(
                    "[clotheshavepockets] {0}: no saved pocket inventory for {1}, starting a fresh one.",
                    api.Side, player.PlayerName);
            }
            else
            {
                int carried = 0;
                for (int i = 0; i < inventory.Count; i++)
                {
                    ItemSlot slot = inventory[i];
                    if (slot != null && !slot.Empty) carried++;
                }

                // THE LINE THAT PROVES THE SAVE ROUND-TRIPPED. If this ever says 0 for a
                // player who logged out with full pockets, the engine is not persisting this
                // inventory and no amount of care in this mod will keep their items.
                api.Logger.Notification(
                    "[clotheshavepockets] {0}: reusing the pocket inventory the engine restored for {1}, {2} item(s) in it.",
                    api.Side, player.PlayerName, carried);
            }

            inventory.AttachConfig(config);

            // OPENED HERE, ON BOTH SIDES, FOR THE WHOLE SESSION. This is the fix for the
            // bug that made everything else look broken.
            //
            // Putting the inventory in the Inventories dictionary makes it EXIST; it does
            // not make it OPEN, and the server rejects slot packets for an inventory the
            // player has not opened:
            //
            //   [Warning] Got activate inventory slot packet on inventory
            //   clotheshavepockets-<uid> but no such inventory currently opened?
            //
            // Every click was therefore applied on the client and refused by the server,
            // which is why an item appeared in a pocket and then vanished or hit the floor
            // the moment the garment synced back down. IPlayerInventoryManager.OpenInventory's
            // own doc comment says it plainly: "Should always be called on both sides
            // (client and server)."
            //
            // The dialog used to send an open packet from OnGuiOpened instead. That is the
            // pattern for a BLOCK ENTITY dialog, where the inventory belongs to something
            // in the world and is only reachable while the window is up. These pockets
            // belong to the player and exist whether or not any window is open, so they
            // are opened once when the player is attached and stay open.
            player.InventoryManager.OpenInventory(inventory);

            // Replace rather than accumulate. A player rejoining without a clean disconnect
            // would otherwise leave the previous mirror subscribed to their clothing.
            DetachInventory(player.PlayerUID);
            mirrors[player.PlayerUID] = new PocketMirror(api, player, inventory, characterInventory, config);

            return inventory;
        }

        private void DetachInventory(string playerUid)
        {
            if (playerUid == null) return;

            if (mirrors.TryGetValue(playerUid, out PocketMirror mirror))
            {
                mirror.Dispose();
                mirrors.Remove(playerUid);
            }
        }

        public override void Dispose()
        {
            // Deliberately NOT clearing per-side state here. Dispose is not told which side
            // it is running for, and in singleplayer both sides share this object - a client
            // teardown pulling state out from under a still-running server is a real bug
            // shape, hit on the previous project. The per-side clears live in
            // StartClientSide / StartServerSide and OnClientLeaveWorld instead.
            base.Dispose();
        }
    }
}
