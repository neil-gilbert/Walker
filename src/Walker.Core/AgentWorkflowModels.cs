namespace Walker.Core;

// Optional workflow data. Hypotheses and contract descriptions come from the caller, not execution.
public sealed record BehaviourGap(string File, string Member, MutationOperator Operator,
    IReadOnlyList<string> MutantIds, string InvestigationHint);
public sealed record InvestigationPacket(IReadOnlyList<BehaviourGap> Gaps, string Guidance);
public sealed record FaultChallenge(string File, string SourceHash, int SpanStart, string Original,
    string Replacement, string Concern, string ExpectedBehaviour);
public sealed record ChallengeManifest(int SchemaVersion, IReadOnlyList<FaultChallenge> Challenges);
public sealed record TestFilePatch(string File, string? OriginalHash, string Content);
public sealed record TestPatchManifest(int SchemaVersion, string Contract, IReadOnlyList<TestFilePatch> Files);
public sealed record TestImprovementEvidence(string Contract, string Status, VerificationResult Before,
    IReadOnlyList<TestFilePatch> Files, IReadOnlyList<string> VerifiedMutantIds, string Guidance);
