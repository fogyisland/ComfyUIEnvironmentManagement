namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.15: 区分 catalog refresh 的哪个 stage 撞了 GitHub rate limit。
/// Version = 拉取节点版本（GitHubVersionService.FetchVersionsAsync）；
/// Metadata = 拉取 catalog metadata（GitHubCatalogMetadataService.EnrichAsync）。
/// 两个 stage 共享 GitHub 同 rate limit bucket，但分开记录让 UI 精确提示
/// "跳过版本" / "跳过 metadata"；后续如分开 quota 可独立 reset time。
/// </summary>
public enum RateLimitStage
{
    Version,
    Metadata,
}