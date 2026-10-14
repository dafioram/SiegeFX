using SiegeFX.Core.Assets;

namespace SiegeFX.Tests;

public class SpeedGuiLookupTests
{
    // Invented labels: tests never carry the game's own text.
    const string Sample = """
        // header comment
        [speed_gui_lookup]
        {
            [brisk]  { screen_name = "Brisk Pace";  min_range = 0.0;  max_range = 0.5; }
            [normal] { screen_name = "Middling Pace"; min_range = 0.51; max_range = 1.0; }
            [slow]   { screen_name = "Plodding Pace"; min_range = 1.01; max_range = 100; }
            [nameless] { min_range = 5; max_range = 6; }
        }
        [something_else] { [normal] { screen_name = "Not this one"; } }
        """;

    [Fact]
    public void Reads_each_tier_in_authored_order()
    {
        var lookup = SpeedGuiLookup.Parse(GasDocument.Parse(Sample));
        Assert.Equal(["brisk", "normal", "slow"], lookup.Tiers.Select(t => t.Key));
        Assert.Equal(new SpeedGuiLookup.Tier("slow", "Plodding Pace", 1.01f, 100f), lookup.Tiers[2]);
    }

    [Fact]
    public void Finds_screen_names_by_key_and_by_speed()
    {
        var lookup = SpeedGuiLookup.Parse(GasDocument.Parse(Sample));
        Assert.Equal("Middling Pace", lookup.ScreenName("NORMAL"));
        Assert.Null(lookup.ScreenName("fastest"));
        Assert.Equal("brisk", lookup.ForSpeed(0.3f)?.Key);
        Assert.Equal("slow", lookup.ForSpeed(1.5f)?.Key);
        Assert.Null(lookup.ForSpeed(-1f));
    }
}
