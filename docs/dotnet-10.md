# .NET 10 migration and feature review

Reviewed 2026-10-07 against Walker's CLI, mutation discovery, process runner,
MSBuild observer, test runner and benchmark harnesses. Recommendations below are
Walker-specific judgments; links identify the official release/API facts. This
note records the migration design, not benchmark results or a test-run record.

.NET 10 is an LTS release supported through November 14, 2028; .NET 8 support
ends November 10, 2026. Moving Walker's host to .NET 10 therefore also extends its
supported runtime lifetime. [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)

## Adopt with the upgrade

| Change | Benefit and scope |
| --- | --- |
| Target `net10.0` for Walker's main projects, tests and benchmarks; select SDK `10.0.100` with `latestFeature` and no prereleases. | Allows installed stable .NET 10 feature bands and patches without silently moving to .NET 11. SDK selection and the project's target runtime are separate settings. [SDK resolution](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json) |
| Upgrade the embedded `Microsoft.CodeAnalysis.CSharp` package to `5.0.0`. | The SDK compiler does not replace Walker's parser dependency. Roslyn 5 exposes `LanguageVersion.CSharp14`; `net10.0` also defaults to C# 14 for Walker's own source. [Roslyn API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.csharp.languageversion?view=roslyn-dotnet-5.0.0), [C# language defaults](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-versioning) |
| Set `AllowDuplicateProperties = false` when deserializing `walker.json`. | Rejects ambiguous repeated settings instead of accepting the last value. Duplicate detection respects case-insensitive property binding, which Walker already permits. Retain that existing option and unknown-property rejection rather than adopting the broader `Strict` preset, which also changes case sensitivity and nullable/required-member handling. [New JSON options](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries#option-to-disallow-duplicate-json-properties) |

C# 14 adds extension blocks, field-backed properties, null-conditional assignment
and other syntax. The immediate value for Walker is understanding users' source,
rather than rewriting its implementation to use every feature. Discovery should
be checked inside extension-member bodies and `field` property accessors, with
stable member names and source spans. Parser acceptance alone does not introduce
new mutation operators. [C# 14 features](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14/)

`RoslynMutationDiscoverer` still constructs a lightweight semantic compilation
from source files and Walker's `TRUSTED_PLATFORM_ASSEMBLIES`. Running Walker on
.NET 10 changes those runtime references; it does not make them identical to the
target project's references, symbols or compiler options. Preserve conservative
handling of unresolved semantic candidates and avoid claiming full project-level
semantic fidelity. See [the discoverer](../src/Walker.Roslyn/RoslynMutationDiscoverer.cs).

## Preserve target-project compatibility

Walker runs under its own runtime, but the child `dotnet` commands run in the
target repository and follow that repository's SDK selection. Projects pinned to
.NET 8 or 9 still need their selected SDKs and test runtimes installed. Upgrading
Walker does not upgrade the project being verified.

`Walker.BuildLogger.dll` is a special case: MSBuild loads it into the selected
SDK's process through `-logger`. Keep the observer targeting `net8.0` and compile
against `Microsoft.Build.Framework` 17.11.4 with `PrivateAssets="all"` and
`ExcludeAssets="runtime"`. The SDK supplies the runtime MSBuild assembly. This
keeps the observer's dependencies within the older host's available APIs instead
of referencing .NET 10 assemblies through `$(MSBuildBinPath)`. This is a
compatibility decision derived from the hosting model, and needs packed-tool
smoke coverage under SDK 8 and 10; a successful build alone is insufficient.
[MSBuild logger loading](https://learn.microsoft.com/en-us/visualstudio/msbuild/build-loggers),
[Microsoft.Build.Framework 17.11.4](https://www.nuget.org/packages/Microsoft.Build.Framework/17.11.4),
[assembly compatibility rules](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/nuget-package-compatibility-rules)

Keep VSTest as the runner. It remains the `dotnet test` default in .NET 10. The
new Microsoft.Testing.Platform (MTP) mode is an explicit `global.json` choice,
with different argument handling. MTP's TRX output requires an extension; the
legacy MTP integration can ignore VSTest's `--logger` argument. Walker currently
depends on VSTest filters, TRX counters and failure identities, and verified DLL
invocations. Supporting MTP needs a separate execution adapter and outcome/filter
contract tests; changing the global runner would not provide that support.
[Testing modes and TRX requirements](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test),
[MTP command reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-mtp)

.NET 10 projects audit transitive NuGet dependencies by default. Because Walker
treats warnings as errors, restore can expose additional dependency problems.
Keep auditing enabled and resolve reported package issues when they arise.
[NuGet audit change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/nugetaudit-transitive-packages)

## Useful capabilities to retain or evaluate later

| Capability | Assessment for Walker |
| --- | --- |
| JIT inlining, array-interface devirtualization and escape-analysis improvements | Available by running on .NET 10. Existing array, interface and LINQ-heavy discovery/selection code may benefit without source changes. Measure using the existing benchmarks; the build/test subprocesses dominate recorded end-to-end workloads, so no overall speedup is assumed. [Runtime improvements](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime), [Walker measurements](performance.md) |
| Faster LINQ paths; `LeftJoin` and `RightJoin` | Runtime improvements may benefit existing queries. No current query needs an outer join, so there is no reason to restructure discovery/planning around the new operators. Preserve deterministic mutation ordering. [Microsoft performance review](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/#linq) |
| Frozen collection improvements | Frozen collections predate .NET 10; this release improves specializations and alternate lookups. Walker's planner, coverage and semantic-model dictionaries mutate during each run. Freezing them would add construction work and change their lifecycle. Only benchmark a future large, immutable, repeatedly queried index. [Frozen collection improvements](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/#frozen-collections), [FrozenDictionary tradeoffs](https://learn.microsoft.com/en-us/dotnet/api/system.collections.frozen.frozendictionary-2?view=net-10.0) |
| `JsonElement.Parse` and `PipeReader` deserialization | Walker currently parses bounded process-output strings with scoped `JsonDocument` ownership, and streams TRX as XML. There is no persistent `JsonElement` clone or JSON pipeline to replace. Defer until a measured workload benefits. [JsonElement API](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonelement.parse?view=net-10.0), [PipeReader support](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries#pipereader-support-for-json-serializer) |
| Windows process groups | `ProcessStartInfo.CreateNewProcessGroup` can isolate console signals, but also disables Ctrl+C in the new process. Evaluate only with a dedicated Windows cancellation change that preserves bounded process-tree cleanup and exact source restoration. [Process-group semantics](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.createnewprocessgroup?view=net-10.0) |
| One-shot tool execution | `dotnet tool exec` and `dnx` can run a pinned package without permanent installation. This is useful for CI and agent setup once the package is available from the chosen feed; use explicit versions for reproducibility. [Tool execution reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec) |
| Platform-specific, self-contained or Native AOT tools | .NET 10 can package these forms. Keep Walker's existing portable, framework-dependent package for now: target builds still require an SDK, and Roslyn/JSON/observer packaging needs separate trimming, AOT and cross-platform validation. [Tool packaging enhancements](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/sdk#net-tools-enhancements) |

Benchmark comparisons should record both Walker's host runtime and the target
SDK/runtime. Preserve the same selected IDs, outcomes and restored source bytes
when comparing releases; a changed or incomplete mutation workload is not evidence
of a speed improvement. See [benchmark instructions](benchmark.md).
