namespace SiegeFX.Core.Install;

/// <summary>A file a known release ships, by name (any folder) and size, with
/// its SHA-256 once someone has reported it from a verified copy.</summary>
public sealed record KnownFile(string FileName, long Size, string? Sha256 = null);

public sealed record KnownRelease(string Name, IReadOnlyList<KnownFile> Files);

public enum MatchQuality
{
    /// <summary>No listed file is present with the right size.</summary>
    None,
    /// <summary>Some listed files match; others are missing or differ.</summary>
    Partial,
    /// <summary>Every listed file is present at the right size; hashes not all confirmed.</summary>
    SizeOnly,
    /// <summary>Every listed file matches by size and SHA-256.</summary>
    Exact,
}

public sealed record ReleaseMatch(
    KnownRelease Release, MatchQuality Quality, int Matched, IReadOnlyList<string> Differences);

/// <summary>
/// Catalog of game-data releases SiegeFX has seen, and the matcher that places
/// an <see cref="InstallReport"/> against it. Entries come only from reports of
/// real installs (`siegefx install identify` output): a guessed size or hash is
/// worse than none. To add a release, paste the tank lines from a report into
/// a new entry and note where the copy came from.
/// </summary>
public static class KnownReleases
{
    public static readonly IReadOnlyList<KnownRelease> All =
    [
        // GOG, game version 1.11. Sizes from the original maintainer's
        // known-good install (the runtime's boot integrity check); hashes
        // still to be reported.
        new("Dungeon Siege 1.11 (GOG)",
        [
            new("Logic.dsres",     4_206_896),
            new("Objects.dsres", 304_438_568),
            new("Sound.dsres",   185_343_092),
            new("Terrain.dsres", 410_230_240),
            new("Voices.dsres",   45_951_736),
        ]),
    ];

    /// <summary>Every catalog release with at least one matching file, best first.</summary>
    public static IReadOnlyList<ReleaseMatch> Match(InstallReport report, IEnumerable<KnownRelease>? catalog = null)
    {
        var files = report.Tanks.Select(t => (Name: Path.GetFileName(t.RelativePath), t.Size, t.Sha256))
            .Concat(report.Executables.Select(e => (Name: Path.GetFileName(e.RelativePath), e.Size, e.Sha256)))
            .ToList();

        var results = new List<ReleaseMatch>();
        foreach (var release in catalog ?? All)
        {
            int matched = 0, hashed = 0;
            var diffs = new List<string>();
            foreach (var want in release.Files)
            {
                var have = files.Where(f => f.Name.Equals(want.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (have.Count == 0) { diffs.Add($"{want.FileName}: missing"); continue; }
                var sized = have.Where(f => f.Size == want.Size).ToList();
                if (sized.Count == 0)
                {
                    diffs.Add($"{want.FileName}: {have[0].Size:N0} bytes, expected {want.Size:N0}");
                    continue;
                }
                if (want.Sha256 is not null && sized.All(f => f.Sha256 is not null))
                {
                    if (!sized.Any(f => want.Sha256.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)))
                    {
                        diffs.Add($"{want.FileName}: same size, different SHA-256");
                        continue;
                    }
                    hashed++;
                }
                matched++;
            }

            var quality = matched == 0 ? MatchQuality.None
                        : matched < release.Files.Count ? MatchQuality.Partial
                        : hashed == release.Files.Count ? MatchQuality.Exact
                        : MatchQuality.SizeOnly;
            if (quality != MatchQuality.None)
                results.Add(new ReleaseMatch(release, quality, matched, diffs));
        }
        return results.OrderByDescending(m => m.Quality).ThenByDescending(m => m.Matched).ToList();
    }

    /// <summary>One line for logs and the boot banner, e.g.
    /// "Dungeon Siege 1.11 (GOG) [size match]". An unrecognized install gets
    /// a pointer to `siegefx install identify` unless <paramref name="hint"/>
    /// is off (the identify report itself).</summary>
    public static string Describe(InstallReport report, bool hint = true)
    {
        var best = Match(report).FirstOrDefault(m => m.Quality >= MatchQuality.SizeOnly);
        var expansion = report.ExpansionTanks.Any() ? ", expansion tanks present" : "";
        var version = report.DataVersion is { } v ? $"data {v}" : "no tanks";
        return best is null
            ? $"unrecognized edition ({version}{expansion})" +
              (hint ? " — run `siegefx install identify` and attach the output to a report" : "")
            : $"{best.Release.Name} [{(best.Quality == MatchQuality.Exact ? "hash verified" : "size match")}] ({version}{expansion})";
    }
}
