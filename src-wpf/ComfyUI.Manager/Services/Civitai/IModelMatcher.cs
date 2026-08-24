using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Strategy interface for matching a local model to a CivitAI entry.
/// Matchers never throw — return null on any failure (network, parse, missing data).
/// First non-null MatchResult from the orchestrator chain wins.
/// </summary>
public interface IModelMatcher
{
    /// <summary>Human-readable name shown in logs (e.g. "Hash", "SafetensorsMetadata", "CompanionJson", "FilenameFuzzy")。</summary>
    string Name { get; }

    /// <summary>Try to match the given <paramref name="model"/> against the source for this matcher.
    /// Returns null if no match is possible (missing input, network error, parse error, 404, etc.).
    /// OperationCanceledException is the only exception propagated — caller cancels the whole scan.</summary>
    Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct);
}
