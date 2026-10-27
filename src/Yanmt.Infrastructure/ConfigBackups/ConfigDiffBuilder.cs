using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Yanmt.Infrastructure.ConfigBackups;

public enum DiffLineKind { Unchanged, Added, Removed, Modified, Blank }

/// <summary>One rendered line. <see cref="Number"/> is null for padding (blank) lines.</summary>
public sealed record DiffLine(DiffLineKind Kind, int? Number, string Text);

/// <summary>A side-by-side row: <see cref="Old"/> and <see cref="New"/> always line up.</summary>
public sealed record SideBySideRow(DiffLine Old, DiffLine New);

/// <summary>Builds diffs between two config texts (already masked or not, as the caller decided). Pure; no I/O.</summary>
public static class ConfigDiffBuilder
{
    public static IReadOnlyList<SideBySideRow> SideBySide(string oldText, string newText)
    {
        var model = new SideBySideDiffBuilder(new Differ()).BuildDiffModel(oldText, newText, ignoreWhitespace: false);
        var rows = new List<SideBySideRow>(model.OldText.Lines.Count);
        for (var i = 0; i < model.OldText.Lines.Count; i++)
        {
            rows.Add(new SideBySideRow(Map(model.OldText.Lines[i]), Map(model.NewText.Lines[i])));
        }

        return rows;
    }

    /// <summary>One list of lines: removed lines first, then the added ones, in file order.</summary>
    public static IReadOnlyList<DiffLine> Unified(string oldText, string newText)
    {
        var model = new InlineDiffBuilder(new Differ()).BuildDiffModel(oldText, newText, ignoreWhitespace: false);
        return model.Lines.Select(Map).ToList();
    }

    /// <summary>True when the two texts differ (any added or removed line).</summary>
    public static bool HasChanges(IReadOnlyList<DiffLine> unified) => unified.Any(l => l.Kind is not DiffLineKind.Unchanged);

    private static DiffLine Map(DiffPiece p) => new(
        p.Type switch
        {
            ChangeType.Inserted => DiffLineKind.Added,
            ChangeType.Deleted => DiffLineKind.Removed,
            ChangeType.Modified => DiffLineKind.Modified,
            ChangeType.Imaginary => DiffLineKind.Blank,
            _ => DiffLineKind.Unchanged,
        },
        p.Position,
        p.Text ?? "");
}
