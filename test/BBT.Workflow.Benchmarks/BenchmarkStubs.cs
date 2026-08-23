using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting.Functions;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Benchmarks;

/// <summary>
/// Hand-written no-op <see cref="IScriptServices"/> for the engine-level benchmarks in
/// <see cref="CompileHitPathIdentityBenchmarks"/>. Compile-only exercise (no compiled script's
/// <c>Handler</c> is ever invoked here), so none of these members are actually read at runtime —
/// they only need to satisfy the interface so <c>ScriptActivator.Create</c>'s
/// <c>ScriptBase.SetServices</c> call has something to store. A Moq-backed double is deliberately
/// avoided: BenchmarkDotNet runs the measured methods in a separate, isolated toolchain process
/// where pulling in Moq (an Application.Tests-only dependency) would be an unnecessary reference.
/// </summary>
public sealed class NoopScriptServices : IScriptServices
{
    public DaprClient DaprClient => null!;

    public ILogger Logger => NullLogger.Instance;

    public IConfiguration Configuration => null!;

    // IScriptSecretCache? SecretCache uses the interface's own default (=> null); no override needed.
}

/// <summary>
/// Hand-written no-op <see cref="IWorkflowMetrics"/> for <see cref="CompileHitPathIdentityBenchmarks"/>.
/// Every member is an empty body — the benchmark measures the compile hit path itself, not metrics
/// recording, and a real metrics implementation (prometheus-net-backed) would add unrelated
/// allocation/contention noise to the [MemoryDiagnoser] numbers. See <see cref="NoopScriptServices"/>
/// for why this is hand-written instead of a mock.
/// </summary>
public sealed class NoopWorkflowMetrics : IWorkflowMetrics
{
    public void RecordInstanceCreated(string workflow, string domain) { }

    public void RecordInstanceCompleted(string workflow, string domain, double? durationSeconds = null) { }

    public void RecordInstanceTimedOut(string workflow, string domain, string currentStatus, double? durationSeconds = null) { }

    public void RecordInstanceDuration(string workflow, string status, double durationSeconds) { }

    public void SetActiveInstances(string workflow, int count) { }

    public void UpdateInstanceStatusMetrics(string workflow, string oldStatus, string newStatus) { }

    public void RecordTaskExecuted(string taskType, string workflow) { }

    public void RecordTaskCompleted(string taskType, string workflow, double durationSeconds) { }

    public void RecordTaskFailed(string taskType, string workflow, double durationSeconds) { }

    public void RecordTaskRetried(string taskType, string workflow) { }

    public void RecordTaskDuration(string taskType, double durationSeconds) { }

    public void RecordTaskQueueWait(string taskType, double waitDurationSeconds) { }

    public void IncrementPendingTasks(string taskType, string workflow) { }

    public void StartTaskExecution(string taskType, string workflow) { }

    public void FinishTaskExecution(string taskType, string workflow) { }

    public void SetTaskPoolSize(string taskType, int size) { }

    public void RecordDbQueryDuration(string queryType, string table, double durationSeconds) { }

    public void RecordDbTransactionDuration(string operation, double durationSeconds) { }

    public void RecordDbQuery(string queryType, string table, string status) { }

    public void RecordDbError(string operation, string table, string errorType) { }

    public void RecordDbConnection(string connectionType, string status) { }

    public void RecordCacheHit(string cacheName) { }

    public void RecordCacheMiss(string cacheName) { }

    public void RecordCacheEviction(string cacheName, string reason) { }

    public void SetCacheSize(string cacheName, long sizeBytes) { }

    public void SetCacheEntries(string cacheName, int entries) { }

    public void RecordHttpRequest(string method, string endpoint, string statusCode) { }

    public void RecordHttpError(string method, string endpoint, string errorType) { }

    public void RecordHttpRequestDuration(string method, string endpoint, string statusCode, double durationSeconds) { }

    public void RecordHttpResponseSize(string method, string endpoint, string statusCode, long sizeBytes) { }

