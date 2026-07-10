using System.Text;

namespace ComfyUI.Manager.Tools.Ts2Resx;

public static class KeyDeriver
{
    /// <summary>
    /// 推导 WPF resource key。
    /// 普通:<ContextName>_<SanitizedSource>
    /// 有 extra-context:<ContextName>_<SanitizedSource>__<ExtraContext>
    /// 复数:numerus=yes 时追加 _N_One / _N_Other
    /// </summary>
    public static string Derive(string context, string source,
        string? extraContext, bool isPlural)
    {
        var ctx = Sanitize(context);
        var src = Sanitize(source);
        var key = $"{ctx}_{src}";
        if (!string.IsNullOrEmpty(extraContext))
            key += "__" + Sanitize(extraContext);
        if (isPlural)
            key += "_N";  // 由 ResxEmitter 决定 _One / _Other
        return key;
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == '_' || c == '-') sb.Append(c);
            else if (c == ' ') sb.Append('_');
            // 其他字符丢弃
        }
        return sb.ToString();
    }
}