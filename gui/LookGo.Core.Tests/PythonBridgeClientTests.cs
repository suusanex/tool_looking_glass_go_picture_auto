using System.Security.Cryptography;
using System.Text;
using LookGo.Core.Backend;
using LookGo.Core.Models;
using LookGo.Core.Playlist;
using Xunit;

namespace LookGo.Core.Tests;

public sealed class PythonBridgeClientTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _localAppData;

    public PythonBridgeClientTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lookgo-bridge-int-{Guid.NewGuid():N}");
        _localAppData = Path.Combine(_tempDir, "localappdata");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_localAppData);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task EndToEnd_BridgeWithDisplayWorker_PlaybackEditApplyReplaceAndFailureIsolation()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        // Create real 4x4 test PNG images via Python PIL
        var imgA = Path.Combine(_tempDir, "画像 A.png");
        var imgB = Path.Combine(_tempDir, "画像 B.png");
        var imgC = Path.Combine(_tempDir, "画像 C.png");
        var imgD = Path.Combine(_tempDir, "画像 D.png");
        var imgE = Path.Combine(_tempDir, "画像 E.png");
        var badImg = Path.Combine(_tempDir, "broken.png");
        File.WriteAllBytes(badImg, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);

        var listAbc = Path.Combine(_tempDir, "abc.txt");
        var listDe = Path.Combine(_tempDir, "de.txt");
        var listBad = Path.Combine(_tempDir, "bad.txt");

        // Harness script runs real DisplayWorker (with FakeRenderer + cached RGBD bypass) + real gui_bridge
        var harnessPath = Path.Combine(_tempDir, "bridge_harness.py");
        var logFile = Path.Combine(_tempDir, "presented.log");
        File.WriteAllText(harnessPath, """
import hashlib
import os
from pathlib import Path
import sys
import threading

from PIL import Image

from lookgo import bridge as bridge_module, gui_bridge, worker as worker_module


def make_png(path: str, color: tuple[int, int, int]) -> None:
    Image.new("RGB", (4, 4), color).save(path, format="PNG")


class FakeBridge:
    def __init__(self, record_path: Path):
        self.record_path = record_path
        self.playback_log_offset = None

    def connect(self):
        return type("Dev", (), {"aspect": 1.0, "index": 0, "hwid": "FAKE-GO", "rows": 6, "columns": 11, "view_count": 66})()

    def health_check(self):
        pass

    def _record(self, line: str) -> None:
        with self.record_path.open("a", encoding="utf-8") as f:
            f.write(line + "\n")

    def show_playlist(self, paths, interval_ms: int):
        self._record(f"show_playlist|{','.join(p.name for p in paths)}|{interval_ms}")
        self.playback_log_offset = None
        return "fake-playlist"

    def pause_playback(self):
        self._record("pause")

    def resume_playback(self):
        self._record("resume")

    def seek_to_index(self, index: int, *, pause_after: bool = False):
        self._record(f"seek|{index}|{pause_after}")
        self.playback_log_offset = None

    def step_next(self, *, pause_after: bool = False):
        self._record(f"step_next|{pause_after}")

    def step_previous(self, *, pause_after: bool = False):
        self._record(f"step_prev|{pause_after}")

    def close(self):
        pass


if sys.argv[1] == "prep-images":
    colors = [(10, 0, 0), (0, 20, 0), (0, 0, 30), (40, 40, 0), (0, 50, 50)]
    for path, color in zip(sys.argv[2:], colors):
        make_png(path, color)
    raise SystemExit(0)

# Run in-process fake worker thread backed by real DisplayWorker + real gui_bridge
bridge_log = Path(os.environ["LOOKGO_TEST_LOG"]).with_name("bridge.log")
bridge_log.write_bytes(b"")
bridge_module._current_bridge_log = lambda: bridge_log

worker = worker_module.DisplayWorker()
worker.estimator = object()
worker_module.RestBridge = lambda: FakeBridge(Path(os.environ["LOOKGO_TEST_LOG"]))
worker_module.convert_to_rgbd = lambda image_bytes, *_args: image_bytes

t = threading.Thread(target=worker.run, daemon=True)
t.start()
worker_module.ensure_worker(timeout=10)
raise SystemExit(gui_bridge.gui_bridge_entry())
""", new UTF8Encoding(false));

        // 1) Prepare test images
        var realLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var uvCacheDir = Path.Combine(realLocalAppData, "uv", "cache");
        var uvPythonDir = Path.Combine(realLocalAppData, "uv", "python");

        var prepPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "uv",
            WorkingDirectory = repoRoot!,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "run", "--with", "pillow==12.0.0", "--with", "filelock==3.20.0", "--with", "numpy==2.2.6",
            "python", harnessPath, "prep-images", imgA, imgB, imgC, imgD, imgE
        })
        {
            prepPsi.ArgumentList.Add(a);
        }
        prepPsi.Environment["LOCALAPPDATA"] = _localAppData;
        prepPsi.Environment["UV_CACHE_DIR"] = uvCacheDir;
        prepPsi.Environment["UV_PYTHON_INSTALL_DIR"] = uvPythonDir;
        prepPsi.Environment["PYTHONUTF8"] = "1";
        prepPsi.Environment["PYTHONPATH"] = repoRoot!;
        using (var p = System.Diagnostics.Process.Start(prepPsi)!)
        {
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, err);
        }

        // Start real bridge + DisplayWorker harness
        var pipeSeed = Guid.NewGuid().ToString("N");
        var env = new Dictionary<string, string>
        {
            ["LOCALAPPDATA"] = _localAppData,
            ["UV_CACHE_DIR"] = uvCacheDir,
            ["UV_PYTHON_INSTALL_DIR"] = uvPythonDir,
            ["USERNAME"] = $"lookgo_test_{pipeSeed}",
            ["LOOKGO_TEST_LOG"] = logFile,
            ["PYTHONPATH"] = repoRoot!,
        };
        await using var client = new PythonBridgeClient(
            repoRoot!,
            executable: "uv",
            baseArguments:
            [
                "run", "--with", "pillow==12.0.0", "--with", "filelock==3.20.0", "--with", "numpy==2.2.6",
                "python", harnessPath, "serve"
            ],
            extraEnvironment: env);

        // 2) Show A/B/C
        var rowsAbc = new[]
        {
            PlaylistDocument.CreateRow(1, imgA, _tempDir),
            PlaylistDocument.CreateRow(2, imgB, _tempDir),
            PlaylistDocument.CreateRow(3, imgC, _tempDir),
        };
        var (_, shaAbc) = PlaylistDocument.WriteFileAtomic(listAbc, rowsAbc);
        var showAbc = await client.ShowAsync(listAbc, 15, "op-abc", shaAbc);
        Assert.True(showAbc.Ok, showAbc.Error);
        Assert.Equal([imgA, imgB, imgC], showAbc.State!.Images);
        Assert.Equal(0, showAbc.State.Index);
        Assert.False(showAbc.State.Paused);
        Assert.Equal("op-abc", showAbc.State.ActiveOperationId);

        // 3) Next, Prev, Pause, Jump (stays paused), Resume
        var nextRes = await client.NextAsync();
        Assert.True(nextRes.Ok);
        Assert.Equal(1, nextRes.State!.Index);

        var pauseRes = await client.PauseAsync();
        Assert.True(pauseRes.Ok);
        Assert.True(pauseRes.State!.Paused);

        var jumpRes = await client.JumpAsync(2);
        Assert.True(jumpRes.Ok);
        Assert.Equal(2, jumpRes.State!.Index);
        Assert.True(jumpRes.State.Paused);
        Assert.Equal("op-abc", jumpRes.State.ActiveOperationId);

        var prevRes = await client.PrevAsync();
        Assert.True(prevRes.Ok);
        Assert.Equal(1, prevRes.State!.Index);
        Assert.True(prevRes.State.Paused);

        var resumeRes = await client.ResumeAsync();
        Assert.True(resumeRes.Ok);
        Assert.False(resumeRes.State!.Paused);

        // 4) Edit in-memory & save D/E to another file -> active playback stays A/B/C
        var editor = PlaylistEditorReducer.LoadFromFile(
            PlaylistEditorReducer.CreateNew(),
            listAbc,
            rowsAbc,
            shaAbc);
        editor = PlaylistEditorReducer.AddPaths(editor, [imgD, imgE]);
        editor = PlaylistEditorReducer.DeleteRow(editor, editor.Rows[0].Id);
        editor = PlaylistEditorReducer.DeleteRow(editor, editor.Rows[0].Id);
        editor = PlaylistEditorReducer.DeleteRow(editor, editor.Rows[0].Id);
        var (_, shaDe) = PlaylistDocument.WriteFileAtomic(listDe, editor.Rows);
        editor = PlaylistEditorReducer.MarkSaved(editor, listDe, shaDe, editor.Rows);

        var beforeApplyStatus = await client.GetStatusAsync();
        Assert.True(beforeApplyStatus.Ok);
        Assert.Equal([imgA, imgB, imgC], beforeApplyStatus.State!.Images);
        Assert.Equal("op-abc", beforeApplyStatus.State.ActiveOperationId);

        // 5) Apply tampered expected_sha256 -> rejected without changing active playback
        var badHashResult = await client.ShowAsync(listDe, 8, "op-bad-sha", new string('0', 64));
        Assert.False(badHashResult.Ok);
        var afterBadHash = await client.GetStatusAsync();
        Assert.Equal([imgA, imgB, imgC], afterBadHash.State!.Images);
        Assert.Equal("op-abc", afterBadHash.State.ActiveOperationId);

        // 6) Apply invalid image list -> rejected, old playback preserved
        File.WriteAllText(listBad, badImg + "\n", new UTF8Encoding(false));
        var badBytes = File.ReadAllBytes(listBad);
        var badSha = Convert.ToHexStringLower(SHA256.HashData(badBytes));
        var badShow = await client.ShowAsync(listBad, 8, "op-bad-img", badSha);
        Assert.False(badShow.Ok);
        var afterBadShow = await client.GetStatusAsync();
        Assert.Equal([imgA, imgB, imgC], afterBadShow.State!.Images);
        Assert.Equal("op-abc", afterBadShow.State.ActiveOperationId);

        // 7) Apply D/E cleanly -> complete replacement
        editor = PlaylistEditorReducer.SetInterval(editor, 8);
        var snapDe = new ApplySnapshot("op-de", editor.EditRevision, listDe, shaDe, 8);
        editor = PlaylistEditorReducer.BeginApply(editor, snapDe);
        var showDe = await client.ShowAsync(listDe, 8, "op-de", shaDe);
        Assert.True(showDe.Ok, showDe.Error);
        editor = PlaylistEditorReducer.CompleteApplySuccess(editor, "op-de", showDe.State!);

        Assert.Equal([imgD, imgE], showDe.State!.Images);
        Assert.Equal(0, showDe.State.Index);
        Assert.Equal("op-de", showDe.State.ActiveOperationId);
        Assert.Equal("表示へ反映済み", editor.GetStatusLabel(showDe.State));

        // 8) Stop cleanly and confirm single-playlist lifecycle + transport controls
        var stopRes = await client.StopAsync();
        Assert.True(stopRes.Ok);
        Assert.False(stopRes.State!.WorkerRunning);
        Assert.Equal("stopped", stopRes.State.Status);
        Assert.False(editor.IsDisplayedMatch(stopRes.State));

        var bridgeLogLines = File.ReadAllLines(logFile, Encoding.UTF8);
        Assert.Equal(2, bridgeLogLines.Count(l => l.StartsWith("show_playlist|", StringComparison.Ordinal)));
        Assert.Contains("seek|1|False", bridgeLogLines);
        Assert.Contains("pause", bridgeLogLines);
        Assert.Contains("seek|2|True", bridgeLogLines);
        Assert.Contains("seek|1|True", bridgeLogLines);
        Assert.Contains("resume", bridgeLogLines);
    }

    [Fact]
    public void PlaybackState_FromJson_ParsesLastStatusErrorAndNullIndexWithImages()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
        {
          "status": "stopped",
          "last_status": "error",
          "images": ["C:\\img\\a.png", "C:\\img\\b.png"],
          "index": null,
          "worker_running": false,
          "last_error": "play_playlist failed"
        }
        """);
        var stoppedError = PlaybackState.FromJson(doc.RootElement);
        Assert.Equal("stopped", stoppedError.Status);
        Assert.Equal("error", stoppedError.LastStatus);
        Assert.Null(stoppedError.Index);
        Assert.Equal(2, stoppedError.Images.Count);
        Assert.True(stoppedError.IsErrorEnded);

        using var trackingLostDoc = System.Text.Json.JsonDocument.Parse("""
        {
          "status": "playing",
          "images": ["C:\\img\\a.png", "C:\\img\\b.png"],
          "index": null,
          "worker_running": true
        }
        """);
        var trackingLost = PlaybackState.FromJson(trackingLostDoc.RootElement);
        Assert.True(trackingLost.WorkerRunning);
        Assert.Null(trackingLost.Index);
        Assert.Equal(2, trackingLost.Images.Count);
        Assert.False(trackingLost.IsErrorEnded);
    }

    [Fact]
    public async Task RealLookgoBridgeEntry_ReportsStoppedStatusCleanlyWhenNoWorkerIsRunning()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var realLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var env = new Dictionary<string, string>
        {
            ["LOCALAPPDATA"] = _localAppData,
            ["UV_CACHE_DIR"] = Path.Combine(realLocalAppData, "uv", "cache"),
            ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(realLocalAppData, "uv", "python"),
            ["USERNAME"] = $"lookgo_cli_{Guid.NewGuid():N}",
        };
        await using var client = new PythonBridgeClient(
            repoRoot!,
            executable: "uv",
            baseArguments: ["run", "lookgo.py", "_bridge"],
            extraEnvironment: env);

        var status = await client.GetStatusAsync();
        Assert.True(status.Ok, status.Error);
        Assert.NotNull(status.State);
        Assert.False(status.State!.WorkerRunning);
        Assert.Equal("stopped", status.State.Status);
        Assert.Empty(status.State.Images);
        Assert.Null(status.State.Index);
    }

    [Fact]
    public async Task HungBridgeStartup_KillsAndDisposesOrphanedChildProcessBeforeReturningError()
    {
        var pidFile = Path.Combine(_tempDir, "hung.pid");
        var hungScript = Path.Combine(_tempDir, "hung_bridge.py");
        File.WriteAllText(hungScript, """
import os
from pathlib import Path
import sys
import time

Path(sys.argv[1]).write_text(str(os.getpid()), encoding="utf-8")
time.sleep(60)
""", new UTF8Encoding(false));

        await using var client = new PythonBridgeClient(
            _tempDir,
            executable: "uv",
            baseArguments: ["run", "--no-sync", "--python", "3.12", "python", hungScript, pidFile],
            readyTimeout: TimeSpan.FromMilliseconds(1500));

        var result = await client.GetStatusAsync();
        Assert.False(result.Ok);
        Assert.Contains("Timed out", result.Error ?? "");

        Assert.True(File.Exists(pidFile));
        var pid = int.Parse(File.ReadAllText(pidFile, Encoding.UTF8).Trim());
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                if (proc.HasExited)
                    break;
            }
            catch (ArgumentException)
            {
                break;
            }
            await Task.Delay(50);
        }

        var stillAlive = false;
        try
        {
            using var check = System.Diagnostics.Process.GetProcessById(pid);
            stillAlive = !check.HasExited;
        }
        catch (ArgumentException)
        {
            stillAlive = false;
        }
        Assert.False(stillAlive);
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
