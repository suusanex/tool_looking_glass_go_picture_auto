namespace LookGo.Core.Models;

public sealed record EditorRow(
    string Id,
    int OrderNumber,
    string RawPath,
    string ResolvedPath,
    bool IsValid,
    string ValidationMessage)
{
    public string ValidationSummary => IsValid ? "OK" : ValidationMessage;
}
