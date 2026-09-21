using System;
using System.IO;
using BBT.Workflow.Execution;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// The shipped appsettings.json is what decides where every domain's tasks run. Pin it: a silent
/// edit here changes the egress host for an entire installation.
/// </summary>
public sealed class TaskInvocationDefaultsTests
{
    [Fact]
    public void ShippedConfiguration_RunsTheFiveSupportedTypesLocally()
    {
        var options = BindShippedOptions();

        options.Modes[TaskTypes.Http].ShouldBe(ExecutionMode.Local);
        options.Modes[TaskTypes.DaprService].ShouldBe(ExecutionMode.Local);
        options.Modes[TaskTypes.Soap].ShouldBe(ExecutionMode.Local);
        options.Modes[TaskTypes.StateStore].ShouldBe(ExecutionMode.Local);
        options.Modes[TaskTypes.CacheAside].ShouldBe(ExecutionMode.Local);
    }

    [Fact]
    public void ShippedConfiguration_LeavesEveryOtherTypeRemote()
    {
        var options = BindShippedOptions();

        options.DefaultMode.ShouldBe(ExecutionMode.Remote);
        options.Modes.ShouldNotContainKey(TaskTypes.Python);
    }

    private static TaskInvocationOptions BindShippedOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(
                RepoRoot(),
                "orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json"))
            .Build();

        var options = new TaskInvocationOptions();
        configuration.GetSection(TaskInvocationOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Walks up from the test assembly to the directory holding the solution file. The host
    /// appsettings are not copied into the test output, so they are read from the working tree.
    /// Mirrors <c>UrlTemplateConfigCompletenessTests.RepoRoot</c> rather than a brittle
    /// <c>../../../../..</c> literal, which breaks the moment the test project's output path
    /// changes shape (TFM bump, container build, etc.).
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BBT.Workflow.slnx")))
            dir = dir.Parent;

        dir.ShouldNotBeNull("Could not locate the repository root (BBT.Workflow.slnx) above the test assembly.");
        return dir!.FullName;
    }
}
