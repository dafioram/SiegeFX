using System.Security.Cryptography;
using SiegeFX.Core.Install;
using SiegeFX.Core.Tank;

namespace SiegeFX.Core.Tests;

/// <summary>Builds throwaway installs out of tiny synthetic tanks.</summary>
public sealed class FakeInstall : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"siegefx-install-{Guid.NewGuid():N}");

    public FakeInstall() => Directory.CreateDirectory(Root);

    public string Tank(string relPath, TankPriority priority = TankPriority.Factory, string title = "")
    {
        var path = Path.Combine(Root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var w = new TankWriter { Priority = priority, Title = title, BuildText = "test build" };
        w.Add("/" + Path.GetFileNameWithoutExtension(relPath).ToLowerInvariant() + "/a.gas", "[a] { x = 1; }"u8.ToArray());
        w.Write(path, new DateTime(2002, 4, 5, 6, 7, 8, DateTimeKind.Utc));
        return path;
    }

    public string File(string relPath, byte[] bytes)
    {
        var path = Path.Combine(Root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class InstallIdentifierTests
{
    static FakeInstall Ds1(FakeInstall i)
    {
        i.Tank("Resources/Logic.dsres");
        i.Tank("Resources/Objects.dsres");
        i.Tank("Maps/World.dsmap");
        i.File("DungeonSiege.exe", [0x4D, 0x5A, 1, 2, 3]);
        i.File("Resources/readme.txt", "not a tank"u8.ToArray());
        return i;
    }

    [Fact]
    public void FindsTanksByMagicAndFingerprintsThem()
    {
        using var i = Ds1(new FakeInstall());
        var r = InstallIdentifier.Scan(i.Root);

        Assert.Equal(["Maps/World.dsmap", "Resources/Logic.dsres", "Resources/Objects.dsres"],
                     r.Tanks.Select(t => t.RelativePath));
        Assert.True(r.HasBaseGame);
        Assert.Empty(r.Unreadable);

        var logic = r.Tanks.Single(t => t.RelativePath.EndsWith("Logic.dsres"));
        Assert.Equal("DSig", logic.ProductId);
        Assert.Equal("test build", logic.BuildText);
        Assert.Equal(new DateTime(2002, 4, 5, 6, 7, 8, DateTimeKind.Utc), logic.UtcBuildTime);
        var expected = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(i.Root, "Resources/Logic.dsres"))));
        Assert.Equal(expected, logic.Sha256);

        var exe = Assert.Single(r.Executables);
        Assert.Equal("DungeonSiege.exe", exe.RelativePath);
        Assert.Equal(5, exe.Size);
    }

    [Fact]
    public void AcceptsTheResourcesFolderAsTheRoot()
    {
        using var i = Ds1(new FakeInstall());
        var r = InstallIdentifier.Scan(Path.Combine(i.Root, "Resources"));
        Assert.Equal(Path.GetFullPath(i.Root), r.Root);
        Assert.True(r.HasBaseGame);
    }

    [Fact]
    public void FastScanSkipsHashing()
    {
        using var i = Ds1(new FakeInstall());
        var r = InstallIdentifier.Scan(i.Root, hash: false);
        Assert.All(r.Tanks, t => Assert.Null(t.Sha256));
        Assert.All(r.Executables, e => Assert.Null(e.Sha256));
    }

    [Fact]
    public void SeparatesExpansionAndPatchTanksWhateverTheirNames()
    {
        using var i = Ds1(new FakeInstall());
        i.Tank("Resources/Expansion.dsres", TankPriority.Expansion, title: "Legends of Aranna");
        i.Tank("Maps/Yesterhaven.dsmap", TankPriority.Expansion);
        i.Tank("Resources/Patch.dsres", TankPriority.Patch);

        var r = InstallIdentifier.Scan(i.Root);
        Assert.Equal(["Maps/Yesterhaven.dsmap", "Resources/Expansion.dsres"],
                     r.ExpansionTanks.Select(t => t.RelativePath));
        Assert.Equal("Resources/Patch.dsres", Assert.Single(r.PatchTanks).RelativePath);
        Assert.Equal(2, r.Maps.Count());
    }

    [Fact]
    public void CorruptTankIsReportedNotThrown()
    {
        using var i = Ds1(new FakeInstall());
        // Right magic, then garbage: the header's offsets point past EOF.
        i.File("Resources/Broken.dsres", "DSigTank"u8.ToArray().Concat(Enumerable.Repeat((byte)0xFF, 64)).ToArray());

        var r = InstallIdentifier.Scan(i.Root);
        Assert.Contains(r.Unreadable, u => u.StartsWith("Resources/Broken.dsres"));
        Assert.Equal(3, r.Tanks.Count);
    }

    [Fact]
    public void MissingCoreTanksMeansNotPlayable()
    {
        using var i = new FakeInstall();
        i.Tank("Resources/Logic.dsres");
        Assert.False(InstallIdentifier.Scan(i.Root).HasBaseGame);
    }

    [Fact]
    public void MissingDirectoryThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            InstallIdentifier.Scan(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}")));
    }
}

