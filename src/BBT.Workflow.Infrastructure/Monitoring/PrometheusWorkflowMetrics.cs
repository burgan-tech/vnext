using BBT.Workflow.Instances;

namespace BBT.Workflow.Monitoring;

/// <summary>
/// Prometheus implementation of workflow metrics interface.
/// Provides concrete implementation using Prometheus metrics collection.
/// </summary>
public sealed class PrometheusWorkflowMetrics : IWorkflowMetrics
{
    public void RecordInstanceCreated(string workflow, string domain)
    {
        WorkflowMetrics.InstancesCreated
            .WithLabels(workflow, domain)
            .Inc();
        
        // New instances are typically created as Active, so increment active gauge
        WorkflowMetrics.ActiveInstances
            .WithLabels(workflow)
            .Inc();
    }

    public void RecordInstanceCompleted(string workflow, string domain, double? durationSeconds = null)
    {
        WorkflowMetrics.InstancesCompleted
            .WithLabels(workflow, domain)
            .Inc();
        
        // Record instance duration if available
        if (durationSeconds.HasValue)
        {
            WorkflowMetrics.InstanceDuration
                .WithLabels(workflow, "Completed")
                .Observe(durationSeconds.Value);
        }
        
        // Note: Status gauge changes are handled by UpdateInstanceStatusMetrics
        // No need to manually decrement active instances here
    }

    public void RecordInstanceTimedOut(string workflow, string domain, string currentStatus, double? durationSeconds = null)
    {
        WorkflowMetrics.InstancesTimedOut
            .WithLabels(workflow, domain)
            .Inc();
        
        // Record instance duration if available  
        if (durationSeconds.HasValue)
        {
            WorkflowMetrics.InstanceDuration
                .WithLabels(workflow, "Timeout")
                .Observe(durationSeconds.Value);
        }
        
        // Decrement the current status gauge since instance will be completed after timeout
        DecrementStatusGauge(workflow, currentStatus);
    }

    public void RecordInstanceDuration(string workflow, string status, double durationSeconds)
    {
        WorkflowMetrics.InstanceDuration
            .WithLabels(workflow, status)
            .Observe(durationSeconds);
    }

    public void SetActiveInstances(string workflow, int count)
    {
        WorkflowMetrics.ActiveInstances
            .WithLabels(workflow)
            .Set(count);
    }

    public void UpdateInstanceStatusMetrics(string workflow, string oldStatus, string newStatus)
    {
        // Decrement the old status gauge
        DecrementStatusGauge(workflow, oldStatus);
        
        // Increment the new status gauge
        IncrementStatusGauge(workflow, newStatus);
    }

    /// <summary>
    /// Decrements the appropriate gauge based on instance status
    /// </summary>
    private void DecrementStatusGauge(string workflow, string status)
    {
        switch (status)
        {
            case var s when s == InstanceStatus.Active.Code: // Active
                WorkflowMetrics.ActiveInstances.WithLabels(workflow).Dec();
                break;
            case var s when s == InstanceStatus.Busy.Code: // Busy (Pending)
                WorkflowMetrics.PendingInstances.WithLabels(workflow).Dec();
                break;
            case var s when s == InstanceStatus.Passive.Code: // Passive (Suspended)
                WorkflowMetrics.SuspendedInstances.WithLabels(workflow).Dec();
                break;
        }
    }

    /// <summary>
    /// Increments the appropriate gauge based on instance status
    /// </summary>
    private void IncrementStatusGauge(string workflow, string status)
    {
        switch (status)
        {
            case var s when s == InstanceStatus.Active.Code: // Active
                WorkflowMetrics.ActiveInstances.WithLabels(workflow).Inc();
                break;
            case var s when s == InstanceStatus.Busy.Code: // Busy (Pending)
                WorkflowMetrics.PendingInstances.WithLabels(workflow).Inc();
                break;
            case var s when s == InstanceStatus.Passive.Code: // Passive (Suspended)
                WorkflowMetrics.SuspendedInstances.WithLabels(workflow).Inc();
                break;
        }
    }

