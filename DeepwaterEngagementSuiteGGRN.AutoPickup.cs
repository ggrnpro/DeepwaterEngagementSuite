using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Cache;
using ExileCore.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Color = SharpDX.Color;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuiteGGRN;

public partial class DeepwaterEngagementSuiteGGRN
{
    private const string PortalMetadataPattern = @"^Metadata/(MiscellaneousObjects|Effects/Microtransactions)/.*Portal";

    private SyncTask<bool> _pickupTask;
    private readonly Stopwatch _sinceLastPickupClick = Stopwatch.StartNew();
    private readonly Stopwatch _sinceUserClick = Stopwatch.StartNew();
    private readonly Stopwatch _sinceMouseMoved = Stopwatch.StartNew();
    private readonly Dictionary<uint, PickupAttempts> _pickupAttempts = new();
    private readonly Dictionary<uint, PickupItemFacts> _pickupItemFacts = new();
    private CachedValue<LabelOnGround> _portalLabel;
    private Vector2? _lastSeenCursorPosition;
    private bool _borrowingCursor;
    private bool _leftButtonHeldByUs;
    private bool _previousUserLeftDown;
    private bool _previousUserRightDown;
    private DateTime _pickupPausedUntil = DateTime.MinValue;
    private List<Regex> _pickupIgnoreRegexes = [];
    private string _pickupIgnoreSource;

    private AutoPickupSettings AutoPickup => Settings.AutoPickupSettings;

    private sealed class PickupAttempts
    {
        public int Count;
        public DateTime BlockedUntil;
    }

    /// <summary>What we need to know about a ground item, worked out once and then remembered.</summary>
    private sealed record PickupItemFacts(int Width, int Height, string Path, string BaseName, bool IsStackable);

    private void InitAutoPickup()
    {
        RegisterHotkey(AutoPickup.ToggleHotkey);
        RegisterHotkey(AutoPickup.PauseHotkey);
        Input.RegisterKey(Keys.Escape);
        _portalLabel = new TimeCache<LabelOnGround>(FindPortalLabel, 200);
    }

    private void ResetAutoPickup()
    {
        _pickupAttempts.Clear();
        _pickupItemFacts.Clear();

        if (!_borrowingCursor)
        {
            _pickupTask = null;
        }

        ReleaseBorrowedMouseButton();
    }

    /// <summary>
    /// Drives the picker. Called first thing every frame so it keeps working in areas where the rest
    /// of the suite has nothing to say.
    /// </summary>
    private void RunAutoPickup()
    {
        if (AutoPickup.ToggleHotkey.PressedOnce())
        {
            AutoPickup.Enabled.Value = !AutoPickup.Enabled.Value;
            ReleaseBorrowedMouseButton();
        }

        // A borrowed cursor is only ever given back by the task that took it, so once one is in flight
        // it gets pumped to the end no matter what else changed this frame.
        if (!AutoPickup.Enabled && !_borrowingCursor)
        {
            _pickupTask = null;
            return;
        }

        TrackUserPointer();

        // The pause key and Escape double as a panic release: if the player let go of the move button
        // during the frame the cursor was away, this is how they get it back.
        if (AutoPickup.PauseHotkey.IsPressed() || Input.GetKeyState(Keys.Escape))
        {
            _pickupPausedUntil = DateTime.UtcNow.AddSeconds(2);
            ReleaseBorrowedMouseButton();
        }

        if (AutoPickup.DebugHighlight)
        {
            foreach (var candidate in FindPickupCandidates())
            {
                Graphics.DrawFrame(candidate.Rect, Color.Violet, 2);
            }
        }

        if (!_borrowingCursor && !CanAutoPickup())
        {
            _pickupTask = null;
            return;
        }

        TaskUtils.RunOrRestart(ref _pickupTask, AutoPickupIterationAsync);
    }

