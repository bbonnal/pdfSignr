using pdfSignr.Services;
using Xunit;

namespace pdfSignr.Tests;

public class PasswordErrorDetectionTests
{
    [Theory]
    [InlineData("The password is invalid")]
    [InlineData("PDF is encrypted")]
    [InlineData("FPDF_ERR_PASSWORD")]
    [InlineData("Cannot open an encrypted PDF document")]
    public void Detects_known_password_phrases(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.True(PasswordErrorDetection.IsPasswordError(ex));
    }

    [Fact]
    public void Walks_inner_exception_chain()
    {
        var inner = new InvalidOperationException("FPDF_ERR_PASSWORD");
        var middle = new Exception("wrapping layer", inner);
        var outer = new Exception("Render failed", middle);
        Assert.True(PasswordErrorDetection.IsPasswordError(outer));
    }

    [Fact]
    public void Returns_false_for_unrelated_errors()
    {
        Assert.False(PasswordErrorDetection.IsPasswordError(
            new IOException("File not found")));
        Assert.False(PasswordErrorDetection.IsPasswordError(
            new InvalidOperationException("Corrupt object stream")));
    }

    [Fact]
    public void Returns_false_for_null()
    {
        Assert.False(PasswordErrorDetection.IsPasswordError(null));
    }
}
