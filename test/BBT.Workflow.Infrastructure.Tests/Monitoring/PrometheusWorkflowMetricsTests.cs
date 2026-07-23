using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Monitoring;

/// <summary>
/// Unit tests for PrometheusWorkflowMetrics
/// </summary>
public sealed class PrometheusWorkflowMetricsTests
{
    private readonly PrometheusWorkflowMetrics _metrics;

    public PrometheusWorkflowMetricsTests()
    {
        _metrics = new PrometheusWorkflowMetrics();
    }

    #region Instance Metrics Tests

    [Fact]
    public void RecordInstanceCreated_Should_Not_Throw()
    {
        // Arrange
        const string workflow = "TestWorkflow";
        const string domain = "TestDomain";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordInstanceCreated(workflow, domain));
    }

    [Fact]
    public void RecordInstanceCompleted_Should_Not_Throw()
    {
        // Arrange
        const string workflow = "TestWorkflow";
        const string domain = "TestDomain";
        const double durationSeconds = 5.5;

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordInstanceCompleted(workflow, domain, durationSeconds));
    }

    [Fact]
    public void RecordInstanceCompleted_Without_Duration_Should_Not_Throw()
    {
        // Arrange
        const string workflow = "TestWorkflow";
        const string domain = "TestDomain";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordInstanceCompleted(workflow, domain));
    }

    [Fact]
    public void RecordInstanceTimedOut_Should_Not_Throw()
    {
        // Arrange
        const string workflow = "TestWorkflow";
        const string domain = "TestDomain";
        const string currentStatus = "Active";
        const double durationSeconds = 60.0;

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordInstanceTimedOut(workflow, domain, currentStatus, durationSeconds));
    }

    [Fact]
    public void RecordInstanceDuration_Should_Not_Throw()
    {
        // Arrange
        const string workflow = "TestWorkflow";
        const string status = "Completed";
        const double durationSeconds = 10.5;

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordInstanceDuration(workflow, status, durationSeconds));
    }

    [Fact]
    public void SetActiveInstances_Should_Not_Throw()
    {
        // Arrange
        const string workflow = "TestWorkflow";
        const int count = 5;

        // Act & Assert
        Should.NotThrow(() => _metrics.SetActiveInstances(workflow, count));
    }

    [Fact]
    public void UpdateInstanceStatusMetrics_Should_Handle_Status_Transition()
    {
        // Arrange
        const string workflow = "TestWorkflow";
        var oldStatus = InstanceStatus.Active.Code;
        var newStatus = InstanceStatus.Busy.Code;

        // Act & Assert
        Should.NotThrow(() => _metrics.UpdateInstanceStatusMetrics(workflow, oldStatus, newStatus));
    }

    [Fact]
    public void UpdateInstanceStatusMetrics_Should_Handle_Completion_Status()
    {
        // Arrange
        const string workflow = "TestWorkflow";
        var oldStatus = InstanceStatus.Active.Code;
        const string newStatus = "Completed";

        // Act & Assert
        Should.NotThrow(() => _metrics.UpdateInstanceStatusMetrics(workflow, oldStatus, newStatus));
    }

    #endregion

    #region Task Metrics Tests

    [Fact]
    public void RecordTaskExecuted_Should_Not_Throw()
    {
        // Arrange
        const string taskType = "HttpTask";
        const string workflow = "TestWorkflow";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordTaskExecuted(taskType, workflow));
    }

    [Fact]
    public void RecordTaskCompleted_Should_Not_Throw()
    {
        // Arrange
        const string taskType = "HttpTask";
        const string workflow = "TestWorkflow";
        const double durationSeconds = 2.5;

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordTaskCompleted(taskType, workflow, durationSeconds));
    }

    [Fact]
    public void RecordTaskFailed_Should_Not_Throw()
    {
        // Arrange
        const string taskType = "HttpTask";
        const string workflow = "TestWorkflow";
        const double durationSeconds = 1.5;

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordTaskFailed(taskType, workflow, durationSeconds));
    }

    [Fact]
    public void RecordTaskRetried_Should_Not_Throw()
    {
        // Arrange
        const string taskType = "HttpTask";
        const string workflow = "TestWorkflow";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordTaskRetried(taskType, workflow));
    }

    [Fact]
    public void RecordTaskQueueWait_Should_Not_Throw()
    {
        // Arrange
        const string taskType = "HttpTask";
        const double waitDurationSeconds = 0.5;

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordTaskQueueWait(taskType, waitDurationSeconds));
    }

    [Fact]
    public void IncrementPendingTasks_Should_Not_Throw()
    {
        // Arrange
        const string taskType = "HttpTask";
        const string workflow = "TestWorkflow";

        // Act & Assert
        Should.NotThrow(() => _metrics.IncrementPendingTasks(taskType, workflow));
    }

    [Fact]
    public void StartTaskExecution_Should_Not_Throw()
    {
        // Arrange
        const string taskType = "HttpTask";
        const string workflow = "TestWorkflow";

        // Act & Assert
        Should.NotThrow(() => _metrics.StartTaskExecution(taskType, workflow));
    }

    [Fact]
    public void FinishTaskExecution_Should_Not_Throw()
    {
        // Arrange
        const string taskType = "HttpTask";
        const string workflow = "TestWorkflow";

        // Act & Assert
        Should.NotThrow(() => _metrics.FinishTaskExecution(taskType, workflow));
    }

    [Fact]
    public void SetTaskPoolSize_Should_Not_Throw()
    {
        // Arrange
        const string taskType = "HttpTask";
        const int size = 10;

        // Act & Assert
        Should.NotThrow(() => _metrics.SetTaskPoolSize(taskType, size));
    }

    #endregion

    #region Database Metrics Tests

    [Fact]
    public void RecordDbQueryDuration_Should_Not_Throw()
    {
        // Arrange
        const string queryType = "SELECT";
        const string table = "Instances";
        const double durationSeconds = 0.05;

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordDbQueryDuration(queryType, table, durationSeconds));
    }

    [Fact]
    public void RecordDbTransactionDuration_Should_Not_Throw()
    {
        // Arrange
        const string operation = "commit";
        const double durationSeconds = 0.1;

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordDbTransactionDuration(operation, durationSeconds));
    }

    [Fact]
    public void RecordDbQuery_Should_Not_Throw()
    {
        // Arrange
        const string queryType = "SELECT";
        const string table = "Instances";
        const string status = "success";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordDbQuery(queryType, table, status));
    }

    [Fact]
    public void RecordDbError_Should_Not_Throw()
    {
        // Arrange
        const string operation = "SELECT";
        const string table = "Instances";
        const string errorType = "TimeoutException";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordDbError(operation, table, errorType));
    }

    [Fact]
    public void RecordDbConnection_Should_Not_Throw()
    {
        // Arrange
        const string connectionType = "transaction";
        const string status = "started";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordDbConnection(connectionType, status));
    }

    #endregion

    #region Cache Metrics Tests

    [Fact]
    public void RecordCacheHit_Should_Not_Throw()
    {
        // Arrange
        const string cacheName = "DefinitionCache";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordCacheHit(cacheName));
    }

    [Fact]
    public void RecordCacheMiss_Should_Not_Throw()
    {
        // Arrange
        const string cacheName = "DefinitionCache";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordCacheMiss(cacheName));
    }

    [Fact]
    public void RecordCacheEviction_Should_Not_Throw()
    {
        // Arrange
        const string cacheName = "DefinitionCache";
        const string reason = "Expired";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordCacheEviction(cacheName, reason));
    }

    [Fact]
    public void SetCacheSize_Should_Not_Throw()
    {
        // Arrange
        const string cacheName = "DefinitionCache";
        const long sizeBytes = 1024000;

        // Act & Assert
        Should.NotThrow(() => _metrics.SetCacheSize(cacheName, sizeBytes));
    }

    [Fact]
    public void SetCacheEntries_Should_Not_Throw()
    {
        // Arrange
        const string cacheName = "DefinitionCache";
        const int entries = 50;

        // Act & Assert
        Should.NotThrow(() => _metrics.SetCacheEntries(cacheName, entries));
    }

    #endregion

    #region Background Job Metrics Tests

    [Fact]
    public void RecordBackgroundJobScheduled_Should_Not_Throw()
    {
        // Arrange
        const string jobType = "TimeoutCheck";
        const string jobName = "InstanceTimeoutChecker";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordBackgroundJobScheduled(jobType, jobName));
    }

    [Fact]
    public void RecordBackgroundJobExecuted_Should_Not_Throw()
    {
        // Arrange
        const string jobType = "TimeoutCheck";
        const string jobName = "InstanceTimeoutChecker";
        const string status = "success";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordBackgroundJobExecuted(jobType, jobName, status));
    }

    [Fact]
    public void RecordBackgroundJobFailed_Should_Not_Throw()
    {
        // Arrange
        const string jobType = "TimeoutCheck";
        const string jobName = "InstanceTimeoutChecker";
        const string failureReason = "DatabaseUnavailable";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordBackgroundJobFailed(jobType, jobName, failureReason));
    }

    [Fact]
    public void RecordBackgroundJobDuration_Should_Not_Throw()
    {
        // Arrange
        const string jobType = "TimeoutCheck";
        const string jobName = "InstanceTimeoutChecker";
        const string status = "success";
        const double durationSeconds = 5.5;

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordBackgroundJobDuration(jobType, jobName, status, durationSeconds));
    }

    [Fact]
    public void SetBackgroundJobsPending_Should_Not_Throw()
    {
        // Arrange
        const string jobType = "TimeoutCheck";
        const int count = 3;

        // Act & Assert
        Should.NotThrow(() => _metrics.SetBackgroundJobsPending(jobType, count));
    }

    #endregion

    #region Dapr Metrics Tests

    [Fact]
    public void RecordDaprServiceInvocation_Should_Not_Throw()
    {
        // Arrange
        const string serviceName = "execution-service";
        const string methodName = "TransitionAsync";
        const string status = "success";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordDaprServiceInvocation(serviceName, methodName, status));
    }

    [Fact]
    public void RecordDaprPubsubMessagePublished_Should_Not_Throw()
    {
        // Arrange
        const string pubsubName = "vnext-pubsub";
        const string topic = "instance-completed";
        const string status = "success";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordDaprPubsubMessagePublished(pubsubName, topic, status));
    }

    [Fact]
    public void RecordDaprPubsubMessagePublished_With_Null_PubsubName_Should_Not_Throw()
    {
        // Arrange
        const string topic = "instance-completed";
        const string status = "success";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordDaprPubsubMessagePublished(null, topic, status));
    }

    #endregion

    #region Instance Data Reconciliation Metrics Tests

    [Fact]
    public void Reconciliation_metrics_should_accept_only_approved_labels()
    {
        // Act & Assert - labels are flow, pipeline_step, result, rebased only;
        // attempts/duration/contributions are histogram observations.
        Should.NotThrow(() => _metrics.RecordInstanceDataReconciliation(
            "flow-a", "OnExecute", "applied", rebased: true,
            attempts: 2, durationSeconds: 0.01, contributions: 1, conflicts: 1));
    }

    [Fact]
    public void Reconciliation_metrics_exhausted_result_should_not_throw()
    {
        Should.NotThrow(() => _metrics.RecordInstanceDataReconciliation(
            "flow-a", "OnEntry", "exhausted", rebased: true,
            attempts: 5, durationSeconds: 0.5, contributions: 2, conflicts: 5));
    }

    [Fact]
    public void Reconciliation_metrics_repeated_calls_should_not_register_duplicate_collectors()
    {
        // prometheus-net collectors are static; a second metrics instance must reuse them.
        var second = new PrometheusWorkflowMetrics();

        Should.NotThrow(() => second.RecordInstanceDataReconciliation(
            "flow-b", "OnExit", "failed", rebased: false,
            attempts: 1, durationSeconds: 0.001, contributions: 3, conflicts: 0));
    }

    #endregion

    #region Error Metrics Tests

    [Fact]
    public void RecordError_Should_Not_Throw()
    {
        // Arrange
        const string errorType = "ValidationError";
        const string severity = "Warning";
        const string component = "TaskExecutor";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordError(errorType, severity, component));
    }

    [Fact]
    public void RecordWorkflowException_Should_Not_Throw()
    {
        // Arrange
        const string exceptionType = "NullReferenceException";
        const string component = "TaskExecutor";
        const string operation = "ExecuteTask";

        // Act & Assert
        Should.NotThrow(() => _metrics.RecordWorkflowException(exceptionType, component, operation));
    }

    [Fact]
    public void SetWorkflowHealthStatus_Should_Handle_Healthy_Status()
    {
        // Arrange
        const string component = "Database";
        const bool isHealthy = true;

        // Act & Assert
        Should.NotThrow(() => _metrics.SetWorkflowHealthStatus(component, isHealthy));
    }

    [Fact]
    public void SetWorkflowHealthStatus_Should_Handle_Unhealthy_Status()
    {
        // Arrange
        const string component = "Database";
        const bool isHealthy = false;

        // Act & Assert
        Should.NotThrow(() => _metrics.SetWorkflowHealthStatus(component, isHealthy));
    }

    #endregion
}

