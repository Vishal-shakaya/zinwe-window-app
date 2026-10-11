using System.Text.RegularExpressions;

namespace Zinwe.Agent.Core.Features.DataTransfer;

/// <summary>
/// Pure helpers that turn raw Windows drive/path strings into the stable, owner-recognisable identifier a
/// transfer is recorded under. No file names or sub-paths ever appear in a key.
/// </summary>
public static partial class TransferKeys
{
    /// <summary>Longest key we keep (also the metric store's own cap); keeps a runaway label from bloating a row.</summary>
    public const int MaxKeyLength = 120;

    /// <summary>
    /// A removable drive's key: drive letter plus volume label when known, e.g. "E: (KINGSTON)" or just "E:".
    /// </summary>
    public static string RemovableKey(string root, string? volumeLabel)
    {
        var letter = DriveLetter(root);
        var label = (volumeLabel ?? "").Trim();
        var key = label.Length == 0 ? letter : $"{letter} ({label})";
        return Clip(key);
    }

    /// <summary>
    /// A network share's key: "host\share" from the UNC the drive maps to (e.g. "\\\\FS01\\Finance\\x" → "FS01\\Finance").
    /// Falls back to the drive letter plus label when the UNC is unknown (mapped drive we could not resolve).
    /// </summary>
    public static string NetworkKey(string root, string? uncTarget, string? volumeLabel)
    {
        var share = ShareFromUnc(uncTarget);
        if (share is not null)
        {
            return Clip(share);
        }
        var letter = DriveLetter(root);
        var label = (volumeLabel ?? "").Trim();
        return Clip(label.Length == 0 ? letter : $"{letter} ({label})");
    }

    /// <summary>"\\\\FS01\\Finance\\sub\\file.xlsx" → "FS01\\Finance"; null/garbage → null. Sub-path is dropped.</summary>
    public static string? ShareFromUnc(string? unc)
    {
        if (string.IsNullOrWhiteSpace(unc))
        {
            return null;
        }
        var match = UncPattern().Match(unc.Trim());
        if (!match.Success)
        {
            return null;
        }
        var host = match.Groups["host"].Value;
        var share = match.Groups["share"].Value;
        return share.Length == 0 ? host : $"{host}\\{share}";
    }

    /// <summary>"E:\\" or "E:" → "E:"; anything without a leading drive letter is returned trimmed.</summary>
    public static string DriveLetter(string root)
    {
        var trimmed = (root ?? "").Trim();
        return trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'
            ? trimmed[..2].ToUpperInvariant()
            : trimmed;
    }

    private static string Clip(string value) =>
        value.Length <= MaxKeyLength ? value : value[..MaxKeyLength];

    [GeneratedRegex(@"^\\\\(?<host>[^\\]+)(\\(?<share>[^\\]+))?", RegexOptions.CultureInvariant)]
    private static partial Regex UncPattern();
}
