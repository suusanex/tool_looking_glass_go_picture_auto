using System.Security.Cryptography;
using System.Text;
using LookGo.Core.Models;

namespace LookGo.Core.Playlist;

public static class PlaylistDocument
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp",
    };

    private static readonly UTF8Encoding StrictUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string ExpandUserPath(string rawPath)
    {
        var trimmed = rawPath.Trim();
        if (trimmed == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, trimmed[2..]);
        }
        return trimmed;
    }

    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            var fullA = Path.GetFullPath(ExpandUserPath(a));
            var fullB = Path.GetFullPath(ExpandUserPath(b));
            return string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static (string ResolvedPath, bool IsValid, string ValidationMessage) ValidateRow(
        string rawPath,
        string? baseDirectory)
    {
        var trimmed = rawPath?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
            return ("", false, "パスが空です");

        string resolved;
        try
        {
            var expanded = ExpandUserPath(trimmed);
            var combined = !Path.IsPathRooted(expanded) && !string.IsNullOrWhiteSpace(baseDirectory)
                ? Path.Combine(baseDirectory, expanded)
                : expanded;
            resolved = Path.GetFullPath(combined);
        }
        catch (Exception ex)
        {
            return ("", false, $"無効なパスです: {ex.Message}");
        }

        if (!File.Exists(resolved))
            return (resolved, false, "ファイルが存在しません");

        var ext = Path.GetExtension(resolved);
        if (!SupportedExtensions.Contains(ext))
            return (resolved, false, $"未対応の形式です ({(string.IsNullOrEmpty(ext) ? "拡張子なし" : ext)})");

        try
        {
            Span<byte> header = stackalloc byte[32];
            using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(header);
            if (!HasValidImageSignature(ext, header[..read]))
                return (resolved, false, "画像ファイルが破損しているか形式が一致しません");
        }
        catch (Exception ex)
        {
            return (resolved, false, $"画像を読み込めません: {ex.Message}");
        }

        return (resolved, true, "OK");
    }

    private static bool HasValidImageSignature(string extension, ReadOnlySpan<byte> header)
    {
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<byte> pngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            return header.Length >= pngSig.Length && header[..pngSig.Length].SequenceEqual(pngSig);
        }

        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return header.Length >= 2 && header[0] == 0xFF && header[1] == 0xD8;

        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return header.Length >= 12 &&
                   header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
                   header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P';
        }

        return false;
    }

    public static EditorRow CreateRow(int orderNumber, string rawPath, string? baseDirectory, string? existingId = null)
    {
        var trimmed = rawPath?.Trim() ?? "";
        var (resolved, isValid, message) = ValidateRow(trimmed, baseDirectory);
        return new EditorRow(
            Id: existingId ?? Guid.NewGuid().ToString("N"),
            OrderNumber: orderNumber,
            RawPath: trimmed,
            ResolvedPath: resolved,
            IsValid: isValid,
            ValidationMessage: message);
    }

    public static IReadOnlyList<EditorRow> RenumberAndRevalidate(IReadOnlyList<EditorRow> rows, string? baseDirectory)
    {
        var updated = new List<EditorRow>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            updated.Add(CreateRow(i + 1, row.RawPath, baseDirectory, row.Id));
        }
        return updated;
    }

    public static (string FullPath, IReadOnlyList<EditorRow> Rows, string Sha256) ReadFile(string filePath)
    {
        var fullPath = Path.GetFullPath(ExpandUserPath(filePath));
        var rawBytes = File.ReadAllBytes(fullPath);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(rawBytes));

        var contentBytes = rawBytes.AsSpan();
        if (contentBytes.Length >= 3 && contentBytes[0] == 0xEF && contentBytes[1] == 0xBB && contentBytes[2] == 0xBF)
            contentBytes = contentBytes[3..];

        string text;
        try
        {
            text = StrictUtf8NoBom.GetString(contentBytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"プレイリスト '{fullPath}' は有効な UTF-8 テキストではありません。", ex);
        }

        var baseDir = Path.GetDirectoryName(fullPath);
        var rows = new List<EditorRow>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            rows.Add(CreateRow(rows.Count + 1, trimmed, baseDir));
        }

        return (fullPath, rows, sha256);
    }

    public static string? ValidateForSave(IReadOnlyList<EditorRow> rows)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.RawPath))
                return $"{row.OrderNumber} 行目のパスが未入力です。パスを入力するか行を削除してから保存してください。";
        }
        return null;
    }

    public static byte[] SerializeUtf8Bytes(IReadOnlyList<EditorRow> rows)
    {
        var saveError = ValidateForSave(rows);
        if (saveError is not null)
            throw new InvalidOperationException(saveError);

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var trimmed = row.RawPath.Trim();
            sb.Append(trimmed);
            sb.Append('\n');
        }
        return StrictUtf8NoBom.GetBytes(sb.ToString());
    }

    public static (string FullPath, string Sha256) WriteFileAtomic(string filePath, IReadOnlyList<EditorRow> rows)
    {
        var fullPath = Path.GetFullPath(ExpandUserPath(filePath));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var bytes = SerializeUtf8Bytes(rows);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        return (fullPath, sha256);
    }

    public static IReadOnlyList<EditorRow> RebaseRowsForSaveAs(
        IReadOnlyList<EditorRow> rows,
        string? previousFilePath,
        string targetFilePath)
    {
        var targetFull = Path.GetFullPath(ExpandUserPath(targetFilePath));
        var targetDir = Path.GetDirectoryName(targetFull);
        var prevDir = string.IsNullOrWhiteSpace(previousFilePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(ExpandUserPath(previousFilePath)));

        var sameDir = !string.IsNullOrWhiteSpace(prevDir) &&
                      !string.IsNullOrWhiteSpace(targetDir) &&
                      string.Equals(prevDir, targetDir, StringComparison.OrdinalIgnoreCase);

        if (sameDir || string.IsNullOrWhiteSpace(targetDir))
            return RenumberAndRevalidate(rows, targetDir);

        var rebased = new List<EditorRow>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var raw = row.RawPath.Trim();
            var rewritten = raw;
            if (raw.Length > 0 && !Path.IsPathRooted(raw) && !raw.StartsWith("~", StringComparison.Ordinal))
            {
                var sourceResolved = row.ResolvedPath;
                if (string.IsNullOrEmpty(sourceResolved) && !string.IsNullOrWhiteSpace(prevDir))
                {
                    try { sourceResolved = Path.GetFullPath(Path.Combine(prevDir, raw)); }
                    catch { sourceResolved = ""; }
                }
                if (!string.IsNullOrEmpty(sourceResolved))
                {
                    var srcRoot = Path.GetPathRoot(sourceResolved);
                    var dstRoot = Path.GetPathRoot(targetDir);
                    rewritten = string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase)
                        ? Path.GetRelativePath(targetDir, sourceResolved)
                        : sourceResolved;
                }
            }
            rebased.Add(CreateRow(i + 1, rewritten, targetDir, row.Id));
        }

        return rebased;
    }

    public static string? ValidateForApply(
        IReadOnlyList<EditorRow> rows,
        double intervalSeconds,
        string? targetFilePath)
    {
        if (!double.IsFinite(intervalSeconds) || intervalSeconds <= 0)
            return "切替間隔は 0 より大きい数値にしてください。";

        if (rows.Count == 0)
            return "プレイリストが空です。少なくとも 1 枚の画像を追加してください。";

        var targetDir = string.IsNullOrWhiteSpace(targetFilePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(ExpandUserPath(targetFilePath)));

        foreach (var row in rows)
        {
            var (_, isValid, message) = ValidateRow(row.RawPath, targetDir);
            if (!isValid)
                return $"{row.OrderNumber} 行目が無効です: {message}";
        }

        return null;
    }
}
