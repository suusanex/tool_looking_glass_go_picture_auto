using LookGo.Core.Models;

namespace LookGo.Core.Backend;

public sealed record BackendResult(bool Ok, PlaybackState? State = null, string? Error = null);

public interface IBackendClient : IAsyncDisposable
{
    Task<BackendResult> GetStatusAsync(CancellationToken ct = default);

    Task<BackendResult> ShowAsync(
        string listPath,
        double intervalSeconds,
        string operationId,
        string? expectedSha256 = null,
        CancellationToken ct = default);

    Task<BackendResult> CancelShowAsync(string operationId, CancellationToken ct = default);
    Task<BackendResult> NextAsync(CancellationToken ct = default);
    Task<BackendResult> PrevAsync(CancellationToken ct = default);
    Task<BackendResult> PauseAsync(CancellationToken ct = default);
    Task<BackendResult> ResumeAsync(CancellationToken ct = default);
    Task<BackendResult> JumpAsync(int index, CancellationToken ct = default);
    Task<BackendResult> StopAsync(CancellationToken ct = default);
}