public class KnownReleasesTests
{
    static (InstallReport Report, long LogicSize, string LogicSha) Install(FakeInstall i)
    {
        i.Tank("Resources/Logic.dsres");
        i.Tank("Resources/Objects.dsres");
        i.Tank("Maps/World.dsmap");
        var r = InstallIdentifier.Scan(i.Root);
        var logic = r.Tanks.Single(t => t.RelativePath.EndsWith("Logic.dsres"));
        return (r, logic.Size, logic.Sha256!);
    }

    [Fact]
    public void ExactWhenEverySizeAndHashMatches()
    {
        using var i = new FakeInstall();
        var (r, size, sha) = Install(i);
        var catalog = new[] { new KnownRelease("Test 1.0", [new KnownFile("logic.DSRES", size, sha.ToUpperInvariant())]) };

        var m = Assert.Single(KnownReleases.Match(r, catalog));
        Assert.Equal(MatchQuality.Exact, m.Quality);
        Assert.Empty(m.Differences);
    }

    [Fact]
    public void SizeOnlyWithoutCatalogHashesOrWithAFastScan()
    {
        using var i = new FakeInstall();
        var (r, size, sha) = Install(i);

        var noHash = new[] { new KnownRelease("Test", [new KnownFile("Logic.dsres", size)]) };
        Assert.Equal(MatchQuality.SizeOnly, KnownReleases.Match(r, noHash).Single().Quality);

        var fast = InstallIdentifier.Scan(i.Root, hash: false);
        var withHash = new[] { new KnownRelease("Test", [new KnownFile("Logic.dsres", size, sha)]) };
        Assert.Equal(MatchQuality.SizeOnly, KnownReleases.Match(fast, withHash).Single().Quality);
    }

    [Fact]
    public void PartialListsWhatDiffers()
    {
        using var i = new FakeInstall();
        var (r, size, _) = Install(i);
        var catalog = new[]
        {
            new KnownRelease("Test", [
                new KnownFile("Logic.dsres", size),
                new KnownFile("Objects.dsres", 123),
                new KnownFile("Sound.dsres", 456),
            ]),
        };

        var m = KnownReleases.Match(r, catalog).Single();
        Assert.Equal(MatchQuality.Partial, m.Quality);
        Assert.Equal(1, m.Matched);
        Assert.Contains(m.Differences, d => d.StartsWith("Objects.dsres:") && d.Contains("expected 123"));
        Assert.Contains("Sound.dsres: missing", m.Differences);
    }

    [Fact]
    public void SameSizeDifferentHashDoesNotMatch()
    {
        using var i = new FakeInstall();
        var (r, size, _) = Install(i);
        var catalog = new[] { new KnownRelease("Test", [new KnownFile("Logic.dsres", size, new string('0', 64))]) };
        Assert.Empty(KnownReleases.Match(r, catalog));
    }

    [Fact]
    public void DescribeAsksForAReportWhenUnrecognized()
    {
        using var i = new FakeInstall();
        var (r, _, _) = Install(i);
        Assert.StartsWith("unrecognized edition (data 1.0.0)", KnownReleases.Describe(r));
        Assert.Contains("siegefx install identify", KnownReleases.Describe(r));
    }

    [Fact]
    public void MarkdownReportListsEveryTank()
    {
        using var i = new FakeInstall();
        var (r, _, sha) = Install(i);
        var md = InstallReportText.ToMarkdown(r);
        Assert.Contains("| Resources/Logic.dsres |", md);
        Assert.Contains(sha, md);
        Assert.Contains("**Base game playable:** yes", md);
    }

    [Fact]
    public void CatalogEntriesAreWellFormed()
    {
        Assert.Equal(KnownReleases.All.Count, KnownReleases.All.Select(r => r.Name).Distinct().Count());
        foreach (var release in KnownReleases.All)
        {
            Assert.NotEmpty(release.Files);
            foreach (var f in release.Files)
            {
                Assert.True(f.Size > 0, $"{release.Name}/{f.FileName}: size");
                if (f.Sha256 is not null)
                    Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
            }
        }
    }
}
