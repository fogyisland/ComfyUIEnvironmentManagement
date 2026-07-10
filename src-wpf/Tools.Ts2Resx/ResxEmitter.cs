using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace ComfyUI.Manager.Tools.Ts2Resx;

public static class ResxEmitter
{
    /// <summary>
    /// 把一组消息写入单个 .resx 文件。
    /// cultureName == null → 默认 Strings.resx(无 culture 后缀)。
    /// </summary>
    public static void Emit(string outPath, string cultureName,
        IEnumerable<(string Key, string Value)> entries)
    {
        using var writer = XmlWriter.Create(outPath, new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
        });
        writer.WriteStartDocument();
        writer.WriteStartElement("root");
        writer.WriteAttributeString("xmlns", "xsd",
            null, "http://www.w3.org/2001/XMLSchema");
        writer.WriteAttributeString("xmlns", "xsi",
            null, "http://www.w3.org/2001/XMLSchema-instance");
        writer.WriteStartElement("resheader");
        writer.WriteAttributeString("name", "resmimetype");
        writer.WriteElementString("value", "text/microsoft-resx");
        writer.WriteEndElement();
        writer.WriteStartElement("resheader");
        writer.WriteAttributeString("name", "version");
        writer.WriteElementString("value", "2.0");
        writer.WriteEndElement();
        writer.WriteStartElement("resheader");
        writer.WriteAttributeString("name", "reader");
        writer.WriteElementString("value",
            "System.Resources.ResXResourceReader, System.Windows.Forms, "
            + "Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        writer.WriteEndElement();
        writer.WriteStartElement("resheader");
        writer.WriteAttributeString("name", "writer");
        writer.WriteElementString("value",
            "System.Resources.ResXResourceWriter, System.Windows.Forms, "
            + "Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        writer.WriteEndElement();

        foreach (var (key, value) in entries)
        {
            writer.WriteStartElement("data");
            writer.WriteAttributeString("name", key);
            writer.WriteAttributeString("xml", "space", null, "preserve");
            writer.WriteElementString("value", value);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }
}