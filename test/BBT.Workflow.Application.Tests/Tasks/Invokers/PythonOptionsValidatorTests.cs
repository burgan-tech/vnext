using BBT.Workflow.Execution.Configuration;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

public sealed class PythonOptionsValidatorTests
{
    private readonly PythonOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultsSucceed()
    {
        _validator.Validate(null, new PythonOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_EnabledPythonNetRequiresCompleteInterpreterConfiguration()
    {
        var options = new PythonOptions
        {
            Enabled = true,
            EnabledModes = [PythonRuntimeModes.PythonNet],
            PythonNet = new PythonNetOptions
            {
                PythonDll = "libpython3.12.so.1.0",
                PythonHome = null,
                PythonPath = "/venv/site-packages",
                RunnerDirectory = "/runner"
            }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("PythonHome");
    }

    [Fact]
    public void Validate_ContainerResourcesMustBePositive()
    {
        var options = new PythonOptions
        {
            Enabled = true,
            DefaultMode = PythonRuntimeModes.Container,
            EnabledModes = [PythonRuntimeModes.Container],
            Container = new PythonContainerOptions { PidsLimit = 0 }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("PidsLimit");
    }

    [Fact]
    public void Validate_KubernetesContainerRequiresNamespaceAndContainerName()
    {
        var options = new PythonOptions
        {
            Enabled = true,
            DefaultMode = PythonRuntimeModes.Container,
            EnabledModes = [PythonRuntimeModes.Container],
            Container = new PythonContainerOptions
            {
                Driver = "kubernetes",
                Kubernetes = new PythonKubernetesOptions
                {
                    Namespace = "",
                    ContainerName = ""
                }
            }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Namespace and ContainerName");
    }

    [Fact]
    public void Validate_UnknownContainerDriverIsRejected()
    {
        var options = new PythonOptions
        {
            Enabled = true,
            DefaultMode = PythonRuntimeModes.Container,
            EnabledModes = [PythonRuntimeModes.Container],
            Container = new PythonContainerOptions { Driver = "unknown" }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("docker or kubernetes");
    }
}
