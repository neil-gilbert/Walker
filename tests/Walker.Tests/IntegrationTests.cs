using System.Text.Json;
using Walker.Core;
using Walker.Execution;
using Xunit;
namespace Walker.Tests;
public sealed class IntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurchaseBoundarySurvivesThenIsKilledAfterEqualityTestAndSourceIsRestored(bool transitiveReference)
    {
        using var workspace = new Workspace();
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "Walker.sln"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var runner = new ProcessRunner();
        async Task<ProcessResult> Run(string executable, params string[] args) => await runner.RunAsync(new(executable, args, workspace.Root), default);
        async Task Git(params string[] args)
        {
            var result = await Run("git", args);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        workspace.Write("Directory.Build.props", File.ReadAllText(Path.Combine(repository.FullName, "Directory.Build.props")));
        var example = Path.Combine(repository.FullName, "examples", "Payments");
        foreach (var relative in new[] { "Payments/Payments.csproj", "Payments/PaymentService.cs", "Payments.Tests/Payments.Tests.csproj", "Payments.Tests/PaymentTests.cs" })
            workspace.Write(relative, File.ReadAllText(Path.Combine(example, relative)));
        if (transitiveReference)
        {
            workspace.Write("Facade/Facade.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../Payments/Payments.csproj\" /></ItemGroup></Project>");
            workspace.Write("Facade/PaymentFacade.cs", "namespace Payments; public class PaymentFacade { private readonly PaymentService service = new(); public bool CanPurchase(decimal balance, decimal price) => service.CanPurchase(balance, price); public bool CanAuthorise(bool active, bool approved) => service.CanAuthorise(active, approved); public bool HasCustomer(string? customer) => service.HasCustomer(customer); public decimal Fee(decimal amount) => service.Fee(amount); }");
            var testProject = Path.Combine(workspace.Root, "Payments.Tests/Payments.Tests.csproj");
            File.WriteAllText(testProject, File.ReadAllText(testProject).Replace("../Payments/Payments.csproj", "../Facade/Facade.csproj"));
            var testSource = Path.Combine(workspace.Root, "Payments.Tests/PaymentTests.cs");
            File.WriteAllText(testSource, File.ReadAllText(testSource).Replace("new PaymentService()", "new PaymentFacade()"));
        }
        workspace.Write(".gitignore", "bin/\nobj/\n");
        var sourcePath = Path.Combine(workspace.Root, "Payments", "PaymentService.cs");
        var initial = File.ReadAllText(sourcePath);
        File.WriteAllText(sourcePath, initial.Replace("balance >= price", "balance > price"));
        await Git("init"); await Git("config", "user.email", "tests@example.invalid"); await Git("config", "user.name", "Tests");
        await Git("add", "."); await Git("commit", "-m", "baseline");
        File.WriteAllText(sourcePath, initial);
        await Git("add", "."); await Git("commit", "-m", "allow equal balance");
        // Dirty source must be analysed and restored, including BOM and CRLF bytes.
        var encoding = new System.Text.UTF8Encoding(true);
        var source = "// uncommitted user change\r\n" + initial.Replace("\n", "\r\n");
        var original = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, original);
        var cli = typeof(Walker.Cli.TextReportWriter).Assembly.Location;
        workspace.Write("walker.json", "{\"filter\":\"FullyQualifiedName~PaymentTests\"}");
        workspace.Write("Payments.Tests/UnrelatedTests.cs", "using Xunit; public class UnrelatedTests { [Fact] public void AlwaysFails() => Assert.True(false); }");
        async Task<ProcessResult> Verify(string? filter = null) => await Run("dotnet", [cli, "verify", "--project", "Payments/Payments.csproj",
            "--tests", "Payments.Tests/Payments.Tests.csproj", "--max-mutants", "1", "--timeout", "120", "--format", "json", "--confirm-kills", ..(filter == null ? Array.Empty<string>() : new[] { "--filter", filter })]);
        var weak = await Verify();
        Assert.True(weak.ExitCode == 1, weak.StandardOutput + weak.StandardError);
        using (var report = JsonDocument.Parse(weak.StandardOutput))
        {
            Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("FullyQualifiedName~PaymentTests", report.RootElement.GetProperty("testFilter").GetString());
            Assert.Equal("failed", report.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, report.RootElement.GetProperty("survived").GetInt32());
            Assert.Equal("balance > price", report.RootElement.GetProperty("survivors")[0].GetProperty("replacement").GetString());
        }
        Assert.Equal(original, File.ReadAllBytes(sourcePath));
        workspace.Write("Payments.Tests/BoundaryTests.cs", "using Xunit; namespace Payments.Tests; public class BoundaryTests { [Fact] public void EqualityCanPurchase() => Assert.True(new " + (transitiveReference ? "PaymentFacade" : "PaymentService") + "().CanPurchase(10, 10)); }");
        var strong = await Verify("FullyQualifiedName~PaymentTests|FullyQualifiedName~BoundaryTests");
        Assert.True(strong.ExitCode == 0, strong.StandardOutput + strong.StandardError);
        using (var report = JsonDocument.Parse(strong.StandardOutput))
        {
            Assert.Equal(1, report.RootElement.GetProperty("killed").GetInt32());
            Assert.True(report.RootElement.GetProperty("confirmKills").GetBoolean());
            var killed = report.RootElement.GetProperty("results")[0];
            Assert.True(killed.GetProperty("killConfirmed").GetBoolean());
            Assert.Contains("EqualityCanPurchase", killed.GetProperty("failingTests")[0].GetString());
            Assert.Equal("FullyQualifiedName~PaymentTests|FullyQualifiedName~BoundaryTests", report.RootElement.GetProperty("testFilter").GetString());
        }
        if (!transitiveReference)
        {
            var empty = await Verify("FullyQualifiedName~DoesNotExist");
            Assert.True(empty.ExitCode == 2, empty.StandardOutput + empty.StandardError);
            using var report = JsonDocument.Parse(empty.StandardOutput);
            Assert.Equal(0, report.RootElement.GetProperty("mutantsExecuted").GetInt32());
            Assert.Contains("No executed tests", report.RootElement.GetProperty("error").GetString());
        }
        Assert.Equal(original, File.ReadAllBytes(sourcePath));
    }
}
