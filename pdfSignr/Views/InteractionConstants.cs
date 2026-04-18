namespace pdfSignr.Views;

/// <summary>
/// Pointer-interaction tuning constants for annotation manipulation on <see cref="PageCanvas"/>.
/// Kept as code (not settings) because they're UX-tuning values that shouldn't drift per user.
/// </summary>
internal static class InteractionConstants
{
    public const double HandleRadius = 5;
    public const double HandleHit = 10;
    public const double RotateDistance = 28;
    public const double RotateRadius = 6;
    public const double MinSizePt = 8;
    public const double DeleteSize = 7;
    public const double DeleteOffset = 14;
    public const double HitBodyInflate = 4;
}
