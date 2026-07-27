using Hoard.Core.Library;

namespace Hoard.Desktop.Services;

/// <summary>How an export run reads back to the user — shared by the board export and the project export
/// so the two never drift apart.</summary>
public static class ExportSummary
{
    public static string Describe(ExportReport report)
    {
        var text = report.Copied == 0
            ? "Already up to date — nothing new to copy."
            : report.UpToDate == 0
                ? $"Done — copied {report.Copied} file(s)."
                : $"Done — copied {report.Copied} file(s); {report.UpToDate} already there.";
        if (report.MissingBlobs > 0)
            text += $" Skipped {report.MissingBlobs} whose file is missing from the store — re-download those first.";
        return text;
    }
}
