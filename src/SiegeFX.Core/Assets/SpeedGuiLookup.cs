using System.Globalization;
using SiegeFX.Core.Tank;

namespace SiegeFX.Core.Assets;

/// <summary><c>/world/global/speed_gui_lookup.gas</c> — the words a weapon
/// card shows for attack speed. Each child of <c>[speed_gui_lookup]</c> is
/// one tier (<c>[super]</c> … <c>[slowest]</c>) with its
/// <c>screen_name</c> and the <c>min_range</c>..<c>max_range</c> of speed
/// values it covers; the file's own header says the value is the wielder's
/// attack animation time plus the weapon's reload delay. The text comes
/// from the player's install, never from this code.</summary>
public sealed class SpeedGuiLookup
{
    public const string DefaultPath = "/world/global/speed_gui_lookup.gas";

    public sealed record Tier(string Key, string ScreenName, float MinRange, float MaxRange);

    /// <summary>The tiers in authored order.</summary>
    public IReadOnlyList<Tier> Tiers { get; }

    SpeedGuiLookup(IReadOnlyList<Tier> tiers) => Tiers = tiers;

    /// <summary>The screen name of the tier keyed <paramref name="key"/>
    /// (e.g. <c>normal</c>), or null when the data has no such tier.</summary>
    public string? ScreenName(string key) =>
        Tiers.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.ScreenName;

    /// <summary>The tier whose range holds <paramref name="speed"/>, or null.</summary>
    public Tier? ForSpeed(float speed) =>
        Tiers.FirstOrDefault(t => speed >= t.MinRange && speed <= t.MaxRange);

    public static SpeedGuiLookup LoadFromTank(TankReader tank, string path = DefaultPath) =>
        Parse(GasDocument.Load(tank.ExtractToMemory(path)));

    public static SpeedGuiLookup Parse(GasDocument doc)
    {
        var tiers = new List<Tier>();
        foreach (var root in doc.Roots)
        {
            if (!root.Header.Equals("speed_gui_lookup", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var t in root.Children)
            {
                string? name = null;
                float min = 0, max = 0;
                foreach (var a in t.Attributes)
                {
                    if (a.Name.Equals("screen_name", StringComparison.OrdinalIgnoreCase)) name = a.Value.Trim().Trim('"');
                    else if (a.Name.Equals("min_range", StringComparison.OrdinalIgnoreCase)) min = ParseFloat(a.Value);
                    else if (a.Name.Equals("max_range", StringComparison.OrdinalIgnoreCase)) max = ParseFloat(a.Value);
                }
                if (name is not null) tiers.Add(new Tier(t.Header, name, min, max));
            }
        }
        return new SpeedGuiLookup(tiers);
    }

    static float ParseFloat(string s) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
}