    public void RecordTaskExecuted(string taskType, string workflow)
    {
        WorkflowMetrics.TasksExecuted
            .WithLabels(taskType, workflow)
            .Inc();
    }

    public void RecordTaskCompleted(string taskType, string workflow, double durationSeconds)
    {
        WorkflowMetrics.TasksCompleted
            .WithLabels(taskType, workflow)
            .Inc();
        
        WorkflowMetrics.TaskDuration
            .WithLabels(taskType, workflow)
            .Observe(durationSeconds);
    }

    public void RecordTaskFailed(string taskType, string workflow, double durationSeconds)
    {
        WorkflowMetrics.TasksFailed
            .WithLabels(taskType, workflow)
            .Inc();
        
        WorkflowMetrics.TaskDuration
            .WithLabels(taskType, workflow)
            .Observe(durationSeconds);
    }

    public void RecordTaskRetried(string taskType, string workflow)
    {
        WorkflowMetrics.TasksRetried
            .WithLabels(taskType, workflow)
            .Inc();
    }

    public void RecordTaskDuration(string taskType, double durationSeconds)
    {
        // This method is kept for backward compatibility
        // New code should use RecordTaskCompleted or RecordTaskFailed instead
    }

    public void RecordTaskQueueWait(string taskType, double waitDurationSeconds)
    {
        WorkflowMetrics.TaskQueueWaitDuration
            .WithLabels(taskType)
            .Observe(waitDurationSeconds);
    }

    public void IncrementPendingTasks(string taskType, string workflow)
    {
        WorkflowMetrics.TasksPending
            .WithLabels(taskType, workflow)
            .Inc();
    }

    public void StartTaskExecution(string taskType, string workflow)
    {
        WorkflowMetrics.TasksPending
            .WithLabels(taskType, workflow)
            .Dec();
        
        WorkflowMetrics.TasksRunning
            .WithLabels(taskType, workflow)
            .Inc();
    }

    public void FinishTaskExecution(string taskType, string workflow)
    {
        WorkflowMetrics.TasksRunning
            .WithLabels(taskType, workflow)
            .Dec();
    }

    public void SetTaskPoolSize(string taskType, int size)
    {
        WorkflowMetrics.TaskPoolSize
            .WithLabels(taskType)
            .Set(size);
    }

    public void RecordDbQueryDuration(string queryType, string table, double durationSeconds)
    {
        WorkflowMetrics.DbQueryDuration
            .WithLabels(queryType, table)
            .Observe(durationSeconds);
    }

    public void RecordDbTransactionDuration(string operation, double durationSeconds)
    {
        WorkflowMetrics.DbTransactionDuration
            .WithLabels(operation)
            .Observe(durationSeconds);
    }

    public void RecordDbQuery(string queryType, string table, string status)
    {
        WorkflowMetrics.DbQueries
            .WithLabels(queryType, table, status)
            .Inc();
    }

    public void RecordDbError(string operation, string table, string errorType)
    {
        WorkflowMetrics.DbErrors
            .WithLabels(operation, table, errorType)
            .Inc();
    }

    public void RecordDbConnection(string connectionType, string status)
    {
        WorkflowMetrics.DbConnections
            .WithLabels(connectionType, status)
            .Inc();
    }

    public void RecordCacheHit(string cacheName)
    {
        WorkflowMetrics.CacheHits
            .WithLabels(cacheName)
            .Inc();
    }

    public void RecordCacheMiss(string cacheName)
    {
        WorkflowMetrics.CacheMisses
            .WithLabels(cacheName)
            .Inc();
    }

    public void RecordCacheEviction(string cacheName, string reason)
    {
        WorkflowMetrics.CacheEvictions
            .WithLabels(cacheName, reason)
            .Inc();
    }

