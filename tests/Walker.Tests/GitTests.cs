using Walker.Core;
using Walker.Execution;
using Walker.Git;
using Xunit;
namespace Walker.Tests;
public sealed class GitTests
{
    [Fact]
    public async Task MapsActualWorkingCopyLinesAndExcludesTestsAndGeneratedFiles()
    {
        using var workspace = new Workspace();
        var runner = new ProcessRunner();
        async Task Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, workspace.Root), default);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        await Git("init"); await Git("config", "user.email", "tests@example.invalid"); await Git("config", "user.name", "Tests");
        workspace.Write("src/Code.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        workspace.Write("src/Code.cs", "class C {\n bool M(int a, int b) => a > b;\n}\n");
        workspace.Write("src/Code.Designer.cs", "class D {}\n");
        workspace.Write("src/Tests/Tests.csproj", "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        workspace.Write("src/Tests/CodeTests.cs", "class Tests {}\n");
        await Git("add", "."); await Git("commit", "-m", "base");
        workspace.Write("src/Code.cs", "class C {\n bool M(int a, int b) => a >= b;\n}\n");
        await Git("add", "."); await Git("commit", "-m", "agent change");
        workspace.Write("src/Code.cs", "// local user edit\nclass C {\n bool M(int a, int b) => a >= b;\n}\n");
        workspace.Write("src/Code.Designer.cs", "class D { bool M() => true; }\n");
        workspace.Write("src/Tests/CodeTests.cs", "class Tests { bool M() => true; }\n");
        var changes = await new GitChangeProvider(runner).GetChangesAsync(new(workspace.Root, "HEAD~1", "src/Code.csproj", ["src/Tests/Tests.csproj"]), default);
        var change = Assert.Single(changes);
        Assert.Equal("src/Code.cs", change.File);
        Assert.True(change.HasUncommittedChanges);
        Assert.Contains(change.Lines, l => l.Intersects(3, 3));
    }
}
