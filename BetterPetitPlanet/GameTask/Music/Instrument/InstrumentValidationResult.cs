using System.Collections.Generic;

namespace BetterPetitPlanet.GameTask.Music.Instrument;

/// <summary>
/// 乐器模板与标注资源有效性校验结果
/// </summary>
public sealed class InstrumentValidationResult
{
    public string InstrumentId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsValid { get; set; }
    public string? TemplatePath { get; set; }
    public bool TemplateExists { get; set; }
    public List<string> LoadedTargets { get; } = [];
    public List<string> MissingTargets { get; } = [];
    public List<string> CorruptedTargets { get; } = [];
    public string DiagnosisMessage { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"[{InstrumentId}] Valid: {IsValid}, Template: {(TemplateExists ? "OK" : "MISSING")}, Targets: {LoadedTargets.Count}/{LoadedTargets.Count + MissingTargets.Count + CorruptedTargets.Count}. {DiagnosisMessage}";
    }
}