    public void RecordJobExecuted(string jobType, string status) { }

    public void SetJobsPending(string jobType, int count) { }

    public void RecordError(string errorType, string severity, string component) { }

    public void RecordStateTransition(string workflow, string fromState, string toState) { }

    public void RecordStateEntry(string workflow, string state) { }

    public void RecordStateDuration(string workflow, string state, double durationSeconds) { }

    public void SetTaskFactoryPoolSize(string taskType, int size) { }

    public void SetTaskFactoryPoolAvailable(string taskType, int available) { }

    public void SetTaskFactoryPoolInUse(string taskType, int inUse) { }

    public void RecordTaskFactoryPoolRental(string taskType) { }

    public void RecordTaskFactoryPoolReturn(string taskType) { }

    public void RecordTaskFactoryPoolCreate(string taskType) { }

    public void RecordExternalServiceCall(string serviceName, string operation, string status) { }

    public void RecordExternalServiceFailure(string serviceName, string operation, string failureType) { }

    public void RecordExternalServiceTimeout(string serviceName, string operation, double timeoutThreshold) { }

    public void RecordExternalServiceDuration(string serviceName, string operation, string status, double durationSeconds) { }

    public void RecordDaprServiceInvocation(string serviceName, string methodName, string status) { }

    public void RecordDaprPubsubMessagePublished(string? pubsubName, string topic, string status) { }

    public void RecordDaprPubsubMessageReceived(string pubsubName, string topic, string status) { }

    public void RecordDaprBindingInvocation(string bindingName, string operation, string status) { }

    public void RecordBackgroundJobScheduled(string jobType, string jobName) { }

    public void RecordBackgroundJobExecuted(string jobType, string jobName, string status) { }

    public void RecordBackgroundJobFailed(string jobType, string jobName, string failureReason) { }

    public void RecordBackgroundJobRetried(string jobType, string jobName, int retryCount) { }

    public void RecordBackgroundJobDuration(string jobType, string jobName, string status, double durationSeconds) { }

    public void RecordBackgroundJobQueueWait(string jobType, string jobName, double waitDurationSeconds) { }

    public void SetBackgroundJobsPending(string jobType, int count) { }

    public void SetBackgroundJobsRunning(string jobType, int count) { }

    public void RecordScriptExecution(string scriptType, string language, string status) { }

    public void RecordScriptCompilationError(string scriptType, string language, string errorType) { }

    public void RecordScriptRuntimeError(string scriptType, string language, string errorType) { }

    public void RecordScriptCompilation(string result, string status) { }

    public void RecordScriptCompilationDuration(string scriptType, string language, string status, double durationSeconds, string cache = "unknown") { }

    public void RecordScriptExecutionDuration(string scriptType, string language, string status, double durationSeconds) { }

    public void RecordWorkflowError(string errorType, string severity, string component) { }

    public void RecordWorkflowException(string exceptionType, string component, string operation) { }

    public void RecordValidationFailure(string validationType, string component, string field) { }

    public void SetWorkflowErrorRate(string component, double errorRate) { }

    public void SetWorkflowHealthStatus(string component, bool isHealthy) { }

    public void RecordTaskExecution(string taskType, string status) { }

    public void RecordWorkflowInstanceCompletion(string workflowType, string status, double durationSeconds) { }

    public void SetActiveWorkflowInstances(string workflowType, int count) { }

    public void RecordFanOutBatch(string taskKey, string workflowKey, int total, int succeeded, int failed, double durationSeconds) { }

    public void RecordErrorBoundaryResolution(string workflow, string level, string action) { }

    public void RecordErrorBoundaryUnhandled(string workflow, string exceptionType, string scope) { }

    public void RecordErrorBoundaryRetry(string workflow, string taskType, int attempt) { }

    public void RecordErrorActionDuration(string action, double durationSeconds) { }

    public void RecordSubFlowErrorPropagation(string parentWorkflow, string childWorkflow, bool propagated) { }
}