    public void SetCacheSize(string cacheName, long sizeBytes)
    {
        WorkflowMetrics.CacheSizeBytes
            .WithLabels(cacheName)
            .Set(sizeBytes);
    }

    public void SetCacheEntries(string cacheName, int entries)
    {
        WorkflowMetrics.CacheEntries
            .WithLabels(cacheName)
            .Set(entries);
    }

    public void RecordHttpRequest(string method, string endpoint, string statusCode)
    {
        WorkflowMetrics.HttpRequests
            .WithLabels(method, endpoint, statusCode)
            .Inc();
    }

    public void RecordHttpError(string method, string endpoint, string errorType)
    {
        WorkflowMetrics.HttpRequestErrors
            .WithLabels(method, endpoint, errorType)
            .Inc();
    }

    public void RecordHttpRequestDuration(string method, string endpoint, string statusCode, double durationSeconds)
    {
        WorkflowMetrics.HttpRequestDuration
            .WithLabels(method, endpoint, statusCode)
            .Observe(durationSeconds);
    }

    public void RecordHttpResponseSize(string method, string endpoint, string statusCode, long sizeBytes)
    {
        WorkflowMetrics.HttpResponseSize
            .WithLabels(method, endpoint, statusCode)
            .Observe(sizeBytes);
    }

    public void RecordJobExecuted(string jobType, string status)
    {
        WorkflowMetrics.JobsExecuted
            .WithLabels(jobType, status)
            .Inc();
    }

    public void SetJobsPending(string jobType, int count)
    {
        WorkflowMetrics.JobsPending
            .WithLabels(jobType)
            .Set(count);
    }

    public void RecordError(string errorType, string severity, string component)
    {
        WorkflowMetrics.Errors
            .WithLabels(errorType, severity, component)
            .Inc();
    }

    public void RecordStateTransition(string workflow, string fromState, string toState)
    {
        WorkflowMetrics.StateTransitions
            .WithLabels(workflow, fromState, toState)
            .Inc();
    }

    public void RecordStateEntry(string workflow, string state)
    {
        WorkflowMetrics.StateEntries
            .WithLabels(workflow, state)
            .Inc();
    }

    public void RecordStateDuration(string workflow, string state, double durationSeconds)
    {
        WorkflowMetrics.StateDuration
            .WithLabels(workflow, state)
            .Observe(durationSeconds);
    }

    public void SetTaskFactoryPoolSize(string taskType, int size)
    {
        WorkflowMetrics.TaskPoolSize
            .WithLabels(taskType)
            .Set(size);
    }

    public void SetTaskFactoryPoolAvailable(string taskType, int available)
    {
        WorkflowMetrics.TaskFactoryPoolAvailable
            .WithLabels(taskType)
            .Set(available);
    }

    public void SetTaskFactoryPoolInUse(string taskType, int inUse)
    {
        WorkflowMetrics.TaskFactoryPoolInUse
            .WithLabels(taskType)
            .Set(inUse);
    }

    public void RecordTaskFactoryPoolRental(string taskType)
    {
        WorkflowMetrics.TaskFactoryPoolRentals
            .WithLabels(taskType)
            .Inc();
    }

    public void RecordTaskFactoryPoolReturn(string taskType)
    {
        WorkflowMetrics.TaskFactoryPoolReturns
            .WithLabels(taskType)
            .Inc();
    }

    public void RecordTaskFactoryPoolCreate(string taskType)
    {
        WorkflowMetrics.TaskFactoryPoolCreates
            .WithLabels(taskType)
            .Inc();
    }

    public void RecordExternalServiceCall(string serviceName, string operation, string status)
    {
        WorkflowMetrics.ExternalServiceCalls
            .WithLabels(serviceName, operation, status)
            .Inc();
    }

