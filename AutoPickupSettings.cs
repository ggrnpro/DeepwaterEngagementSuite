using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;

namespace DeepwaterEngagementSuiteGGRN;

[Submenu(CollapsedByDefault = true)]
public class AutoPickupSettings
{
    [Menu("Pick up loot automatically", "Clicks anything your in-game loot filter still draws a label for.")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(false);

    [Menu("Toggle key", "Turns the whole thing on and off without opening the settings.")]
    public HotkeyNodeV2 ToggleHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    [Menu("Hold to pause", "While this is held, and for a moment afterwards, nothing is picked up.")]
    public HotkeyNodeV2 PauseHotkey { get; set; } = new HotkeyNodeV2(Keys.Space);

    [Menu("Pickup range", "Ground distance, in the same units the game uses for item labels.")]
    public RangeNode<int> PickupRange { get; set; } = new RangeNode<int>(600, 1, 1000);

    [Menu("Milliseconds between clicks", "The floor on how fast loot is taken. Below ~35 the game starts dropping clicks.")]
    public RangeNode<int> ClickIntervalMs { get; set; } = new RangeNode<int>(45, 0, 500);

    [Menu("Milliseconds to wait for the item to light up", "How long a borrowed cursor waits for the game to register the hover before clicking.")]
    public RangeNode<int> TargetWaitMs { get; set; } = new RangeNode<int>(60, 0, 300);

    [Menu("Give up on an item after this many tries")]
    public RangeNode<int> MaxAttemptsPerItem { get; set; } = new RangeNode<int>(3, 1, 10);

    [Menu("Seconds to leave a failed item alone")]
    public RangeNode<int> RetryCooldownSeconds { get; set; } = new RangeNode<int>(4, 1, 60);

    public CursorHandlingSettings CursorHandling { get; set; } = new CursorHandlingSettings();
    public PickupSafetySettings Safety { get; set; } = new PickupSafetySettings();

    [Menu("Outline what would be picked up", "Debug overlay. Draws a frame around every label the picker considers fair game.")]
    public ToggleNode DebugHighlight { get; set; } = new ToggleNode(false);
}

[Submenu(CollapsedByDefault = false)]
public class CursorHandlingSettings
{
    [Menu("Send clicks to the window instead of moving the cursor",
        "Experimental. Posts the click straight to the game window, so the pointer never leaves your hand. " +
        "Some clients ignore posted mouse input entirely - if nothing gets picked up, turn this back off.")]
    public ToggleNode UseWindowMessages { get; set; } = new ToggleNode(false);

    [Menu("Only borrow the cursor while the mouse is still",
        "Waits for you to stop moving the mouse before taking it. Nothing to see, but loot waits for a gap.")]
    [ConditionalDisplay(nameof(UseWindowMessages), false)]
    public ToggleNode OnlyWhileMouseIsStill { get; set; } = new ToggleNode(false);

    [Menu("Milliseconds of stillness required")]
    [ConditionalDisplay(nameof(OnlyWhileMouseIsStill))]
    public RangeNode<int> MouseStillForMs { get; set; } = new RangeNode<int>(120, 20, 1000);

    [Menu("Keep holding the move button through the click",
        "You move by holding the left button, so the click has to release it and press it again. " +
        "Turn this off to simply skip loot while you are holding the button down.")]
    [ConditionalDisplay(nameof(UseWindowMessages), false)]
    public ToggleNode RestoreHeldMouseButton { get; set; } = new ToggleNode(true);

    [Menu("Block your mouse while the cursor is borrowed",
        "Stops your own movement fighting the plugin during the frame or two the cursor is away.")]
    [ConditionalDisplay(nameof(UseWindowMessages), false)]
    public ToggleNode BlockUserInputDuringClick { get; set; } = new ToggleNode(true);

    [Menu("Wait this long after your own click", "Keeps the picker out of the way right after you click something yourself.")]
    public RangeNode<int> QuietAfterOwnClickMs { get; set; } = new RangeNode<int>(150, 0, 2000);
}

[Submenu(CollapsedByDefault = true)]
public class PickupSafetySettings
{
    [Menu("Pick up even when the inventory is full", "Off by default, otherwise the picker spends the map clicking loot it cannot hold.")]
    public ToggleNode PickUpWhenInventoryIsFull { get; set; } = new ToggleNode(false);

    [Menu("Stop while a monster is close")]
    public ToggleNode PauseWhileEnemiesClose { get; set; } = new ToggleNode(false);

    [Menu("Monster range")]
    [ConditionalDisplay(nameof(PauseWhileEnemiesClose))]
    public RangeNode<int> EnemyRange { get; set; } = new RangeNode<int>(600, 50, 2000);

    [Menu("Never click a label that sits on top of a portal", "Cheap insurance against being sent back to town mid-map.")]
    public ToggleNode AvoidPortals { get; set; } = new ToggleNode(true);

    [Menu("Stop in town and hideout")]
    public ToggleNode DisableInTown { get; set; } = new ToggleNode(true);

    [Menu("Stop while any panel is open", "Inventory, stash, vendor and the like.")]
    public ToggleNode DisableWithPanelsOpen { get; set; } = new ToggleNode(true);

    [Menu("Leave items alone while moving", "Skips anything further away than the distance below while your character is running.")]
    public ToggleNode SkipDistantItemsWhileMoving { get; set; } = new ToggleNode(false);

    [Menu("Distance that still counts as close enough while moving")]
    [ConditionalDisplay(nameof(SkipDistantItemsWhileMoving))]
    public RangeNode<int> MovingPickupRange { get; set; } = new RangeNode<int>(50, 0, 1000);

    [Menu("Skip these items", "Comma separated. Each entry is a regular expression matched against the item path and its base name.")]
    public TextNode IgnorePatterns { get; set; } = new TextNode("");
}
