using System.Diagnostics;
using System.Text;
using LookGo.Core.Models;
using LookGo.Core.Playlist;
using Xunit;

namespace LookGo.Core.Tests;

public sealed class PlaylistDocumentTests : IDisposable
{
    private readonly string _tempDir;

    public PlaylistDocumentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lookgo-core-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static byte[] MakePngHeader() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static byte[] MakeJpegHeader() => [0xFF, 0xD8, 0xFF, 0xE0];
    private static byte[] MakeWebpHeader() => "RIFF\x24\x00\x00\x00WEBPVP8 "u8.ToArray();

    private string CreateImage(string relativeName, byte[] content)
    {
        var full = Path.Combine(_tempDir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    [Fact]
    public void ReadFile_SupportsBomRelativeJapaneseSpacesDuplicatesAndOrder()
    {
        var imgA = CreateImage("画像 A.png", MakePngHeader());
        var imgB = CreateImage(Path.Combine("sub", "photo B.jpg"), MakeJpegHeader());
        var imgC = CreateImage("art.webp", MakeWebpHeader());
        var listPath = Path.Combine(_tempDir, "playlist.txt");

        var content = "\uFEFF画像 A.png\r\n\r\nsub\\photo B.jpg\n画像 A.png\nart.webp\n";
        File.WriteAllBytes(listPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(content));

        var (fullPath, rows, sha256) = PlaylistDocument.ReadFile(listPath);

        Assert.Equal(Path.GetFullPath(listPath), fullPath);
        Assert.Equal(64, sha256.Length);
        Assert.Equal(4, rows.Count);
        Assert.Equal(["画像 A.png", "sub\\photo B.jpg", "画像 A.png", "art.webp"], rows.Select(r => r.RawPath));
        Assert.Equal([imgA, imgB, imgA, imgC], rows.Select(r => r.ResolvedPath));
        Assert.Equal([1, 2, 3, 4], rows.Select(r => r.OrderNumber));
        Assert.All(rows, r => Assert.True(r.IsValid));
        Assert.NotEqual(rows[0].Id, rows[2].Id);
    }

    [Fact]
    public void ReadFile_MarksMissingUnsupportedAndCorruptImagesInvalid()
    {
        CreateImage("fake.png", "not a png"u8.ToArray());
        CreateImage("pic.bmp", "BM123456"u8.ToArray());
        var listPath = Path.Combine(_tempDir, "bad.txt");
        File.WriteAllText(listPath, "missing.png\nfake.png\npic.bmp\n", new UTF8Encoding(false));

        var (_, rows, _) = PlaylistDocument.ReadFile(listPath);

        Assert.Equal(3, rows.Count);
        Assert.False(rows[0].IsValid);
        Assert.False(rows[1].IsValid);
        Assert.False(rows[2].IsValid);
        Assert.Contains("1 行目", PlaylistDocument.ValidateForApply(rows, 10, listPath));
    }

    [Fact]
    public void WriteFileAtomic_And_RebaseRowsForSaveAs_PreserveRelativeSemantics()
    {
        var dirA = Path.Combine(_tempDir, "folderA");
        var dirB = Path.Combine(_tempDir, "folderB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var img = CreateImage(Path.Combine("shared", "日本語 画像.png"), MakePngHeader());
        var listA = Path.Combine(dirA, "a.txt");
        var listB = Path.Combine(dirB, "b.txt");
        File.WriteAllText(listA, "..\\shared\\日本語 画像.png\n", new UTF8Encoding(false));

        var (_, rowsA, _) = PlaylistDocument.ReadFile(listA);
        Assert.True(rowsA[0].IsValid);
        Assert.Equal(img, rowsA[0].ResolvedPath);

        // Save in same directory preserves raw relative string verbatim
        var listASameDir = Path.Combine(dirA, "a2.txt");
        var rebasedSame = PlaylistDocument.RebaseRowsForSaveAs(rowsA, listA, listASameDir);
        Assert.Equal("..\\shared\\日本語 画像.png", rebasedSame[0].RawPath);

        // Save into another directory rebases relative path so it still resolves to img
        var rebasedOther = PlaylistDocument.RebaseRowsForSaveAs(rowsA, listA, listB);
        var (writtenB, shaB) = PlaylistDocument.WriteFileAtomic(listB, rebasedOther);
        Assert.Equal(64, shaB.Length);

        var (_, rowsB, _) = PlaylistDocument.ReadFile(writtenB);
        Assert.True(rowsB[0].IsValid);
        Assert.Equal(img, rowsB[0].ResolvedPath);
    }

    [Fact]
    public void ValidateForSave_And_WriteFileAtomic_RejectEmptyPathRowsInsteadOfOmitting()
    {
        var listPath = Path.Combine(_tempDir, "with_empty.txt");
        var img = CreateImage("ok.png", MakePngHeader());
        var rows = new[]
        {
            PlaylistDocument.CreateRow(1, img, _tempDir),
            PlaylistDocument.CreateRow(2, "   ", _tempDir),
        };

        var err = PlaylistDocument.ValidateForSave(rows);
        Assert.NotNull(err);
        Assert.Contains("2 行目", err);
        Assert.Throws<InvalidOperationException>(() => PlaylistDocument.WriteFileAtomic(listPath, rows));
        Assert.False(File.Exists(listPath));
    }

    [Fact]
    public void WriteFileAtomic_IsReadableByPythonPlaylistReader()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
            return;

        // Build a minimal real 2x2 PNG using Python Pillow and write a playlist via PlaylistDocument
        var img = Path.Combine(_tempDir, "画像 スペース.png");
        var listPath = Path.Combine(_tempDir, "saved_by_csharp.txt");

        var makePng = StartUv(repoRoot, "run", "--with", "pillow==12.0.0", "python", "-c",
            $"from PIL import Image; Image.new('RGB',(2,2),(10,20,30)).save(r'{img}', format='PNG')");
        Assert.Equal(0, makePng.ExitCode);

        var row1 = PlaylistDocument.CreateRow(1, Path.GetFileName(img), _tempDir);
        var row2 = PlaylistDocument.CreateRow(2, img, _tempDir);
        Assert.True(row1.IsValid);
        Assert.True(row2.IsValid);

        PlaylistDocument.WriteFileAtomic(listPath, [row1, row2]);

        var readPy = StartUv(repoRoot, "run", "--with", "pillow==12.0.0", "python", "-c",
            $"import json; from pathlib import Path; from lookgo.playlist import read_playlist_entries; " +
            $"entries = read_playlist_entries(Path(r'{listPath}')); " +
            $"print(json.dumps([[str(e.path), e.line] for e in entries], ensure_ascii=False))");
        Assert.Equal(0, readPy.ExitCode);
        Assert.Contains("画像 スペース.png", readPy.Stdout);
    }

    private static (int ExitCode, string Stdout) StartUv(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "uv",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUTF8"] = "1";
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "lookgo.py")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
