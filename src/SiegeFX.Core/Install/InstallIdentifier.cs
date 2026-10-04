using System.Security.Cryptography;
using SiegeFX.Core.Tank;

namespace SiegeFX.Core.Install;

/// <summary>One tank found in an install: its on-disk identity plus the header
/// fields GPG stamped at build time (product version, priority, build text,
/// guid, CRCs). The header alone is enough to tell editions and patches apart
/// without hashing gigabytes; <see cref="Sha256"/> is the exact fingerprint.</summary>
public sealed record TankFingerprint(
    string RelativePath,
    long Size,
    string ProductId,
    ProductVersion ProductVersion,
    ProductVersion MinimumVersion,
    TankPriority Priority,
    TankFlags Flags,
    string CreatorId,
    Guid Guid,
    uint IndexCrc32,
    uint DataCrc32,
    DateTime UtcBuildTime,
    string Title,
    string BuildText,
    string Description,
    string? Sha256);

/// <summary>A non-tank file worth fingerprinting (the game executables).</summary>
public sealed record FileFingerprint(string RelativePath, long Size, string? Sha256);

/// <summary>What <see cref="InstallIdentifier.Scan"/> found under an install root.</summary>
public sealed record InstallReport(
    string Root,
    IReadOnlyList<TankFingerprint> Tanks,
    IReadOnlyList<FileFingerprint> Executables,
    IReadOnlyList<string> Unreadable)
{
    /// <summary>DS1 is playable from here: the core resource tanks and a map.</summary>
    public bool HasBaseGame =>
        HasTank("Logic.dsres") && HasTank("Objects.dsres") && Maps.Any();

    /// <summary>Tanks GPG marked as expansion content (Legends of Aranna).</summary>
    public IEnumerable<TankFingerprint> ExpansionTanks =>
        Tanks.Where(t => t.Priority == TankPriority.Expansion);

    /// <summary>Tanks GPG marked as patch content (official updates layered on top).</summary>
    public IEnumerable<TankFingerprint> PatchTanks =>
        Tanks.Where(t => t.Priority == TankPriority.Patch);

    public IEnumerable<TankFingerprint> Maps =>
        Tanks.Where(t => t.RelativePath.EndsWith(".dsmap", StringComparison.OrdinalIgnoreCase));

    /// <summary>Highest product version stamped on any tank — the effective data version.</summary>
    public ProductVersion? DataVersion =>
        Tanks.Count == 0 ? null
        : Tanks.Select(t => t.ProductVersion)
               .OrderBy(v => v.V1).ThenBy(v => v.V2).ThenBy(v => v.V3)
               .Last();

    bool HasTank(string fileName) =>
        Tanks.Any(t => Path.GetFileName(t.RelativePath).Equals(fileName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Fingerprints a Dungeon Siege install so testers can say exactly which data
/// they run (retail CD, a patch level, GOG, Steam, Legends of Aranna) and so
/// SiegeFX can recognize known editions. Tanks are found by their header magic
/// rather than by name or extension, so expansion and patch tanks we have not
/// catalogued still show up. Nothing is modified and no asset bytes are kept;
/// the report holds sizes, header fields and hashes only.
/// </summary>
public static class InstallIdentifier
{
    // Install layouts nest resources one or two levels down (Resources\, Maps\);
    // three keeps the scan away from deep save/screenshot trees.
    const int MaxDepth = 3;

    /// <summary>Scan <paramref name="path"/> (the install root, or its Resources
    /// folder). With <paramref name="hash"/> every tank and executable is
    /// SHA-256 hashed — a few seconds per gigabyte; without it only headers
    /// are read.</summary>
    public static InstallReport Scan(string path, bool hash = true)
    {
        var root = Path.GetFullPath(path);
        if (Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals("Resources", StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"no such directory: {root}");

        var tanks = new List<TankFingerprint>();
        var exes = new List<FileFingerprint>();
        var unreadable = new List<string>();

        foreach (var file in EnumerateFiles(root, MaxDepth))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            try
            {
                if (LooksLikeTank(file))
                {
                    tanks.Add(Fingerprint(file, rel, hash));
                }
                else if (Path.GetDirectoryName(rel) is "" or null &&
                         Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    exes.Add(new FileFingerprint(rel, new FileInfo(file).Length, hash ? Sha256Of(file) : null));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TankException)
            {
                unreadable.Add($"{rel}: {ex.Message}");
            }
        }

        tanks.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        exes.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return new InstallReport(root, tanks, exes, unreadable);
    }

    static TankFingerprint Fingerprint(string file, string rel, bool hash)
    {
        long size;
        TankHeader h;
        using (var tank = TankFile.Open(file))
        {
            size = tank.SizeBytes;
            h = tank.Header;
        }
        return new TankFingerprint(
            rel, size, h.ProductId.ToString(), h.ProductVersion, h.MinimumVersion,
            h.Priority, h.Flags, h.CreatorId.ToString(), h.Guid.ToGuid(),
            h.IndexCrc32, h.DataCrc32, h.UtcBuildTime.ToDateTime(),
            h.TitleText, h.BuildText, h.DescriptionText,
            hash ? Sha256Of(file) : null);
    }

    /// <summary>True when the file opens with a DS1/DS2 tank magic ("DSigTank" / "DSg2Tank").</summary>
    public static bool LooksLikeTank(string file)
    {
        Span<byte> magic = stackalloc byte[8];
        using var fs = File.OpenRead(file);
        if (fs.ReadAtLeast(magic, 8, throwOnEndOfStream: false) < 8) return false;
        return magic[4] == 'T' && magic[5] == 'a' && magic[6] == 'n' && magic[7] == 'k'
            && magic[0] == 'D' && magic[1] == 'S'
            && ((magic[2] == 'i' && magic[3] == 'g') || (magic[2] == 'g' && magic[3] == '2'));
    }

    static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    static IEnumerable<string> EnumerateFiles(string dir, int depth)
    {
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(dir);
            dirs = depth > 1 ? Directory.GetDirectories(dir) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
            foreach (var f in EnumerateFiles(d, depth - 1))
                yield return f;
    }
}
