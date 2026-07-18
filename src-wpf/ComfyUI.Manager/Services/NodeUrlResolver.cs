namespace ComfyUI.Manager.Services;

/// <summary>
/// NodeUrlResolver: 把下载源模板 URL 里的 <c>{node}</c> 占位替换成实际 node id。
/// 纯静态函数,无副作用,易于单测。
///
/// 规则:
/// - 空 / 空白 templateUrl → 原样返回
/// - 包含 <c>{node}</c> → 全部替换为 nodeId
/// - 不包含 <c>{node}</c> → 原样返回(用户填了固定 URL)
/// </summary>
public static class NodeUrlResolver
{
    public static string Resolve(string templateUrl, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(templateUrl)) return templateUrl;
        return templateUrl.Replace("{node}", nodeId);
    }
}