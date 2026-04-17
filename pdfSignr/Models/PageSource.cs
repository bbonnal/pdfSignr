namespace pdfSignr.Models;

public record PageSource(byte[] PdfBytes, int SourcePageIndex, string? Password = null);
