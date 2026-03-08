namespace LogReader.Core;

using System.Text;
using LogReader.Core.Models;

public static class EncodingHelper
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);
    private static readonly Encoding Ansi;

    static EncodingHelper()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Ansi = Encoding.GetEncoding(1252);
    }

    public static Encoding GetEncoding(FileEncoding encoding) => encoding switch
    {
        FileEncoding.Utf8 => Utf8NoBom,
        FileEncoding.Utf8Bom => Utf8WithBom,
        FileEncoding.Ansi => Ansi,
        FileEncoding.Utf16 => Encoding.Unicode,
        FileEncoding.Utf16Be => Encoding.BigEndianUnicode,
        _ => Utf8NoBom
    };
}
