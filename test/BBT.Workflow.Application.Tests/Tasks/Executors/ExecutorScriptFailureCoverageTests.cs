using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// vnext#1268: every task script's failure reaches the incident's stack trace only because the
/// executor reports it through <c>ScriptDiagnostics.TaskScriptError</c>, which keeps the exception on
/// the script context. An executor that builds the <c>Error</c> from the exception itself loses the
/// trace silently — no behaviour test of another executor would notice — so the source is checked.
/// </summary>
public sealed class ExecutorScriptFailureCoverageTests
{
    // An Error built from a script exception: `Error.Failure(..., ScriptDiagnostics.Explain(ex)...)`.
    private static readonly Regex ErrorFromExplainedException =
        new(@"Error\.\w+\([^;]*?ScriptDiagnostics\.Explain\(", RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void NoTaskExecutor_BuildsAnErrorFromAScriptException_ItReportsItThroughTaskScriptError()
    {
        var tasksDirectory = Path.Combine(RepositoryRoot(), "src", "BBT.Workflow.Application", "Tasks");

        var offenders = Directory.EnumerateFiles(tasksDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => ErrorFromExplainedException.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(tasksDirectory, file))
            .ToList();

        offenders.ShouldBeEmpty(
            "these build an Error from a script exception and drop its stack trace; use ScriptDiagnostics.TaskScriptError");
        Directory.EnumerateFiles(tasksDirectory, "*.cs", SearchOption.AllDirectories)
            .Count(file => File.ReadAllText(file).Contains("ScriptDiagnostics.TaskScriptError(", StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(16, "every executor family with mapping handlers reports through the helper");
    }

    private static string RepositoryRoot()
    {
        // `.git` is a directory in a clone and a file in a worktree; either marks the root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
               && !File.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("could not locate the repository root");
        return directory!.FullName;
    }
}
