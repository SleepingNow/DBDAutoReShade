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

    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    Task<IReadOnlyList<OcrTextLine>> RecognizeAsync(CapturedImage image, CancellationToken ct);
}
