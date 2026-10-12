using DBDAutoReShade.Core.Services;

namespace DBDAutoReShade.Desktop.Services;

/// <summary>One OCR result line. EngineConfidence is null when the engine exposes none (e.g. WinRT OCR).</summary>
public sealed record OcrTextLine(string Text, float? EngineConfidence);

/// <summary>
/// Local OCR engine abstraction. Implementations must work fully offline;
/// cloud APIs are prohibited. The default is the in-box Windows OCR engine.
/// </summary>
public interface IOcrEngine
{
    string Name { get; }

    /// <summary>
    /// Whether recognition can run now. Implementations may re-evaluate this
    /// over time (e.g. after an OCR language pack is installed), so callers
    /// should query it per attempt rather than cache it.
    /// </summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>
    /// Recognize text in a BGRA image of any size; implementations scale it to
    /// what their engine accepts. Honors <paramref name="ct"/> by throwing
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(CapturedImage image, CancellationToken ct);
}
