using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// NodeInstallDiffService 产出:全部分类 + Warnings 子集(Downgrade + Conflict)。
/// </summary>
public sealed class NodeInstallDiffReport
{
    public IReadOnlyList<DiffEntry> Entries { get; }

    public NodeInstallDiffReport(IReadOnlyList<DiffEntry> entries)
    {
        Entries = entries;
    }

    /// <summary>Downgrade + Conflict 子集 — UI 警告 modal 只看这个。</summary>
    public IReadOnlyList<DiffEntry> Warnings
    {
        get
        {
            var list = new List<DiffEntry>();
            foreach (var e in Entries)
            {
                if (e.Category is DiffCategory.Downgrade or DiffCategory.Conflict)
                    list.Add(e);
            }
            return list;
        }
    }

    public static NodeInstallDiffReport Empty { get; } =
        new(System.Array.Empty<DiffEntry>());
}