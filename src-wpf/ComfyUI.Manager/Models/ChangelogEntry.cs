using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.11+ dashboard/splash polish:CHANGELOG.md 解析后的单条 version entry。
/// Version 不带 'v' prefix 也行(ChangelogParser 接受两种)。
/// BulletPoints 是 markdown '- xxx' 的列表(嵌套扁平化)。
/// </summary>
public sealed record ChangelogEntry(
    string Version,
    DateTime? Date,
    IReadOnlyList<string> BulletPoints);
