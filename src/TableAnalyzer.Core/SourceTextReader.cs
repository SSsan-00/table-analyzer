using System.Text;

namespace TableAnalyzer.Core;

public sealed record SourceTextReadResult(bool Success, string Text, string EncodingName, string? ErrorMessage);

public sealed class SourceTextReader
{
    public SourceTextReadResult Read(string path)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return new SourceTextReadResult(true, Encoding.UTF8.GetString(bytes), "utf-8-bom", null);
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            try
            {
                return new SourceTextReadResult(true, utf8.GetString(bytes), "utf-8", null);
            }
            catch (DecoderFallbackException)
            {
                var cp932 = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return new SourceTextReadResult(true, cp932.GetString(bytes), "cp932", null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new SourceTextReadResult(false, string.Empty, string.Empty, ex.Message);
        }
    }
}
