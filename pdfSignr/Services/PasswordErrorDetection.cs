namespace pdfSignr.Services;

/// <summary>
/// Detects whether an exception from a PDF parse/render call indicates a missing or wrong
/// password. Both PDFsharp and pdfium (via PDFtoImage) surface password failures through
/// exception messages rather than a typed error code, so we match known phrases across
/// the inner-exception chain.
/// </summary>
internal static class PasswordErrorDetection
{
    public static bool IsPasswordError(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var msg = current.Message;
            if (msg.Contains("password", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("FPDF_ERR_PASSWORD", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
