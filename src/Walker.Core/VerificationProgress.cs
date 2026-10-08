namespace Walker.Core;

public sealed record VerificationProgress(string Phase, int Completed = 0, int Total = 0, string? Detail = null);
