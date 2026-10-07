using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Walker.Core;

namespace Walker.Execution;

internal static class ProjectCompilation
{
    public static async Task<CSharpCompilation?> Read(IProcessRunner runner, VerificationRequest request, CancellationToken token)
    {
        var result = await runner.RunAsync(new("dotnet", ["msbuild", request.Project, "--nologo", "-target:ResolveReferences",
            "-p:BuildProjectReferences=false", "-p:RunAnalyzers=false",
            "-getProperty:TargetFramework,TargetFrameworks,DefineConstants,LangVersion,Nullable,CheckForOverflowUnderflow,AllowUnsafeBlocks,EnableDefaultCompileItems,GeneratedGlobalUsingsFile,MSBuildToolsPath",
            "-getItem:Compile,ReferencePath,Analyzer"], request.Root, OutputLimit: 16 * 1024 * 1024), token);
        if (result.ExitCode != 0 || result.OutputTruncated) return null;
        using var json = JsonDocument.Parse(result.StandardOutput);
        var properties = json.RootElement.GetProperty("Properties");
        string Property(string name) => properties.GetProperty(name).GetString()!;
        if (Property("TargetFrameworks").Length > 0 || Property("TargetFramework").Length == 0
            || Property("EnableDefaultCompileItems").Equals("false", StringComparison.OrdinalIgnoreCase)) return null;
        var items = json.RootElement.GetProperty("Items");
        foreach (var analyzer in items.GetProperty("Analyzer").EnumerateArray())
        {
            var path = analyzer.GetProperty("FullPath").GetString()!;
            // Standard SDK analyzers/generators are allowed only for projects without source attributes.
            if (!path.Contains("/packs/Microsoft.NETCore.App.Ref/", StringComparison.Ordinal)
                && !path.StartsWith(Property("MSBuildToolsPath") + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        }
        if (!LanguageVersionFacts.TryParse(Property("LangVersion"), out var language)) return null;
        var parse = new CSharpParseOptions(language, preprocessorSymbols: Property("DefineConstants").Split([';', ','], StringSplitOptions.RemoveEmptyEntries));
        var sources = new List<SyntaxTree>();
        foreach (var item in items.GetProperty("Compile").EnumerateArray())
        {
            var fullPath = item.GetProperty("FullPath").GetString()!;
            var relative = Path.GetRelativePath(request.Root, fullPath);
            if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            var source = await File.ReadAllTextAsync(fullPath, token);
            var tree = CSharpSyntaxTree.ParseText(source, parse, path: relative, cancellationToken: token);
            if (tree.GetRoot(token).DescendantNodes().OfType<AttributeSyntax>().Any()) return null;
            sources.Add(tree);
        }
        var globals = Property("GeneratedGlobalUsingsFile");
        if (globals.Length > 0)
        {
            var path = Path.GetFullPath(globals, Path.GetDirectoryName(Path.GetFullPath(request.Project, request.Root))!);
            if (File.Exists(path)) sources.Add(CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(path, token), parse, path: path, cancellationToken: token));
        }
        var references = items.GetProperty("ReferencePath").EnumerateArray().Select(item => MetadataReference.CreateFromFile(item.GetProperty("FullPath").GetString()!)).ToArray();
        return CSharpCompilation.Create("PreparedAnalysis", sources, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            checkOverflow: Property("CheckForOverflowUnderflow") == "true", allowUnsafe: Property("AllowUnsafeBlocks") == "true",
            nullableContextOptions: Property("Nullable") switch { "enable" => NullableContextOptions.Enable, "warnings" => NullableContextOptions.Warnings,
                "annotations" => NullableContextOptions.Annotations, _ => NullableContextOptions.Disable }));
    }
}
