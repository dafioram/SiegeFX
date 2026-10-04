using System.Text;

namespace SiegeFX.Core.Install;

/// <summary>Renders an <see cref="InstallReport"/> as Markdown that pastes
/// cleanly into a GitHub issue, and is the format the README's
/// supported-data table is built from.</summary>
public static class InstallReportText
{
    public static string ToMarkdown(InstallReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### SiegeFX install report");
        sb.AppendLine();
        var matches = KnownReleases.Match(report);
        sb.AppendLine($"- **Edition:** {KnownReleases.Describe(report, hint: false)}");
        if (!matches.Any(m => m.Quality >= MatchQuality.SizeOnly))
            sb.AppendLine("- Not in SiegeFX's release catalog yet. Please attach this report to an issue so it can be added.");
        sb.AppendLine($"- **Base game playable:** {(report.HasBaseGame ? "yes" : "no (Logic.dsres, Objects.dsres or a .dsmap missing)")}");
        sb.AppendLine($"- **Maps:** {(report.Maps.Any() ? string.Join(", ", report.Maps.Select(m => m.RelativePath)) : "none")}");
        if (report.ExpansionTanks.Any())
            sb.AppendLine($"- **Expansion tanks:** {string.Join(", ", report.ExpansionTanks.Select(t => t.RelativePath))}");
        if (report.PatchTanks.Any())
            sb.AppendLine($"- **Patch tanks:** {string.Join(", ", report.PatchTanks.Select(t => t.RelativePath))}");

        foreach (var m in matches.Where(m => m.Quality == MatchQuality.Partial))
            sb.AppendLine($"- **Near match:** {m.Release.Name} ({m.Matched}/{m.Release.Files.Count} files; {string.Join("; ", m.Differences)})");

        sb.AppendLine();
        sb.AppendLine("| Tank | Size | Version | Priority | Build | Built (UTC) | Title | SHA-256 |");
        sb.AppendLine("|---|---:|---|---|---|---|---|---|");
        foreach (var t in report.Tanks)
            sb.AppendLine($"| {t.RelativePath} | {t.Size:N0} | {t.ProductVersion} | {t.Priority} | {Cell(t.BuildText)} | " +
                          $"{(t.UtcBuildTime == DateTime.MinValue ? "" : t.UtcBuildTime.ToString("yyyy-MM-dd HH:mm"))} | " +
                          $"{Cell(t.Title)} | {t.Sha256 ?? "(not hashed)"} |");

        if (report.Executables.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| Executable | Size | SHA-256 |");
            sb.AppendLine("|---|---:|---|");
            foreach (var e in report.Executables)
                sb.AppendLine($"| {e.RelativePath} | {e.Size:N0} | {e.Sha256 ?? "(not hashed)"} |");
        }

        if (report.Unreadable.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Unreadable:");
            foreach (var u in report.Unreadable) sb.AppendLine($"- {u}");
        }
        return sb.ToString();
    }

    static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
}
