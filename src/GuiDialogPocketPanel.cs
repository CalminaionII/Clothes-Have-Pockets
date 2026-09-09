using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace ClothesHavePockets
{
    /// <summary>
    /// The pocket slots, as a small panel that appears beside the inventory screen.
    ///
    /// WHY A COMPANION PANEL RATHER THAN SLOTS INSIDE THE VANILLA DIALOG. The survival
    /// inventory dialog is GuiDialogInventory, in Vintagestory.Client.NoObf - part of
    /// VintagestoryLib, which is NOT public source. Adding elements inside it means
    /// Harmony against decompiled client code that can change in any patch. GuiDialog's
    /// OnOpened and OnClosed are public events on the other hand, so this panel can ride
    /// alongside that dialog with no patching at all: it opens when the inventory opens
    /// and closes when it closes.
    ///
    /// The cost is honest and visible - it is a separate window next to the inventory
    /// rather than slots inside its frame. If that reads badly in game the fallback is a
    /// tab on the character screen, which GuiDialogCharacterBase supports as public API.
    /// </summary>
    public class GuiDialogPocketPanel : GuiDialog
    {
        private readonly InventoryPockets inventory;

        /// <summary>
        /// How many pocket slots per row. Two is deliberate: the default garment gives
        /// two pockets, so one garment is one row and the grouping reads without needing
        /// a label per garment.
        /// </summary>
        private const int SlotsPerRow = 2;

        /// <summary>
        /// How many rows are shown before the panel stops growing and starts scrolling.
        /// Six is 12 slots at two per row - tall enough that the common cases never
        /// scroll, short enough that the panel cannot reach the edges of a small screen.
        /// </summary>
        private const int MaxVisibleRows = 6;

        /// <summary>
        /// Scrollbar width. Started at 20, vanilla's usual figure for a full-width dialog
        /// scrollbar, which is too heavy next to a panel only two slots wide - it read as
        /// the widest thing in the window. 14 keeps it grabbable without competing with
        /// the slots.
        /// </summary>
        private const int ScrollbarWidth = 14;

        private const int ScrollbarGap = 3;

        /// <summary>
        /// Gap between the inventory dialog's edge and this panel, in unscaled GUI units.
        /// </summary>
        private const int GapFromParent = 10;

        // No fallback OFFSET constants any more - when there is no parent to sit beside,
        // the panel stands on its own at the right of the screen, the same place vanilla
        // puts a standalone dialog. See ApplyPosition.

        /// <summary>
        /// The vanilla inventory dialog this panel accompanies, so we can sit beside it.
        ///
        /// POSITIONED OFF THE PARENT'S REAL BOUNDS, NOT A HARDCODED OFFSET. The first
        /// attempt centred the panel and pushed it left by a constant, which landed it on
        /// top of the Environment dialog - vanilla's own companion panels are down there.
        /// Vanilla solves this in CharacterExtraDialogs by reading the parent dialog's
        /// rendered bounds and dividing by RuntimeEnv.GUIScale:
        ///
        ///     .WithFixedPosition(leftDlgBounds.renderX / RuntimeEnv.GUIScale, ...)
        ///
        /// Same thing here, placing us to the RIGHT of the inventory: the left side is
        /// where the character and environment panels live, and the right is clear.
        /// </summary>
        public GuiDialog ParentDialog { get; set; }

        /// <summary>
        /// How many LIVE slots this dialog was last composed for.
        ///
        /// LIVE, NOT TOTAL. Since the 2026-09-07 rewrite the inventory is always
        /// InventoryPockets.TotalSlots slots - a constant, so the engine can address sync
        /// packets by index without the count moving underneath it - and clothing decides
        /// only which of them are usable. Composing against the total would draw two
        /// hundred boxes.
        ///
        /// The live count still changes as clothing goes on and off, and a composer built
        /// for four slots does not grow a fifth on its own - it has to be rebuilt. Tracking
        /// what we built for is how we avoid rebuilding it every single frame.
        /// </summary>
        private int composedForLiveSlotCount = -1;

        public GuiDialogPocketPanel(ICoreClientAPI capi, InventoryPockets inventory) : base(capi)
        {
            this.inventory = inventory;

            // SYNCHRONOUS, not polled. The composed grid holds a snapshot of slot ids and
            // re-reads them every frame; if the count changes and we have not recomposed
            // by the next render, vanilla dereferences a slot that is gone. A 1-second
            // poll left roughly a second of that window and crashed the client on the
            // first deploy.
            inventory.LiveSlotsChanged += OnLiveSlotsChanged;
        }

        /// <summary>
        /// Drop the subscription. Called when the world is left - without it the dialog,
        /// its composer and its textures stay reachable from the inventory for the rest of
        /// the session.
        /// </summary>
        public void Detach()
        {
            inventory.LiveSlotsChanged -= OnLiveSlotsChanged;
        }

        private void OnLiveSlotsChanged()
        {
            if (!IsOpened()) return;

            // NEVER CLOSES ITSELF. The panel lives and dies with the inventory dialog and
            // nothing else - his call, 2026-08-09: "I think it should keep the window
            // regardless like the inventory does so it goes when closed."
            //
            // It used to close when the count reached zero, on the reasoning that an empty
            // window announcing "you have no pockets" was noise. That was wrong twice
            // over: a window that vanishes and reappears as you dress is more distracting
            // than a quiet empty one, and it made the panel's presence depend on state the
            // player cannot see, which is why putting clothes back on appeared to do
            // nothing.
            Compose();
        }

        /// <summary>
        /// Null: this panel has no hotkey of its own. It is opened and closed by the
        /// inventory dialog it accompanies, never directly by the player.
        /// </summary>
        public override string ToggleKeyCombinationCode => null;

        /// <summary>
        /// Do not steal focus from the inventory dialog. The player's typing and their
        /// Escape key should keep going to the window they actually opened.
        /// </summary>
        public override bool PrefersUngrabbedMouse => false;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();

            Compose();

            // NO OPEN PACKET HERE ANY MORE. The pocket inventory is opened once, on both
            // sides, when the player is attached - see AttachInventory. Opening it from
            // the dialog is the pattern for a BLOCK ENTITY inventory, which only exists
            // while its window is up; these pockets belong to the player and outlive any
            // window. Doing it from here also did not work: the server logged "no such
            // inventory currently opened?" for every click and refused all of them.
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();

            // Release the grid's textures. The inventory itself stays open - closing this
            // window must not close the pockets.
            SingleComposer?.GetSlotGrid("pocketgrid")?.OnGuiClosed(capi);
        }

        /// <summary>
        /// Backstop for the event above. LiveSlotsChanged covers every route we know of;
        /// this catches anything that changes the live mapping without going through
        /// SetLiveMapping or FromTreeAttributes. Cheap when nothing changed, which is the
        /// normal case.
        /// </summary>
        public void RecomposeIfLiveSlotsChanged()
        {
            if (!IsOpened()) return;
            if (inventory.LiveSlotIds().Length == composedForLiveSlotCount) return;

            capi.Logger.VerboseDebug(
                "[clotheshavepockets] live slot count changed to {0} without raising LiveSlotsChanged " +
                "(panel was composed for {1}) - caught by the polling backstop.",
                inventory.LiveSlotIds().Length, composedForLiveSlotCount);

            OnLiveSlotsChanged();
        }

        private void Compose()
        {
            // THE GRID IS COMPOSED OVER THE LIVE SLOTS ONLY. The inventory itself is a
            // fixed 100 slots so the engine can address updates by index without the count
            // moving under it, but the player must see two boxes in trousers, not a
            // hundred. vanilla's AddItemSlotGrid takes an explicit slot id list for exactly
            // this.
            int[] liveSlotIds = inventory.LiveSlotIds();

            composedForLiveSlotCount = liveSlotIds.Length;

            bool empty = liveSlotIds.Length == 0;

            // With no pockets the window still stands, sized to one empty row so it does
            // not collapse to a title bar and does not jump in size the moment a garment
            // goes on.
            int rows = empty ? 1 : (int)Math.Ceiling(liveSlotIds.Length / (double)SlotsPerRow);

            // LAID OUT THE WAY VANILLA LAYS OUT A SMALL SLOT DIALOG, rather than by
            // guessing at pixels. Taken from GuiDialogCreatureContents in vsessentialsmod,
            // which is the closest vanilla analogue - a compact window that is nothing but
            // a titled grid of slots.
            //
            // The two things that were wrong before, and they were both visible in the
            // screenshot: the grid grew by FixedGrow(0, 10), which padded the BOTTOM only
            // and left an L of dead space down the right and bottom of the inset; and the
            // dialog was an autosized shell with a Fill background, so nothing tied the
            // frame to the content. Vanilla grows the grid symmetrically by the slot
            // padding and then FORKS the frame from the content, so every edge gets the
            // same gap by construction.
            double pad = GuiElementItemSlotGrid.unscaledSlotPadding;
            double dlgPad = GuiStyle.ElementToDialogPadding;

            // NO FixedGrow, AND NO OFFSETS HERE. Both were wrong once an inset was drawn
            // around this, and the screenshot showed exactly why.
            //
            // FixedGrow enlarges the grid's BOUNDS past the slots it contains. Vanilla can
            // do that because it draws nothing around the grid - the dialog background
            // hides the slack. Our inset wraps whatever box it is given, so the slack
            // became visible dead space down the right and bottom.
            //
            // The x/y offsets are pointless too: ForkBoundingParent RESETS a child's
            // fixed position to the parent's padding, so anything set here is discarded.
            // That is what silently ate the title bar clearance - see the dialog fork.
            ElementBounds slotBounds = ElementStdBounds
                .SlotGrid(EnumDialogArea.None, 0, 0, SlotsPerRow, rows);

            // THE PANEL IS CAPPED AND SCROLLS PAST THE CAP.
            //
            // Without this it simply grew downward for ever: rows = ceil(count / 2) with
            // FitToChildren bounds and nothing clipping it. Four pocketed garments at 8
            // slots each is 16 rows, which runs off a 1080p screen at default GUI scale -
            // and because the panel is centred on its parent it overflows BOTH ends, so
            // the title bar leaves the top while the last rows leave the bottom. Nothing
            // warned about it.
            //
            // The visible area is a whole number of rows so the cap never slices a row in
            // half.
            int visibleRows = Math.Min(rows, MaxVisibleRows);
            bool needsScroll = rows > visibleRows;

            double totalHeight = slotBounds.fixedHeight;
            double visibleHeight = ElementStdBounds
                .SlotGrid(EnumDialogArea.None, 0, 0, SlotsPerRow, visibleRows)
                .fixedHeight;

            // What the player can see. The grid itself is the full height and slides
            // underneath this, which is how a clipped scroll region works - the scrollbar
            // moves the CONTENT, not the window.
            ElementBounds clipBounds = ElementBounds.Fixed(0, 0, slotBounds.fixedWidth, visibleHeight);

            // THE SUNKEN BACKING IS WHAT MAKES THIS LOOK LIKE PART OF THE INVENTORY.
            // His note, 2026-08-09: "same backing as the inventory as well". The vanilla
            // inventory's slot area is not drawn straight onto the dialog background - it
            // sits in an inset, which is what gives it that recessed darker panel.
            //
            // Forked from the slots, and then the dialog forked from the inset: each fork
            // makes exactly one parent, so the chain is slots -> inset -> dialog and every
            // level is sized from the one inside it. Forking BOTH from slotBounds would
            // give one child two parents and lay out wrongly.
            // Forked from the CLIP bounds, not the grid: the inset must be the size of the
            // window onto the slots, not the size of all of them. Forking the grid would
            // draw a recess as tall as every row and defeat the cap.
            ElementBounds insetBounds = clipBounds.ForkBoundingParent(pad, pad, pad, pad);

            // THE +30 ON TOP IS THE TITLE BAR, and it has to be applied HERE, at the
            // outermost fork. Vanilla does the same thing in GuiDialogCreatureContents:
            //
            //     slotBounds.ForkBoundingParent(elemToDlgPad, elemToDlgPad + 30, elemToDlgPad, elemToDlgPad)
            //
            // Reserving the space on an inner bounds does not work, because each fork
            // overwrites its child's fixed position with the padding it was given. The
            // first attempt put the clearance on the slot grid, the two forks above it
            // threw it away, and the title bar ended up sitting on the contents.
            // The scrollbar lives to the right of the inset, so the dialog's right padding
            // has to grow by its width or the frame would clip it off. Widened only when a
            // scrollbar is actually present, so a panel that fits looks exactly as it did
            // before this change.
            double rightPad = dlgPad + (needsScroll ? ScrollbarWidth + ScrollbarGap : 0);

            ElementBounds dialogBounds = insetBounds
                .ForkBoundingParent(dlgPad, dlgPad + 30, rightPad, dlgPad)
                .WithAlignment(EnumDialogArea.None);

            ApplyPosition(dialogBounds);

            // A SIBLING of the inset, so it shares the dialog as its parent and is
            // positioned in the same coordinate space.
            ElementBounds scrollbarBounds = insetBounds
                .CopyOffsetedSibling(insetBounds.fixedWidth + ScrollbarGap, 0)
                .WithFixedWidth(ScrollbarWidth);

            GuiComposer composer = capi.Gui
                .CreateCompo("clotheshavepocketspanel", dialogBounds)
                .AddShadedDialogBG(ElementBounds.Fill, true)
                .AddDialogTitleBar(Lang.Get("clotheshavepockets:panel-title"), OnTitleBarClose)
                .AddInset(insetBounds, 3);

            // With no pockets the panel is just an empty inset - no grid and no text.
            // His call, 2026-08-09: "I dont think we need any words in the box either when
            // not wearing clothes without pocket, should be fine to be blank." An empty
            // recess reads as "nothing here" on its own, and a sentence of explanation in
            // a window this small is louder than the thing it is explaining.
            //
            // Still no grid rather than a grid over an empty inventory: vanilla's grid
            // walks the inventory it is given, and handing it nothing to walk is one more
            // edge case than simply not adding it.
            if (!empty)
            {
                // BeginClip/EndClip is what makes the cap real: the grid keeps its full
                // height and anything outside the clip is simply not drawn.
                composer
                    .BeginClip(clipBounds)
                        .AddItemSlotGrid(inventory, SendInvPacket, SlotsPerRow, liveSlotIds, slotBounds, "pocketgrid")
                    .EndClip();

                if (needsScroll)
                {
                    composer.AddVerticalScrollbar(OnScrolled, scrollbarBounds, "pocketscroll");
                }
            }

            SingleComposer = composer.Compose();

            // SetHeights has to come AFTER Compose - the scrollbar does not exist until
            // then. It takes the VISIBLE height and the TOTAL height, in that order;
            // swapping them gives a handle that is the wrong size and scrolls the wrong
            // distance, which looks like a rendering bug rather than a wrong argument.
            if (!empty && needsScroll)
            {
                SingleComposer.GetScrollbar("pocketscroll")
                    ?.SetHeights((float)visibleHeight, (float)totalHeight);
            }
        }

        /// <summary>
        /// Slide the slot grid under the clip window.
        ///
        /// The scrollbar moves the CONTENT, not the window - the clip bounds stay put and
        /// the grid's own Y goes negative as the player scrolls down. CalcWorldBounds has
        /// to be called by hand afterwards, or the change is invisible until something
        /// else forces a relayout.
        /// </summary>
        private void OnScrolled(float value)
        {
            ElementBounds bounds = SingleComposer?.GetSlotGrid("pocketgrid")?.Bounds;
            if (bounds == null) return;

            bounds.fixedY = -value;
            bounds.CalcWorldBounds();
        }

        /// <summary>
        /// Put the dialog immediately to the right of the inventory dialog, top edges
        /// aligned.
        ///
        /// Falls back to a fixed offset from screen centre if the parent's bounds cannot
        /// be read - which happens if the parent has not composed yet. A panel in a
        /// slightly wrong place is a far better failure than no panel, so this never
        /// throws and never refuses to compose.
        /// </summary>
        private void ApplyPosition(ElementBounds dialogBounds)
        {
            // No parent - either the panel is unbound from the inventory and standing on
            // its own, or the inventory dialog has not composed yet. Either way, put it
            // where vanilla puts a standalone dialog rather than somewhere arbitrary.
            if (!TryReadParentBounds(out double parentRight, out double parentTop))
            {
                dialogBounds
                    .WithAlignment(EnumDialogArea.RightMiddle)
                    .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);
                return;
            }

            // renderX/renderY are real pixels; fixed positions are unscaled GUI units, so
            // both have to come back through GUIScale. Getting this wrong puts the panel
            // progressively further out the further the player's GUI scale is from 1.0,
            // which is invisible to whoever wrote it and obvious to everyone else.
            double scale = RuntimeEnv.GUIScale;

            dialogBounds
                .WithAlignment(EnumDialogArea.None)
                .WithFixedPosition(parentRight / scale + GapFromParent, parentTop / scale);
        }

        /// <summary>
        /// The bounding box of the WHOLE parent dialog, as the union of every composer it
        /// owns.
        ///
        /// READING SingleComposer WAS THE BUG. The first version of this took
        /// ParentDialog.SingleComposer.Bounds, which put the panel low and to the right,
        /// down over the hotbar. GuiDialogInventory is not one composer - a GuiDialog owns
        /// a DlgComposers collection, and "single" is only the one that happens to be
        /// named that. On the inventory dialog it is evidently not the main frame.
        ///
        /// Taking the union of them all is right whatever the dialog is built from, which
        /// matters here because the dialog is non-public and we cannot simply go and read
        /// how it lays itself out.
        ///
        /// Hands back the two numbers the caller actually needs, in RENDER pixels: the
        /// right edge and the top edge of the parent as a whole. Returning a synthetic
        /// ElementBounds instead would not work - the caller reads renderX off it, and a
        /// bounds built with ElementBounds.Fixed has no render position until it is
        /// composed into a hierarchy.
        ///
        /// False if nothing usable was found, so the caller can fall back.
        /// </summary>
        private bool TryReadParentBounds(out double right, out double top)
        {
            right = 0;
            top = 0;

            if (ParentDialog?.Composers == null) return false;

            double minY = double.MaxValue;
            double maxX = double.MinValue;
            bool any = false;

            foreach (GuiComposer composer in ParentDialog.Composers.Values)
            {
                ElementBounds b = composer?.Bounds;
                if (b == null || b.OuterWidth <= 0 || b.OuterHeight <= 0) continue;

                minY = Math.Min(minY, b.renderY);
                maxX = Math.Max(maxX, b.renderX + b.OuterWidth);
                any = true;
            }

            if (!any) return false;

            right = maxX;
            top = minY;
            return true;
        }

        /// <summary>
        /// Every slot interaction the grid produces has to reach the server, or the click
        /// only happens on the client and the two sides drift apart.
        /// </summary>
        private void SendInvPacket(object packet)
        {
            capi.Network.SendPacketClient(packet);
        }

        private void OnTitleBarClose()
        {
            TryClose();
        }
    }
}
