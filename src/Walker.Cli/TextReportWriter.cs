using System.Globalization;
using System.Text.Json;
using Walker.Core;
namespace Walker.Cli;

public static class TextReportWriter
{
    public static void Write(VerificationResult result, TextWriter writer, bool verbose = false)
    {
        writer.WriteLine("WALKER");
        writer.WriteLine($"Horde: {result.MutantsDiscovered} mutation candidates");
        writer.WriteLine($"{result.MutantsSelected} selected for verification");
        writer.WriteLine($"{result.MutantsExecuted}/{result.MutantsSelected} executed");
        writer.WriteLine($"{result.Killed} KILLED");
        if (result.Hung > 0) writer.WriteLine($"{result.Hung} hung (exceeded the per-mutant hang limit; counted as detected)");
        writer.WriteLine($"{result.Survived} {(result.Survived == 1 ? "WALKER" : "WALKERS")}");
        if (result.CompileErrors + result.TestErrors + result.TimedOut + result.Skipped > 0)
            writer.WriteLine($"{result.CompileErrors + result.TestErrors} errored, {result.TimedOut} timed out, {result.Skipped} skipped");
        if (result.UnresolvedArithmetic > 0)
            writer.WriteLine($"{result.UnresolvedArithmetic} arithmetic {(result.UnresolvedArithmetic == 1 ? "candidate" : "candidates")} not mutated: operand types could not be resolved");
        if (result.UnresolvedBoolean > 0)
            writer.WriteLine($"{result.UnresolvedBoolean} boolean return candidates not mutated: expression types could not be resolved");
        writer.WriteLine(result.Status switch
        {
            "passed" => "SAFE — nothing is still walking.",
            "failed" => "IT'S STILL WALKING.",
            "error" => "VERIFICATION ERROR — mutation evidence is unavailable or unreliable.",
            _ => "VERIFICATION INCOMPLETE — safety has not been established."
        });
        if (result.Error != null) writer.WriteLine(result.Error);
        foreach (var r in result.Results.Where(r => r.Outcome is not (MutationOutcome.Killed or MutationOutcome.Skipped)))
        {
            writer.WriteLine();
            writer.WriteLine(r.Outcome == MutationOutcome.Survived ? $"WALKER {r.Mutant.Id}" : $"{r.Outcome.ToString().ToUpperInvariant()} MUTANT {r.Mutant.Id}");
            writer.WriteLine($"{r.Mutant.File}:{r.Mutant.Line}");
            writer.WriteLine(r.Mutant.Member);
            writer.WriteLine($"Original:\n    {r.Mutant.Original}");
            writer.WriteLine($"{(r.Outcome == MutationOutcome.Survived ? "Walker" : "Mutation")}:\n    {r.Mutant.Replacement}");
            writer.WriteLine(r.Outcome == MutationOutcome.Survived ? "Your tests did not detect this behavioural change. Investigate the missing behavioural constraint." : r.Detail);
        }
        writer.WriteLine($"Completed in {(result.DurationMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture)}s");
        if (verbose)
            writer.WriteLine(JsonSerializer.Serialize(new { result.Timings, PerMutant = result.Results.Select(r => new { r.Mutant.Id, r.DurationMs, r.BuildMs, r.TestMs }) },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        if (result.Survived > 0) writer.WriteLine("If it still walks, your tests aren't done.");
        writer.WriteLine(result.Guidance);
    }
}
