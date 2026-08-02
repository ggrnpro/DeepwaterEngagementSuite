using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace DeepwaterEngagementSuiteGGRN;

[Submenu(CollapsedByDefault = true)]
public class DelveSettings
{
    [Menu("Show what is worth taking in the mine", "Ranks the objects around you and marks them on the map.")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [Menu("How far to look", "Objects further away than this are ignored entirely.")]
    public RangeNode<int> Range { get; set; } = new RangeNode<int>(400, 50, 1000);

    [Menu("How many to list", "Length of the ranked list drawn on screen.")]
    public RangeNode<int> ListLength { get; set; } = new RangeNode<int>(6, 1, 20);

    [Menu("Text size")]
    public RangeNode<float> FontScale { get; set; } = new RangeNode<float>(1.6f, 0.5f, 4f);

    [Menu("Hide chests that drop nothing", "The mine spawns decoy chests whose own name says NoDrops.")]
    public ToggleNode HideEmpty { get; set; } = new ToggleNode(true);

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