    /// <summary>
    /// Watches the real pointer so we can tell a still hand from a busy one, and so a click of the
    /// player's own buys them a moment of quiet.
    /// </summary>
    private void TrackUserPointer()
    {
        if (_borrowingCursor)
        {
            return;
        }

        if (NativeCursor.TryGetPosition() is { } position)
        {
            if (_lastSeenCursorPosition is not { } previous || Vector2.DistanceSquared(previous, position) > 1)
            {
                _lastSeenCursorPosition = position;
                _sinceMouseMoved.Restart();
            }
        }

        var leftDown = Input.IsKeyDown(Keys.LButton);
        var rightDown = Input.IsKeyDown(Keys.RButton);

        // Only a fresh press counts. Holding the left button is how the character moves, and treating
        // that as "the player is busy" would mean never picking anything up.
        if (leftDown && !_previousUserLeftDown || rightDown && !_previousUserRightDown)
        {
            _sinceUserClick.Restart();
        }

        // Once the button comes back up the state is the player's own again, whoever pressed it last.
        if (!leftDown)
        {
            _leftButtonHeldByUs = false;
        }

        _previousUserLeftDown = leftDown;
        _previousUserRightDown = rightDown;
    }

    private bool CanAutoPickup()
    {
        if (DateTime.UtcNow < _pickupPausedUntil) return false;
        if (GameController.IsLoading) return false;
        if (!GameController.Window.IsForeground()) return false;
        if (Input.GetKeyState(Keys.Escape)) return false;
        if (_sinceUserClick.ElapsedMilliseconds < AutoPickup.CursorHandling.QuietAfterOwnClickMs.Value) return false;

        var player = GameController.Player;
        if (player == null || !player.IsValid) return false;
        if (player.GetComponent<Life>() is { CurHP: <= 0 }) return false;

        var area = GameController.Area?.CurrentArea;
        if (AutoPickup.Safety.DisableInTown && area is { } current && (current.IsTown || current.IsHideout)) return false;

        var ingameUi = GameController.IngameState.IngameUi;
        if (ingameUi == null) return false;
        if (ingameUi.FullscreenPanels.Any(x => x.IsVisible)) return false;
        if (ingameUi.LargePanels.Any(x => x.IsVisible)) return false;
        if (ingameUi.ChatTitlePanel.IsVisible) return false;

        if (AutoPickup.Safety.DisableWithPanelsOpen &&
            (ingameUi.OpenLeftPanel.IsVisible || ingameUi.OpenRightPanel.IsVisible))
        {
            return false;
        }

        if (!AutoPickup.CursorHandling.UseWindowMessages)
        {
            if (AutoPickup.CursorHandling.OnlyWhileMouseIsStill &&
                _sinceMouseMoved.ElapsedMilliseconds < AutoPickup.CursorHandling.MouseStillForMs.Value)
            {
                return false;
            }

            if (!AutoPickup.CursorHandling.RestoreHeldMouseButton && Input.IsKeyDown(Keys.LButton))
            {
                return false;
            }
        }

        if (AutoPickup.Safety.PauseWhileEnemiesClose && IsMonsterClose()) return false;

        return true;
    }

