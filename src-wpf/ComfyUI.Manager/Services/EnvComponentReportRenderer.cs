using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// EnvComponentReportRenderer:把 <see cref="EnvComponentReport"/> 渲染成
/// self-contained HTML(单文件,内联 CSS,UTF-8,无外部资源)。
///
/// v0.6.7 引入 — T2 UI 集成生成 .html 文件后用本机默认浏览器打开。
///
/// 安全:
/// - 所有 user-controlled 字段(env name / paths / versions / commit hashes /
///   error messages)走 <see cref="WebUtility.HtmlEncode"/>,防 XSS / 注入。
/// - 不做 URL 拼接;commit hash 仅显示不链 GitHub。
///
/// 渲染结构(6 阶段 + 顶部警告 + 报告头):
/// 1. Report header:env 名 + UTC + 本地时间 + App 版本
/// 2. SectionWarnings(顶部 banner)
/// 3. 阶段 1 — Required BED (规范要求)
/// 4. 阶段 2 — Actual 关键包对比 (实际安装 vs 规范)
/// 5. 阶段 3 — 完整 pip list (N 个包)
/// 6. 阶段 4 — ComfyUI 源码状态
/// 7. 阶段 5 — Custom Nodes (N 个)
/// 8. 阶段 6 — Env 元数据
/// </summary>
public static class EnvComponentReportRenderer
{
    /// <summary>
    /// 主入口:把 <paramref name="report"/> 渲染成单文件 HTML 字符串。
    /// 调用方负责写盘(UTF-8 encoding,无 BOM)。
    /// </summary>
    public static string Render(EnvComponentReport report)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));

        var sb = new StringBuilder(8192);
        AppendHtmlHead(sb, report);
        AppendBody(sb, report);
        AppendHtmlTail(sb);
        return sb.ToString();
    }

    private static void AppendHtmlHead(StringBuilder sb, EnvComponentReport report)
    {
        var title = "组件报告 — " + report.EnvName;
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).AppendLine("</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(InlineCss);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
    }

    private static void AppendHtmlTail(StringBuilder sb)
    {
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
    }

    private static void AppendBody(StringBuilder sb, EnvComponentReport report)
    {
        // 报告头
        sb.AppendLine("<header class=\"report-header\">");
        sb.Append("<h1>组件报告 — ").Append(WebUtility.HtmlEncode(report.EnvName)).AppendLine("</h1>");
        sb.Append("<div class=\"meta-line\">生成时间(UTC): ")
          .Append(WebUtility.HtmlEncode(FormatUtc(report.GeneratedAtUtc)))
          .Append("</div>");
        var localTime = report.GeneratedAtUtc.ToLocalTime();
        sb.Append("<div class=\"meta-line\">生成时间(本地): ")
          .Append(WebUtility.HtmlEncode(localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
          .Append("</div>");
        sb.Append("<div class=\"meta-line\">App 版本: ")
          .Append(WebUtility.HtmlEncode(string.IsNullOrEmpty(report.AppVersion) ? "?" : report.AppVersion))
          .Append("</div>");
        sb.AppendLine("</header>");

        // 警告 banner
        if (report.SectionWarnings.Count > 0)
        {
            sb.AppendLine("<section class=\"warnings\">");
            sb.AppendLine("<h2>采集警告</h2>");
            sb.AppendLine("<ul>");
            foreach (var w in report.SectionWarnings)
            {
                sb.Append("<li>").Append(WebUtility.HtmlEncode(w)).AppendLine("</li>");
            }
            sb.AppendLine("</ul>");
            sb.AppendLine("</section>");
        }

        AppendSection1Required(sb, report);
        AppendSection2KeyPackages(sb, report);
        AppendSection3FullPipList(sb, report);
        AppendSection4Comfyui(sb, report);
        AppendSection5CustomNodes(sb, report);
        AppendSection6Metadata(sb, report);
    }

    private static void AppendSection1Required(StringBuilder sb, EnvComponentReport report)
    {
        sb.AppendLine("<section class=\"stage stage-required\">");
        sb.AppendLine("<h2>阶段 1 — Required BED (规范要求)</h2>");
        if (report.Required is null)
        {
            sb.AppendLine("<p class=\"skip-notice\">本 env 未指定 BedProfileId(<code>BedProfileId</code> 为空),跳过对比。</p>");
            sb.AppendLine("</section>");
            return;
        }
        var r = report.Required;
        sb.Append("<table class=\"info-table\">");
        sb.Append("<tr><th>Profile ID</th><td>")
          .Append(WebUtility.HtmlEncode(r.ProfileId)).AppendLine("</td></tr>");
        sb.Append("<tr><th>Torch 版本</th><td>")
          .Append(WebUtility.HtmlEncode(r.TorchVersion)).AppendLine("</td></tr>");
        sb.Append("<tr><th>CUDA</th><td>")
          .Append(WebUtility.HtmlEncode(r.CudaVersion))
          .Append(" (")
          .Append(WebUtility.HtmlEncode(r.CudaLabel))
          .AppendLine(")</td></tr>");
        sb.Append("<tr><th>Channel</th><td>")
          .Append(WebUtility.HtmlEncode(r.Channel)).AppendLine("</td></tr>");
        sb.Append("<tr><th>指定包列表</th><td>")
          .Append(WebUtility.HtmlEncode(string.Join(", ", r.Packages)))
          .AppendLine("</td></tr>");
        sb.Append("<tr><th>BED 状态</th><td>")
          .Append(WebUtility.HtmlEncode(r.BedStatus ?? "(null)"));
        if (!string.IsNullOrEmpty(r.BedFailedReason))
        {
            sb.Append(" <span class=\"fail-reason\">(失败原因: ")
              .Append(WebUtility.HtmlEncode(r.BedFailedReason))
              .Append(")</span>");
        }
        sb.AppendLine("</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("</section>");
    }

    private static void AppendSection2KeyPackages(StringBuilder sb, EnvComponentReport report)
    {
        sb.AppendLine("<section class=\"stage stage-key-packages\">");
        sb.Append("<h2>阶段 2 — Actual 关键包对比 (实际安装 vs 规范)</h2>");
        if (report.Required is null)
        {
            sb.AppendLine("<p class=\"skip-notice\">跳过对比:未指定 Required BED。</p>");
            sb.AppendLine("</section>");
            return;
        }
        if (report.KeyPackages.Count == 0)
        {
            sb.AppendLine("<p class=\"skip-notice\">跳过对比:Python 解释器未找到或 pip show 失败(见顶部警告)。</p>");
            sb.AppendLine("</section>");
            return;
        }

        sb.AppendLine("<table class=\"comparison-table\">");
        sb.AppendLine("<thead><tr><th>包名</th><th>Required</th><th>Actual</th><th>状态</th></tr></thead>");
        sb.AppendLine("<tbody>");
        foreach (var kp in report.KeyPackages)
        {
            var badge = BadgeFor(kp.Status);
            sb.Append("<tr class=\"row-").Append(badge.CssClass).Append("\">");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(kp.PackageName)).AppendLine("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(kp.RequiredVersion ?? "—")).AppendLine("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(kp.ActualVersion ?? "—")).AppendLine("</td>");
            sb.Append("<td><span class=\"badge ").Append(badge.CssClass).Append("\">")
              .Append(WebUtility.HtmlEncode(badge.Label))
              .AppendLine("</span></td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</section>");
    }

    private static void AppendSection3FullPipList(StringBuilder sb, EnvComponentReport report)
    {
        sb.AppendLine("<section class=\"stage stage-pip-list\">");
        sb.Append("<h2>阶段 3 — 完整 pip list (").Append(report.FullPipList.Count).AppendLine(" 个包)</h2>");
        if (report.FullPipList.Count == 0)
        {
            sb.AppendLine("<p class=\"skip-notice\">跳过:Python 解释器未找到或 pip list 解析失败。</p>");
            sb.AppendLine("</section>");
            return;
        }

        sb.AppendLine("<table class=\"pip-table\">");
        sb.AppendLine("<thead><tr><th>Name</th><th>Version</th></tr></thead>");
        sb.AppendLine("<tbody>");
        foreach (var p in report.FullPipList)
        {
            sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(p.Name)).Append("</td><td>")
              .Append(WebUtility.HtmlEncode(p.Version)).AppendLine("</td></tr>");
        }
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</section>");
    }

    private static void AppendSection4Comfyui(StringBuilder sb, EnvComponentReport report)
    {
        sb.AppendLine("<section class=\"stage stage-comfyui\">");
        sb.AppendLine("<h2>阶段 4 — ComfyUI 源码状态</h2>");
        if (report.ComfyuiStatus is null)
        {
            sb.AppendLine("<p class=\"skip-notice\">跳过:ComfyUI 源码目录未设置(ComfyuiSource=null)或目录不存在。</p>");
            sb.AppendLine("</section>");
            return;
        }
        AppendGitStatusTable(sb, report.ComfyuiStatus);
        sb.AppendLine("</section>");
    }

    private static void AppendSection5CustomNodes(StringBuilder sb, EnvComponentReport report)
    {
        sb.AppendLine("<section class=\"stage stage-custom-nodes\">");
        sb.Append("<h2>阶段 5 — Custom Nodes (").Append(report.CustomNodes.Count).AppendLine(" 个)</h2>");
        if (report.CustomNodes.Count == 0)
        {
            sb.AppendLine("<p class=\"skip-notice\">跳过:Custom Nodes 目录未设置(CustomNodesPath=null)或目录不存在,或目录为空。</p>");
            sb.AppendLine("</section>");
            return;
        }
        sb.AppendLine("<table class=\"git-table\">");
        sb.AppendLine("<thead><tr><th>名称</th><th>状态</th><th>Commit</th><th>Branch</th><th>最后提交(本地)</th><th>路径</th></tr></thead>");
        sb.AppendLine("<tbody>");
        foreach (var node in report.CustomNodes)
        {
            AppendGitStatusRow(sb, node);
        }
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</section>");
    }

    private static void AppendSection6Metadata(StringBuilder sb, EnvComponentReport report)
    {
        sb.AppendLine("<section class=\"stage stage-metadata\">");
        sb.AppendLine("<h2>阶段 6 — Env 元数据</h2>");
        var m = report.Metadata;
        sb.AppendLine("<table class=\"info-table\">");
        AppendInfoRow(sb, "RootPath", m.RootPath);
        AppendInfoRow(sb, "PythonExecutable", m.PythonExecutable);
        AppendInfoRow(sb, "VenvPath", m.VenvPath);
        AppendInfoRow(sb, "Venv 创建时间(本地)", m.VenvCreatedAtUtc is null ? "?" : m.VenvCreatedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        AppendInfoRow(sb, "ComfyuiSource", m.ComfyuiSource);
        AppendInfoRow(sb, "CustomNodesPath", m.CustomNodesPath);
        AppendInfoRow(sb, "Port", m.Port);
        AppendInfoRow(sb, "Status", m.Status);
        sb.AppendLine("</table>");
        sb.AppendLine("</section>");
    }

    private static void AppendGitStatusTable(StringBuilder sb, GitTargetStatus status)
    {
        sb.AppendLine("<table class=\"git-table\">");
        sb.AppendLine("<thead><tr><th>名称</th><th>状态</th><th>Commit</th><th>Branch</th><th>最后提交(本地)</th><th>路径</th></tr></thead>");
        sb.AppendLine("<tbody>");
        AppendGitStatusRow(sb, status);
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
    }

    private static void AppendGitStatusRow(StringBuilder sb, GitTargetStatus status)
    {
        string badgeText;
        string badgeClass;
        switch (status.State)
        {
            case GitTargetState.Ok:
                badgeText = "✓ OK";
                badgeClass = "ok";
                break;
            case GitTargetState.NotARepository:
                badgeText = "⚠ 不是 git 仓库";
                badgeClass = "not-a-repo";
                break;
            case GitTargetState.Error:
                badgeText = "✗ Error";
                badgeClass = "error";
                break;
            default:
                badgeText = "?";
                badgeClass = "unknown";
                break;
        }

        sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(status.DisplayName)).Append("</td>");
        sb.Append("<td><span class=\"badge ").Append(badgeClass).Append("\">")
          .Append(WebUtility.HtmlEncode(badgeText))
          .Append("</span>");
        if (!string.IsNullOrEmpty(status.ErrorMessage))
        {
            sb.Append(" <span class=\"error-detail\">")
              .Append(WebUtility.HtmlEncode(status.ErrorMessage))
              .Append("</span>");
        }
        sb.Append("</td>");
        sb.Append("<td>").Append(WebUtility.HtmlEncode(status.CommitHash ?? "—")).Append("</td>");
        sb.Append("<td>").Append(WebUtility.HtmlEncode(status.Branch ?? "—")).Append("</td>");
        sb.Append("<td>")
          .Append(status.LastCommitTimeUtc is null
              ? "—"
              : WebUtility.HtmlEncode(status.LastCommitTimeUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
          .Append("</td>");
        sb.Append("<td>").Append(WebUtility.HtmlEncode(status.Path)).AppendLine("</td></tr>");
    }

    private static void AppendInfoRow(StringBuilder sb, string key, string? value)
    {
        sb.Append("<tr><th>").Append(WebUtility.HtmlEncode(key)).Append("</th><td>")
          .Append(WebUtility.HtmlEncode(string.IsNullOrEmpty(value) ? "(未设置)" : value))
          .AppendLine("</td></tr>");
    }

    private readonly struct BadgeStyle
    {
        public BadgeStyle(string label, string cssClass) { Label = label; CssClass = cssClass; }
        public string Label { get; }
        public string CssClass { get; }
    }

    private static BadgeStyle BadgeFor(KeyPackageMatchStatus status) => status switch
    {
        KeyPackageMatchStatus.Match => new BadgeStyle("✓ MATCH", "match"),
        KeyPackageMatchStatus.Mismatch => new BadgeStyle("✗ MISMATCH", "mismatch"),
        KeyPackageMatchStatus.Missing => new BadgeStyle("⚠ MISSING", "missing"),
        KeyPackageMatchStatus.ExtraUnpinned => new BadgeStyle("✱ EXTRA", "extra"),
        _ => new BadgeStyle("?", "unknown"),
    };

    private static string FormatUtc(DateTime utc)
    {
        return utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
    }

    // 内联 CSS — self-contained,无外部资源。
    private const string InlineCss = @"
* { box-sizing: border-box; }
body {
  font-family: 'Segoe UI', 'Microsoft YaHei', sans-serif;
  margin: 0;
  padding: 24px;
  background: #f5f5f7;
  color: #1c1c1e;
  line-height: 1.5;
}
.report-header {
  background: #fff;
  border-radius: 8px;
  padding: 16px 24px;
  margin-bottom: 16px;
  box-shadow: 0 1px 3px rgba(0,0,0,0.06);
}
.report-header h1 {
  margin: 0 0 8px 0;
  font-size: 1.6em;
  color: #1c1c1e;
}
.meta-line {
  font-size: 0.9em;
  color: #6c6c70;
}
.warnings {
  background: #fff5e6;
  border-left: 4px solid #ff9500;
  border-radius: 4px;
  padding: 12px 16px;
  margin-bottom: 16px;
}
.warnings h2 {
  margin: 0 0 8px 0;
  font-size: 1.05em;
  color: #b25800;
}
.warnings ul {
  margin: 0;
  padding-left: 20px;
}
.warnings li {
  font-size: 0.9em;
  color: #8a4a00;
}
.stage {
  background: #fff;
  border-radius: 8px;
  padding: 16px 24px;
  margin-bottom: 16px;
  box-shadow: 0 1px 3px rgba(0,0,0,0.06);
}
.stage h2 {
  margin: 0 0 12px 0;
  font-size: 1.2em;
  color: #1c1c1e;
  border-bottom: 1px solid #e5e5ea;
  padding-bottom: 6px;
}
.skip-notice {
  color: #6c6c70;
  font-style: italic;
  margin: 0;
}
.info-table, .comparison-table, .pip-table, .git-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.92em;
}
.info-table th, .info-table td,
.comparison-table th, .comparison-table td,
.pip-table th, .pip-table td,
.git-table th, .git-table td {
  border: 1px solid #e5e5ea;
  padding: 6px 10px;
  text-align: left;
  vertical-align: top;
}
.info-table th, .comparison-table th, .pip-table th, .git-table th {
  background: #f5f5f7;
  font-weight: 600;
  width: 20%;
}
.info-table th { width: 25%; }
code {
  font-family: 'Consolas', 'Courier New', monospace;
  background: #f0f0f3;
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 0.92em;
}
.fail-reason {
  color: #b25800;
  font-size: 0.9em;
}
.badge {
  display: inline-block;
  padding: 2px 8px;
  border-radius: 12px;
  font-size: 0.85em;
  font-weight: 600;
  line-height: 1.4;
}
.badge.match { background: #d4f4dd; color: #1d6f3a; }
.badge.mismatch { background: #ffd6d6; color: #a02323; }
.badge.missing { background: #ffe8c2; color: #8a5800; }
.badge.extra { background: #e0e0e8; color: #4a4a55; }
.badge.ok { background: #d4f4dd; color: #1d6f3a; }
.badge.not-a-repo { background: #ffe8c2; color: #8a5800; }
.badge.error { background: #ffd6d6; color: #a02323; }
.badge.unknown { background: #e0e0e8; color: #4a4a55; }
.error-detail {
  color: #a02323;
  font-size: 0.85em;
  margin-left: 6px;
}
tr.row-mismatch { background: #fff8f8; }
tr.row-missing { background: #fffaf0; }
tr.row-match { background: #fff; }
";
}
