using System.Text.Json;

namespace LookGo.Core.Models;

public sealed record PlaybackState
{
    public static PlaybackState Empty { get; } = new();

    public string Status { get; init; } = "stopped";
    public string? LastStatus { get; init; }
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();
    public int? Index { get; init; }
    public bool Busy { get; init; }
    public bool Paused { get; init; }
    public double Interval { get; init; } = 10.0;
    public string? ListPath { get; init; }
    public string? ActiveOperationId { get; init; }
    public string? ActiveListSha256 { get; init; }
    public string? LastError { get; init; }
    public bool WorkerRunning { get; init; }
    public string? LogPath { get; init; }

    public bool IsErrorEnded =>
        !WorkerRunning &&
        (string.Equals(Status, "error", StringComparison.Ordinal) ||
         string.Equals(LastStatus, "error", StringComparison.Ordinal));

    public static PlaybackState FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return Empty;

        var images = new List<string>();
        if (element.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in imagesEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } path)
                    images.Add(path);
            }
        }

        int? index = null;
        if (element.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number && idxEl.TryGetInt32(out var idxVal))
            index = idxVal;

        double interval = 10.0;
        if (element.TryGetProperty("interval", out var intEl) && intEl.ValueKind == JsonValueKind.Number && intEl.TryGetDouble(out var intVal) && double.IsFinite(intVal) && intVal > 0)
            interval = intVal;

        return new PlaybackState
        {
            Status = GetString(element, "status") ?? "stopped",
            LastStatus = GetString(element, "last_status"),
            Images = images,
            Index = index,
            Busy = GetBool(element, "busy"),
            Paused = GetBool(element, "paused"),
            Interval = interval,
            ListPath = GetString(element, "list_path"),
            ActiveOperationId = GetString(element, "active_operation_id"),
            ActiveListSha256 = GetString(element, "active_list_sha256"),
            LastError = GetString(element, "last_error"),
            WorkerRunning = GetBool(element, "worker_running"),
            LogPath = GetString(element, "log"),
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.True;
    }
}
