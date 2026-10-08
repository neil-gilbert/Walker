namespace Walker.Core;

/// <summary>Stable machine identifiers with human-readable context. Actions are advisory.</summary>
public sealed record VerificationDiagnostic(string Code, string Phase, string Message, IReadOnlyList<string> Actions)
{
    public static VerificationDiagnostic Create(string code, string phase, string message, params string[] actions) =>
        new(code, phase, message, actions);

    public static VerificationDiagnostic FromException(Exception exception, string phase) =>
        exception is VerificationException failure ? failure.Diagnostic : Create("unexpected_error", phase, exception.Message);
}

public sealed class VerificationException : InvalidOperationException
{
    public VerificationDiagnostic Diagnostic { get; }

    public VerificationException(string code, string phase, string message, Exception? innerException = null, params string[] actions)
        : base(message, innerException) => Diagnostic = VerificationDiagnostic.Create(code, phase, message, actions);
}