    public void RecordExternalServiceFailure(string serviceName, string operation, string failureType)
    {
        WorkflowMetrics.ExternalServiceFailures
            .WithLabels(serviceName, operation, failureType)
            .Inc();
    }

    public void RecordExternalServiceTimeout(string serviceName, string operation, double timeoutThreshold)
    {
        WorkflowMetrics.ExternalServiceTimeouts
            .WithLabels(serviceName, operation, timeoutThreshold.ToString("F1"))
            .Inc();
    }

    public void RecordExternalServiceDuration(string serviceName, string operation, string status, double durationSeconds)
    {
        WorkflowMetrics.ExternalServiceDuration
            .WithLabels(serviceName, operation, status)
            .Observe(durationSeconds);
    }

    public void RecordDaprServiceInvocation(string serviceName, string methodName, string status)
    {
        WorkflowMetrics.DaprServiceInvocations
            .WithLabels(serviceName, methodName, status)
            .Inc();
    }

    public void RecordDaprPubsubMessagePublished(string? pubsubName, string topic, string status)
    {
        if (pubsubName != null)
            WorkflowMetrics.DaprPubsubMessagesPublished
                .WithLabels(pubsubName, topic, status)
                .Inc();
    }

    public void RecordDaprPubsubMessageReceived(string pubsubName, string topic, string status)
    {
        WorkflowMetrics.DaprPubsubMessagesReceived
            .WithLabels(pubsubName, topic, status)
            .Inc();
    }

    public void RecordDaprBindingInvocation(string bindingName, string operation, string status)
    {
        WorkflowMetrics.DaprBindingInvocations
            .WithLabels(bindingName, operation, status)
            .Inc();
    }

    public void RecordBackgroundJobScheduled(string jobType, string jobName)
    {
        WorkflowMetrics.BackgroundJobsScheduled
            .WithLabels(jobType, jobName)
            .Inc();
    }

    public void RecordBackgroundJobExecuted(string jobType, string jobName, string status)
    {
        WorkflowMetrics.BackgroundJobsExecuted
            .WithLabels(jobType, jobName, status)
            .Inc();
    }

    public void RecordBackgroundJobFailed(string jobType, string jobName, string failureReason)
    {
        WorkflowMetrics.BackgroundJobsFailed
            .WithLabels(jobType, jobName, failureReason)
            .Inc();
    }

    public void RecordBackgroundJobRetried(string jobType, string jobName, int retryCount)
    {
        WorkflowMetrics.BackgroundJobsRetried
            .WithLabels(jobType, jobName, retryCount.ToString())
            .Inc();
    }

    public void RecordBackgroundJobDuration(string jobType, string jobName, string status, double durationSeconds)
    {
        WorkflowMetrics.BackgroundJobDuration
            .WithLabels(jobType, jobName, status)
            .Observe(durationSeconds);
    }

    public void RecordBackgroundJobQueueWait(string jobType, string jobName, double waitDurationSeconds)
    {
        WorkflowMetrics.BackgroundJobQueueWait
            .WithLabels(jobType, jobName)
            .Observe(waitDurationSeconds);
    }

    public void SetBackgroundJobsPending(string jobType, int count)
    {
        WorkflowMetrics.BackgroundJobsPending
            .WithLabels(jobType)
            .Set(count);
    }

    public void SetBackgroundJobsRunning(string jobType, int count)
    {
        WorkflowMetrics.BackgroundJobsRunning
            .WithLabels(jobType)
            .Set(count);
    }

    public void RecordScriptExecution(string scriptType, string language, string status)
    {
        WorkflowMetrics.ScriptExecutions
            .WithLabels(scriptType, language, status)
            .Inc();
    }

    public void RecordScriptCompilationError(string scriptType, string language, string errorType)
    {
        WorkflowMetrics.ScriptCompilationErrors
            .WithLabels(scriptType, language, errorType)
            .Inc();
    }

