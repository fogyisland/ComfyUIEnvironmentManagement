using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Chains 4 IModelMatcher strategies in order
/// [Hash → SafetensorsMetadata → CompanionJson → FilenameFuzzy].
/// First non-null MatchResult wins. Returns null if all strategies fail.
/// Logs strategy outcome via <c>"civitai-matcher"</c> subsystem
/// (the ONLY place that logs outcome — matchers themselves only log diagnostics).</summary>
public sealed class CivitaiMatcherOrchestrator
{
    private const string LogSubsystem = "civitai-matcher";

    private readonly IReadOnlyList<IModelMatcher> _matchers;
    private readonly AppLogger? _logger;

    /// <summary>Primary ctor used by <see cref="CivitAiLookupService"/> with concrete
    /// matcher types. Chains in spec §3.2 order: Hash → SafetensorsMetadata → CompanionJson → FilenameFuzzy.</summary>
    public CivitaiMatcherOrchestrator(
        CivitaiHashMatcher hash,
        SafetensorsMetadataMatcher metadata,
        CompanionJsonMatcher companion,
        FilenameMatcher filename,
        AppLogger? logger = null)
        : this(new IModelMatcher[] { hash, metadata, companion, filename }, logger) { }

    /// <summary>Secondary ctor — accepts any <see cref="IModelMatcher"/> sequence.
    /// Used by tests to inject <c>Mock&lt;IModelMatcher&gt;</c> without standing up
    /// real <see cref="CivitAiLookupService"/> instances.</summary>
    public CivitaiMatcherOrchestrator(IReadOnlyList<IModelMatcher> matchers, AppLogger? logger = null)
    {
        _matchers = matchers;
        _logger = logger;
    }

    public async Task<MatchResult?> MatchAsync(DownloadedModel model, CancellationToken ct)
    {
        foreach (var matcher in _matchers)
        {
            try
            {
                var result = await matcher.MatchAsync(model, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    _logger?.Info(LogSubsystem, $"✓ matched via {matcher.Name}");
                    return result;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Warn(LogSubsystem,
                    $"✗ {matcher.Name} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        _logger?.Info(LogSubsystem, "✗ no match (all strategies failed)");
        return null;
    }
}