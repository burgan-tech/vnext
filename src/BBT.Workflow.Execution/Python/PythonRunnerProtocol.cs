using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Python;

public sealed class PythonRunnerProtocol(IOptions<PythonOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PythonOptions _options = options.Value;

    public int MaxResponseBytes => checked((int)(
        ((long)_options.MaxOutputBytes + _options.MaxStdoutBytes + _options.MaxStderrBytes) * 6 +
        64 * 1024));

    public string CreateRequestJson(PythonExecutionRequest request)
    {
        using var inputDocument = JsonDocument.Parse(request.InputJson);
        var payload = new PythonRunnerRequest
        {
            Script = request.Script,
            Location = request.Location,
            Input = inputDocument.RootElement.Clone(),
            AllowedModules = _options.AllowedModules,
            MaxOutputBytes = _options.MaxOutputBytes,
            MaxStdoutBytes = _options.MaxStdoutBytes,
            MaxStderrBytes = _options.MaxStderrBytes
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public PythonExecutionResult ParseResponse(string responseJson)
    {
        PythonRunnerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<PythonRunnerResponse>(responseJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PythonExecutionException(
                "Python runner returned an invalid response.",
                "invalid_runner_response",
                innerException: ex);
        }

        if (response is null)
        {
            throw new PythonExecutionException(
                "Python runner returned an empty response.",
                "invalid_runner_response");
        }

        if (!response.Success)
        {
            throw new PythonExecutionException(
                response.Error ?? "Python execution failed.",
                MapReason(response.ExceptionType),
                response.ExceptionType,
                response.RuntimeVersion);
        }

        if (response.OutputJson is null)
        {
            throw new PythonExecutionException(
                "Python runner did not return output JSON.",
                "invalid_runner_response",
                runtimeVersion: response.RuntimeVersion);
        }

        if (Encoding.UTF8.GetByteCount(response.Stdout) > _options.MaxStdoutBytes ||
            Encoding.UTF8.GetByteCount(response.Stderr) > _options.MaxStderrBytes)
        {
            throw new PythonExecutionException(
                "Python captured output exceeds the configured size limit.",
                "output_limit_exceeded",
                runtimeVersion: response.RuntimeVersion);
        }

        if (Encoding.UTF8.GetByteCount(response.OutputJson) > _options.MaxOutputBytes)
        {
            throw new PythonExecutionException(
                "Python output exceeds the configured size limit.",
                "output_limit_exceeded",
                runtimeVersion: response.RuntimeVersion);
        }

        try
        {
            using var _ = JsonDocument.Parse(response.OutputJson);
        }
        catch (JsonException ex)
        {
            throw new PythonExecutionException(
                "Python output is not valid JSON.",
                "output_serialization_error",
                runtimeVersion: response.RuntimeVersion,
                innerException: ex);
        }

        return new PythonExecutionResult(
            response.OutputJson,
            response.Stdout,
            response.Stderr,
            response.RuntimeVersion,
            response.StdoutTruncated,
            response.StderrTruncated);
    }

    private static string MapReason(string? exceptionType) => exceptionType switch
    {
        "SyntaxError" => "syntax_error",
        "EntryPointError" => "entrypoint_error",
        "OutputSerializationError" => "output_serialization_error",
        "OutputLimitError" => "output_limit_exceeded",
        "ImportPolicyError" => "import_policy_error",
        _ => "execution_error"
    };
}
