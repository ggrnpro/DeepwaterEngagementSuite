using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace DeepwaterEngagementSuiteGGRN;

[Submenu(CollapsedByDefault = true)]
public class DelveSettings
{
    [Menu("Show what is worth taking in the mine", "Ranks the objects around you and marks them on the map.")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [Menu("How far to look",
        "The game lists what is in the area whether or not you can see it, so this is a choice about " +
        "clutter rather than a limit on what can be known. Wide by default: the point is not to walk " +
        "past things.")]
    public RangeNode<int> Range { get; set; } = new RangeNode<int>(1000, 50, 4000);

    [Menu("How many to list", "Length of the ranked list drawn on screen.")]
    public RangeNode<int> ListLength { get; set; } = new RangeNode<int>(6, 1, 20);

    [Menu("Text size")]
    public RangeNode<float> FontScale { get; set; } = new RangeNode<float>(1.6f, 0.5f, 4f);

    [Menu("Mark whole rooms rather than every chest",
        "The buried towns put a handful of chests in each of two or three rooms off one corridor. One " +
        "mark per room answers the question you actually have down there - is this room worth walking " +
        "into - where a mark per chest just repeats the loot filter.")]
    public ToggleNode GroupIntoRooms { get; set; } = new ToggleNode(true);

    [Menu("How far apart two chests can be and still be one room")]
    public RangeNode<int> RoomRadius { get; set; } = new RangeNode<int>(90, 20, 400);

    [Menu("Hide chests that drop nothing", "The mine spawns decoy chests whose own name says NoDrops.")]
    public ToggleNode HideEmpty { get; set; } = new ToggleNode(true);

    [Menu("Only show things worth at least this much",
        "1 lists everything. 2 drops the filler and leaves what is worth a detour. 3 leaves only the " +
        "things worth abandoning what you were doing for - special chamber chests, the top resonators, " +
        "the big azurite and the good fossils.")]
    public RangeNode<int> MinimumTier { get; set; } = new RangeNode<int>(2, 1, 3);

    [Menu("Mark objects on the map by worth",
        "Draws a coloured mark, not another copy of the name - MinimapIcons already writes the name, " +
        "and two plugins writing the same word on the same spot is what made it unreadable.")]
    public ToggleNode DrawMapLabels { get; set; } = new ToggleNode(true);

    [Menu("Also rank chests outside the mine",
        "The same worth ranking applied to any area's chests, so a side dungeon is worth entering or " +
        "it is not before you walk down there.")]
    public ToggleNode Everywhere { get; set; } = new ToggleNode(true);

    [Menu("Hide the plainest chests",
        "The generic supply chests are the mine's filler. They are worth taking if you walk past one " +
        "and never worth a detour, so they only clutter the list.")]
    public ToggleNode HideGeneric { get; set; } = new ToggleNode(true);

    [Menu("Export the mine's own database",
        "Writes every biome, room feature and league modifier the game ships to a snapshot file, so " +
        "node ranking is built on the installed patch rather than on a community page.")]
    public ButtonNode ExportCatalogue { get; set; } = new ButtonNode();

    public DelveAzuriteSettings Azurite { get; set; } = new DelveAzuriteSettings();
    public DelveWallSettings Walls { get; set; } = new DelveWallSettings();
}

[Submenu(CollapsedByDefault = false)]
public class DelveAzuriteSettings
{
    [Menu("Mark azurite veins")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [Menu("Only show veins at least this rich",
        "The mine grades a vein in its own name - a Flawed vein is the poorest. 1 shows every vein, " +
        "raise it to be told only about the ones worth a detour.")]
    public RangeNode<int> MinimumGrade { get; set; } = new RangeNode<int>(1, 1, 4);

    [Menu("Only show fossils at least this rich",
        "1 shows every fossil chest, 3 only the types worth stopping the cart for.")]
    public RangeNode<int> MinimumFossilGrade { get; set; } = new RangeNode<int>(1, 1, 3);
}

[Submenu(CollapsedByDefault = false)]
public class DelveWallSettings
{
    [Menu("Mark walls that can be blown open")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [Menu("Include walls the game has not revealed yet",
        "The mine hides some walls until you are almost touching them, so a passage that exists reads " +
        "as a dead end. These are the ones that save the most walking.")]
    public ToggleNode ShowUndiscovered { get; set; } = new ToggleNode(true);

    [Menu("Only mark walls whose contents are worth at least this much",
        "2 shows only the walls worth the dynamite and draws nothing at all for the rest, which is the " +
        "answer to whether to stop. 0 marks every wall including the ones nothing was found behind. " +
        "Walls the game has not revealed are always marked - there the passage itself is the reward.")]
    public RangeNode<int> MinimumTier { get; set; } = new RangeNode<int>(2, 0, 3);

    [Menu("Draw a line to the way through",
        "A hidden wall is a route the map is denying exists, so knowing the room has one is only half " +
        "of it - the line says which way to walk.")]
    public ToggleNode GuideToSecret { get; set; } = new ToggleNode(true);

    [Menu("Keep the line up while running", "Drawn in the world too, not only on the open map.")]
    public ToggleNode GuideInWorld { get; set; } = new ToggleNode(true);

    [Menu("How far to look for walls",
        "Walls get their own reach, wider than everything else. A chest you never come near costs you " +
        "nothing, but a passage missed because it sat outside the radius is a room you never entered.")]
    public RangeNode<int> Range { get; set; } = new RangeNode<int>(2000, 100, 6000);

    [Menu("Prompt to throw dynamite within this distance", "How close you have to be before the reminder appears.")]
    public RangeNode<int> PromptDistance { get; set; } = new RangeNode<int>(70, 10, 400);

    [Menu("Only prompt when something is behind the wall",
        "The mine also places walls with nothing behind them. Turn this off to be told about every wall.")]
    public ToggleNode OnlyWhenLootBehind { get; set; } = new ToggleNode(false);

    [Menu("How far behind a wall to look for its contents",
        "A chest is treated as belonging to the nearest wall within this distance.")]
    public RangeNode<int> ContentsRadius { get; set; } = new RangeNode<int>(120, 20, 400);
}

/// <summary>What an object in the mine is, ordered so a bigger value is a better reason to walk over.</summary>
public enum DelveCategory
{
    Unknown,
    Empty,
    Generic,
    Armour,
    Weapon,
    Gem,
    Trinkets,
    Map,
    Flares,
    Dynamite,
    Currency,
    Divination,
    Fossil,
    Resonator,
    AzuriteShard,
    Azurite,
    Special,
    Wall,
}

public readonly record struct DelveKind(
    DelveCategory Category,
    bool OffPath,
    bool BehindWall,
    bool Empty,
    int Tier)
{
    public Color Color => Category switch
    {
        DelveCategory.Special => Color.Magenta,
        DelveCategory.Azurite => Color.Cyan,
        DelveCategory.AzuriteShard => Color.LightBlue,
        DelveCategory.Resonator => Color.Orange,
        DelveCategory.Fossil => Color.OrangeRed,
        DelveCategory.Divination => Color.OrangeRed,
        DelveCategory.Currency => Color.Gold,
        DelveCategory.Dynamite => Color.IndianRed,
        DelveCategory.Flares => Color.Yellow,
        DelveCategory.Map => Color.Aqua,
        DelveCategory.Trinkets => Color.GreenYellow,
        DelveCategory.Gem => Color.LimeGreen,
        DelveCategory.Wall => Color.Magenta,
        DelveCategory.Empty => Color.Gray,
        _ => Color.WhiteSmoke,
    };
}
