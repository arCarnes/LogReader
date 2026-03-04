namespace LogReader.Core;

using System.Text;
using LogReader.Core.Models;

public static class EncodingHelper
{
    static EncodingHelper()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding GetEncoding(FileEncoding encoding) => encoding switch
    {
        FileEncoding.Utf8 => new UTF8Encoding(false),
        FileEncoding.Ansi => Encoding.GetEncoding(1252),
        FileEncoding.Utf16 => Encoding.Unicode,
        _ => new UTF8Encoding(false)
    };
}
