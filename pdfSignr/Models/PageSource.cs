namespace pdfSignr.Models;

public record PageSource(byte[] PdfBytes, int SourcePageIndex);
