using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace ComfyUI.Manager.Tools.Ts2Resx;

public record TsMessage(
    string Context,
    string Source,
    string? ExtraContext,
    string Translation,
    bool IsVanished,
    bool IsPlural);

public record TsFile(string Locale, List<TsMessage> Messages);

public static class TsParser
{
    public static TsFile Parse(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root
            ?? throw new InvalidOperationException("TS root missing");
        var locale = root.Attribute("language")?.Value
            ?? throw new InvalidOperationException("TS language missing");

        var messages = new List<TsMessage>();
        foreach (var ctxElem in root.Elements("context"))
        {
            var ctx = ctxElem.Element("name")?.Value ?? "";
            foreach (var msgElem in ctxElem.Elements("message"))
            {
                var source = msgElem.Element("source")?.Value ?? "";
                var translationElem = msgElem.Element("translation");
                var isVanished = translationElem?
                    .Attribute("type")?.Value == "vanished";
                var isPlural = msgElem.Element("numerusform") != null;
                string translation;
                if (isPlural)
                {
                    // 复数:取第一个 numerusform 的 translation,key 加 _N_One
                    var nf = msgElem.Element("numerusform");
                    translation = nf?
                        .Element("translation")?.Value ?? "";
                }
                else
                {
                    translation = translationElem?.Value ?? "";
                }
                var extra = msgElem.Element("extracomment")?.Value;
                if (string.IsNullOrEmpty(source)) continue;
                messages.Add(new TsMessage(ctx, source, extra,
                    translation, isVanished, isPlural));
            }
        }
        return new TsFile(locale, messages);
    }
}