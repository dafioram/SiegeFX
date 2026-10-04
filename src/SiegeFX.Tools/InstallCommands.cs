using System.Text.Json;
using SiegeFX.Core.Install;

/// <summary>`siegefx install identify` — fingerprint a Dungeon Siege install
/// (retail, patched, GOG, Steam, Legends of Aranna) so a bug report says
/// exactly which game data it ran against. Reads headers and hashes only.</summary>
static class InstallCommands
{
    public static int Dispatch(string[] a)
    {
        if (a.Length == 0 || !a[0].Equals("identify", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("usage: siegefx install identify [install-dir] [--fast] [--json]");
            return 1;
        }
        bool fast = a.Contains("--fast", StringComparer.OrdinalIgnoreCase);
        bool json = a.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var dir = a.Skip(1).FirstOrDefault(x => !x.StartsWith("--"))
                  ?? Environment.GetEnvironmentVariable("SIEGEFX_DS1");
        if (string.IsNullOrWhiteSpace(dir))
        {
            Console.Error.WriteLine("siegefx install identify: pass the Dungeon Siege folder (or set SIEGEFX_DS1).");
            return 1;
        }

        if (!fast && !json)
            Console.Error.WriteLine("hashing every tank (about a minute for a full install; --fast reads headers only)...");
        var report = InstallIdentifier.Scan(dir, hash: !fast);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                report.Root,
                Edition = KnownReleases.Describe(report, hint: false),
                report.HasBaseGame,
                DataVersion = report.DataVersion?.ToString(),
                Tanks = report.Tanks.Select(t => new
                {
                    t.RelativePath, t.Size, t.ProductId,
                    ProductVersion = t.ProductVersion.ToString(),
                    MinimumVersion = t.MinimumVersion.ToString(),
                    Priority = t.Priority.ToString(), Flags = t.Flags.ToString(), t.CreatorId,
                    t.Guid, IndexCrc32 = $"{t.IndexCrc32:x8}", DataCrc32 = $"{t.DataCrc32:x8}",
                    t.UtcBuildTime, t.Title, t.BuildText, t.Description, t.Sha256,
                }),
                report.Executables,
                report.Unreadable,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                // Console output, not HTML: keep dashes and backticks readable.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        else
        {
            Console.WriteLine(InstallReportText.ToMarkdown(report));
        }
        return report.Tanks.Count > 0 ? 0 : 3;
    }
}
