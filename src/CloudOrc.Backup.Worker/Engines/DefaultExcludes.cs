namespace CloudOrc.Backup.Worker.Engines;

/// <summary>
/// Never worth backing up in a folder backup: rebuildable dependency/cache folders and Windows system
/// files. Applied before the customer's own exclusions (Kopia ignore rules and the scanner). A plain
/// name matches at any depth. Deliberately conservative: nothing that could hold the customer's own
/// data (.git keeps unpushed history; "bin" / "build" can be real data folders).
/// </summary>
public static class DefaultExcludes
{
    public static readonly IReadOnlyList<string> Patterns =
    [
        // Rebuildable dependency / cache folders (tens of thousands of tiny files)
        "node_modules",
        "__pycache__",
        ".pytest_cache",
        ".mypy_cache",
        ".gradle",
        ".nuget",

        // Windows system / swap files and recycle bin
        "$RECYCLE.BIN",
        "System Volume Information",
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
        "Thumbs.db",

        // Office "owner" lock files (~$Book1.xlsx) - recreated when the document is opened
        "~$*",
    ];

    /// <summary>Defaults first, then the customer's own rules (duplicates removed).</summary>
    public static IReadOnlyList<string> With(IEnumerable<string>? custom) =>
        Patterns.Concat(custom ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
