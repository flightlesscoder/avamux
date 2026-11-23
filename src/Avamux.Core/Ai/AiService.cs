using Avamux.Plugins;

namespace Avamux.Core.Ai;

/// <summary>AI providers and conversations. (Filled in with the provider layer.)</summary>
public sealed class AiService(AppServices app) : IAiService, IAsyncDisposable
{
    public IReadOnlyList<AiProviderInfo> Providers => [];
    public string? DefaultProviderId => null;
    public Task<AiRunResult> RunAsync(AiRunRequest request, Action<AiEvent>? onEvent = null, CancellationToken ct = default) =>
        Task.FromResult(new AiRunResult { Ok = false, Error = "No AI provider is set up." });
    public Task<string> OpenChatAsync(AiChatRequest request) => throw new NotSupportedException();
    public ValueTask DisposeAsync() { _ = app; return ValueTask.CompletedTask; }
}