    public void RecordScriptRuntimeError(string scriptType, string language, string errorType)
    {
        WorkflowMetrics.ScriptRuntimeErrors
            .WithLabels(scriptType, language, errorType)
            .Inc();
    }

    public void RecordScriptCompilationDuration(string scriptType, string language, string status, double durationSeconds)
    {
        WorkflowMetrics.ScriptCompilationDuration
            .WithLabels(scriptType, language, status)
            .Observe(durationSeconds);
    }

    public void RecordScriptExecutionDuration(string scriptType, string language, string status, double durationSeconds)
    {
        WorkflowMetrics.ScriptExecutionDuration
            .WithLabels(scriptType, language, status)
            .Observe(durationSeconds);
    }

    public void RecordWorkflowError(string errorType, string severity, string component)
    {
        WorkflowMetrics.WorkflowErrors
            .WithLabels(errorType, severity, component)
            .Inc();
    }

    public void RecordWorkflowException(string exceptionType, string component, string operation)
    {
        WorkflowMetrics.WorkflowExceptions
            .WithLabels(exceptionType, component, operation)
            .Inc();
    }

    public void RecordValidationFailure(string validationType, string component, string field)
    {
        WorkflowMetrics.ValidationFailures
            .WithLabels(validationType, component, field)
            .Inc();
    }

    public void SetWorkflowErrorRate(string component, double errorRate)
    {
        WorkflowMetrics.WorkflowErrorRate
            .WithLabels(component)
            .Set(errorRate);
    }

    public void SetWorkflowHealthStatus(string component, bool isHealthy)
    {
        WorkflowMetrics.WorkflowHealthStatus
            .WithLabels(component)
            .Set(isHealthy ? 1.0 : 0.0);
    }

    public void RecordTaskExecution(string taskType, string status)
    {
        WorkflowMetrics.TaskExecutions
            .WithLabels(taskType, status)
            .Inc();
        
        // Also record as completion if successful
        if (status == "success")
        {
            WorkflowMetrics.TaskCompletions
                .WithLabels(taskType, status)
                .Inc();
        }
    }

    public void RecordWorkflowInstanceCompletion(string workflowType, string status, double durationSeconds)
    {
        WorkflowMetrics.WorkflowInstanceCompletions
            .WithLabels(workflowType, status)
            .Inc();
            
        WorkflowMetrics.WorkflowInstanceDuration
            .WithLabels(workflowType, status)
            .Observe(durationSeconds);
    }

    public void SetActiveWorkflowInstances(string workflowType, int count)
    {
        WorkflowMetrics.ActiveWorkflowInstances
            .WithLabels(workflowType)
            .Set(count);
    }

    #region Error Boundary Metrics

    public void RecordErrorBoundaryResolution(string workflow, string level, string action)
    {
        WorkflowMetrics.ErrorBoundaryResolutions
            .WithLabels(workflow, level, action)
            .Inc();
    }

    public void RecordErrorBoundaryUnhandled(string workflow, string exceptionType, string scope)
    {
        WorkflowMetrics.ErrorBoundaryUnhandled
            .WithLabels(workflow, exceptionType, scope)
            .Inc();
    }

    public void RecordErrorBoundaryRetry(string workflow, string taskType, int attempt)
    {
        WorkflowMetrics.ErrorBoundaryRetries
            .WithLabels(workflow, taskType, attempt.ToString())
            .Inc();
    }

    public void RecordErrorActionDuration(string action, double durationSeconds)
    {
        WorkflowMetrics.ErrorActionDuration
            .WithLabels(action)
            .Observe(durationSeconds);
    }

    public void RecordSubFlowErrorPropagation(string parentWorkflow, string childWorkflow, bool propagated)
    {
        WorkflowMetrics.SubFlowErrorPropagation
            .WithLabels(parentWorkflow, childWorkflow, propagated.ToString().ToLower())
            .Inc();
    }

    #endregion
}