    private bool IsMonsterClose()
    {
        try
        {
            var range = (float)AutoPickup.Safety.EnemyRange.Value;
            return GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                .Any(x => x is { IsValid: true, IsHostile: true, IsAlive: true, IsHidden: false } &&
                          x.Path?.Contains("ElementalSummoned") != true &&
                          x.DistancePlayer < range);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async SyncTask<bool> AutoPickupIterationAsync()
    {
        if (_sinceLastPickupClick.ElapsedMilliseconds < AutoPickup.ClickIntervalMs.Value)
        {
            return true;
        }

        var target = FindPickupCandidates().FirstOrDefault();
        if (target == null)
        {
            return true;
        }

        var clicked = await ClickGroundLabelAsync(target);
        RecordAttempt(target.EntityId, clicked);
        return true;
    }

    private sealed record PickupCandidate(uint EntityId, Entity Entity, Element Label, RectangleF Rect, float Distance);

    /// <summary>
    /// Every ground label the game is currently drawing, nearest first. The in-game loot filter has
    /// already done the deciding: if it does not draw a label, there is nothing here to take.
    /// </summary>
    private List<PickupCandidate> FindPickupCandidates()
    {
        var candidates = new List<PickupCandidate>();
        var labels = GameController?.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.VisibleGroundItemLabels;
        if (labels == null)
        {
            return candidates;
        }

        var now = DateTime.UtcNow;
        var range = (float)AutoPickup.PickupRange.Value;
        var movingRange = AutoPickup.Safety.SkipDistantItemsWhileMoving && IsPlayerMoving()
            ? (float)AutoPickup.Safety.MovingPickupRange.Value
            : float.MaxValue;

        // Read the bag once. A juiced map puts dozens of labels on screen and every one of them would
        // otherwise walk the whole inventory again.
        var space = AutoPickup.Safety.PickUpWhenInventoryIsFull ? null : ReadInventorySpace();

        foreach (var label in labels)
        {
            var entity = label.Entity;
            if (entity is not { IsValid: true }) continue;

            var distance = entity.DistancePlayer;
            if (distance > range || distance > movingRange) continue;

            if (_pickupAttempts.TryGetValue(entity.Id, out var attempts) &&
                (attempts.Count >= AutoPickup.MaxAttemptsPerItem.Value || now < attempts.BlockedUntil))
            {
                continue;
            }

            if (!IsLabelClickable(label.ClientRect)) continue;
            if (!ShouldPickUp(entity, space)) continue;
            if (AutoPickup.Safety.AvoidPortals && IsPortalNearby(label.ClientRect)) continue;

            candidates.Add(new PickupCandidate(entity.Id, entity, label.Label, label.ClientRect, distance));
        }

        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return candidates;
    }

    private bool IsPlayerMoving()
    {
        return GameController.Player?.GetComponent<Actor>()?.isMoving == true;
    }

    /// <summary>A label half off the screen cannot be clicked, and the edges belong to the game's own HUD.</summary>
    private bool IsLabelClickable(RectangleF rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        var windowRect = GameController.Window.GetWindowRectangleTimeCache with { Location = SharpDX.Vector2.Zero };
        windowRect.Inflate(-36, -36);
        var center = rect.Center;
        return windowRect.Contains(center.X, center.Y);
    }

    private bool ShouldPickUp(Entity groundItem, InventorySpace space)
    {
        var facts = GetItemFacts(groundItem);
        if (facts == null)
        {
            return false;
        }

        if (MatchesIgnorePattern(facts))
        {
            return false;
        }

        return space == null || space.HasRoomFor(groundItem, facts);
    }

    private PickupItemFacts GetItemFacts(Entity groundItem)
    {
        if (_pickupItemFacts.TryGetValue(groundItem.Id, out var cached))
        {
            return cached;
        }

        var item = groundItem.GetComponent<WorldItem>()?.ItemEntity;
        if (item == null)
        {
            // Not an item at all - a portal, a shrine, a chest. Those are somebody else's job.
            _pickupItemFacts[groundItem.Id] = null;
            return null;
        }

        PickupItemFacts facts;
        try
        {
            var baseItem = GameController.Files.BaseItemTypes.Translate(item.Path);
            facts = new PickupItemFacts(
                baseItem?.Width ?? 1,
                baseItem?.Height ?? 1,
                item.Path ?? string.Empty,
                baseItem?.BaseName ?? string.Empty,
                item.HasComponent<Stack>());
        }
        catch (Exception)
        {
            facts = new PickupItemFacts(1, 1, item.Path ?? string.Empty, string.Empty, false);
        }

        _pickupItemFacts[groundItem.Id] = facts;
        return facts;
    }

    private bool MatchesIgnorePattern(PickupItemFacts facts)
    {
        var source = AutoPickup.Safety.IgnorePatterns.Value ?? string.Empty;
        if (source != _pickupIgnoreSource)
        {
            _pickupIgnoreSource = source;
            _pickupIgnoreRegexes = source
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(TryCompile)
                .Where(x => x != null)
                .ToList();
        }

        return _pickupIgnoreRegexes.Any(x => x.IsMatch(facts.Path) ||
                                             !string.IsNullOrEmpty(facts.BaseName) && x.IsMatch(facts.BaseName));

        static Regex TryCompile(string pattern)
        {
            try
            {
                return new Regex(pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    private InventorySpace ReadInventorySpace()
    {
        try
        {
            var inventory = GameController.IngameState.ServerData.PlayerInventories
                .FirstOrDefault(x => x.TypeId == InventoryNameE.MainInventory1)?.Inventory;
            return inventory == null ? null : new InventorySpace(inventory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One frame's view of the bag. Without it the picker spends the rest of the map hammering loot
    /// it has nowhere to put.
    /// </summary>
    private sealed class InventorySpace
    {
        private readonly ServerInventory _inventory;
        private readonly bool[,] _occupied;
        private readonly int _rows;
        private readonly int _columns;

        public InventorySpace(ServerInventory inventory)
        {
            _inventory = inventory;
            _rows = Math.Max(0, inventory.Rows);
            _columns = Math.Max(0, inventory.Columns);
            _occupied = new bool[_rows, _columns];

            foreach (var slot in inventory.InventorySlotItems)
            {
                for (var y = Math.Max(0, slot.PosY); y < Math.Min(_rows, slot.PosY + slot.SizeY); y++)
                for (var x = Math.Max(0, slot.PosX); x < Math.Min(_columns, slot.PosX + slot.SizeX); x++)
                {
                    _occupied[y, x] = true;
                }
            }
        }

        public bool HasRoomFor(Entity groundItem, PickupItemFacts facts)
        {
            if (_rows <= 0 || _columns <= 0)
            {
                return true;
            }

            if (facts.IsStackable && CanStackWith(groundItem))
            {
                return true;
            }

            for (var y = 0; y <= _rows - facts.Height; y++)
            for (var x = 0; x <= _columns - facts.Width; x++)
            {
                var blocked = false;
                for (var dy = 0; dy < facts.Height && !blocked; dy++)
                for (var dx = 0; dx < facts.Width && !blocked; dx++)
                {
                    blocked = _occupied[y + dy, x + dx];
                }

                if (!blocked)
                {
                    return true;
                }
            }

            return false;
        }

        private bool CanStackWith(Entity groundItem)
        {
            var item = groundItem.GetComponent<WorldItem>()?.ItemEntity;
            var stack = item?.GetComponent<Stack>();
            if (stack == null)
            {
                return false;
            }

            return _inventory.InventorySlotItems.Any(slot =>
                slot.Item?.Path == item.Path &&
                slot.Item.GetComponent<Stack>() is { } heldStack &&
                heldStack.Size + stack.Size <= heldStack.Info.MaxStackSize);
        }
    }

    private LabelOnGround FindPortalLabel()
    {
        var labels = GameController?.IngameState?.IngameUi?.ItemsOnGroundLabels;
        if (labels == null)
        {
            return null;
        }

        var regex = new Regex(PortalMetadataPattern);
        return labels.FirstOrDefault(x => x.Label is { IsValid: true, IsVisible: true } &&
                                          x.ItemOnGround?.Metadata is { } metadata &&
                                          regex.IsMatch(metadata));
    }

    private bool IsPortalNearby(RectangleF rect)
    {
        if (_portalLabel?.Value is not { } portal)
        {
            return false;
        }

        var portalRect = portal.Label.GetClientRectCache;
        portalRect.Inflate(100, 100);
        var itemRect = rect;
        itemRect.Inflate(100, 100);
        return portalRect.Intersects(itemRect);
    }

    /// <summary>
    /// Only a click that actually landed counts against an item's budget. A label that never lit up
    /// under the cursor is worth another look in a moment, not a permanent grudge.
    /// </summary>
    private void RecordAttempt(uint entityId, bool clicked)
    {
        if (!_pickupAttempts.TryGetValue(entityId, out var attempts))
        {
            if (_pickupAttempts.Count > 512)
            {
                PruneStaleAttempts();
            }

            attempts = new PickupAttempts();
            _pickupAttempts[entityId] = attempts;
        }

        if (clicked)
        {
            attempts.Count++;
        }

        attempts.BlockedUntil = DateTime.UtcNow.AddSeconds(clicked ? AutoPickup.RetryCooldownSeconds.Value : 0.5);
    }

    private void PruneStaleAttempts()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        foreach (var stale in _pickupAttempts.Where(x => x.Value.BlockedUntil < cutoff).Select(x => x.Key).ToList())
        {
            _pickupAttempts.Remove(stale);
        }
    }

    /// <summary>
    /// The one place that touches input. Either the click is posted to the window and the pointer
    /// never moves, or the cursor is borrowed for a frame or two and put back where it was.
    /// </summary>
    private async SyncTask<bool> ClickGroundLabelAsync(PickupCandidate target)
    {
        _sinceLastPickupClick.Restart();

        // The rect in the candidate list is a frame or two old, and a running character drags every
        // label across the screen. Aim at where the label is now.
        var rect = target.Label is { IsValid: true } ? target.Label.GetClientRect() : target.Rect;
        if (!IsLabelClickable(rect))
        {
            return false;
        }

        var clientCentre = new Vector2(rect.Center.X, rect.Center.Y);

        if (AutoPickup.CursorHandling.UseWindowMessages)
        {
            return await ClickByWindowMessageAsync(target, clientCentre);
        }

        var windowTopLeft = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
        var screenPoint = new Vector2(clientCentre.X + windowTopLeft.X, clientCentre.Y + windowTopLeft.Y);
        var restorePoint = NativeCursor.TryGetPosition() ?? Input.ForceMousePositionNum;
        var handBackTheButton = AutoPickup.CursorHandling.RestoreHeldMouseButton && Input.IsKeyDown(Keys.LButton);
        IStatusDisposable inputBlock = null;
        var clicked = false;

        _borrowingCursor = true;
        try
        {
            if (AutoPickup.CursorHandling.BlockUserInputDuringClick)
            {
                inputBlock = TryBlockUserMouse();
            }

            if (handBackTheButton)
            {
                Input.LeftUp();
            }

            Input.SetCursorPos(screenPoint);
            using var wait = new CancellationTokenSource(AutoPickup.TargetWaitMs.Value);

            // Nothing is clicked until the game says the cursor is on the item. A click that misses
            // lands on open ground, and the character obediently runs there.
            if (await TaskUtils.CheckEveryFrame(() => IsTargeted(target), wait.Token))
            {
                Input.Click(MouseButtons.Left);
                clicked = true;

                // The click is queued, not consumed. Holding the cursor still for one frame is what
                // makes the game read it against the item and not against wherever the hand was.
                await TaskUtils.NextFrame();
            }
        }
        finally
        {
            Input.SetCursorPos(restorePoint);
            _lastSeenCursorPosition = restorePoint;

            if (handBackTheButton)
            {
                Input.LeftDown();
                _leftButtonHeldByUs = true;
            }

            inputBlock?.Dispose();
            _borrowingCursor = false;
        }

        return clicked;
    }

    /// <summary>
    /// The pointer never moves at all: the hover and the click are handed to the window directly.
    /// Whether the client honours posted mouse input is exactly what the targeting check proves.
    /// </summary>
    private async SyncTask<bool> ClickByWindowMessageAsync(PickupCandidate target, Vector2 clientCentre)
    {
        var window = GameController.Window.Process?.MainWindowHandle ?? IntPtr.Zero;
        if (!NativeCursor.PostMouseMove(window, clientCentre))
        {
            return false;
        }

        using var wait = new CancellationTokenSource(AutoPickup.TargetWaitMs.Value);
        if (!await TaskUtils.CheckEveryFrame(() => IsTargeted(target), wait.Token))
        {
            return false;
        }

        return NativeCursor.PostLeftClick(window, clientCentre);
    }

    private IStatusDisposable TryBlockUserMouse()
    {
        try
        {
            return Input.InputManager?.BlockUserMouseInput();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Undoes a move button we pressed on the player's behalf. Only matters if they let go during the
    /// frame the cursor was away, which would otherwise leave the character running on its own.
    /// </summary>
    private void ReleaseBorrowedMouseButton()
    {
        if (!_leftButtonHeldByUs)
        {
            return;
        }

        _leftButtonHeldByUs = false;
        if (Input.IsKeyDown(Keys.LButton))
        {
            Input.LeftUp();
        }
    }

    private static bool IsTargeted(PickupCandidate target)
    {
        if (target.Entity?.GetComponent<Targetable>()?.isTargeted is { } isTargeted)
        {
            return isTargeted;
        }

        return target.Label is { HasShinyHighlight: true };
    }
}
