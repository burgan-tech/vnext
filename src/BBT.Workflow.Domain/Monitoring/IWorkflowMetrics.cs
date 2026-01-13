namespace BBT.Workflow.Monitoring;

/// <summary>
/// Interface for workflow metrics collection.
/// Provides abstraction for monitoring workflow operations without coupling to specific monitoring implementation.
/// </summary>
public interface IWorkflowMetrics
{
    /// <summary>
    /// Records workflow instance creation.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="domain">Domain identifier</param>
    void RecordInstanceCreated(string workflow, string domain);

    /// <summary>
    /// Records workflow instance completion.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="domain">Domain identifier</param>
    /// <param name="durationSeconds">Total execution duration in seconds</param>
    void RecordInstanceCompleted(string workflow, string domain, double? durationSeconds = null);

    /// <summary>
    /// Records workflow instance timeout.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="domain">Domain identifier</param>
    /// <param name="currentStatus">Current status before timeout</param>
    /// <param name="durationSeconds">Total execution duration in seconds until timeout</param>
    void RecordInstanceTimedOut(string workflow, string domain, string currentStatus, double? durationSeconds = null);

    /// <summary>
    /// Records workflow instance duration.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="status">Instance completion status</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordInstanceDuration(string workflow, string status, double durationSeconds);

    /// <summary>
    /// Updates active instances count.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="count">Current active count</param>
    void SetActiveInstances(string workflow, int count);

    /// <summary>
    /// Updates instance status metrics by transitioning from old status to new status.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="oldStatus">Previous instance status</param>
    /// <param name="newStatus">New instance status</param>
    void UpdateInstanceStatusMetrics(string workflow, string oldStatus, string newStatus);

    /// <summary>
    /// Records task execution start.
    /// </summary>
    /// <param name="taskType">Type of task executed</param>
    /// <param name="workflow">Workflow identifier</param>
    void RecordTaskExecuted(string taskType, string workflow);

    /// <summary>
    /// Records successful task completion.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordTaskCompleted(string taskType, string workflow, double durationSeconds);

    /// <summary>
    /// Records task failure.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="durationSeconds">Duration in seconds until failure</param>
    void RecordTaskFailed(string taskType, string workflow, double durationSeconds);

    /// <summary>
    /// Records task retry attempt.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="workflow">Workflow identifier</param>
    void RecordTaskRetried(string taskType, string workflow);

    /// <summary>
    /// Records task execution duration.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordTaskDuration(string taskType, double durationSeconds);

    /// <summary>
    /// Records task queue wait duration.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="waitDurationSeconds">Wait duration in seconds</param>
    void RecordTaskQueueWait(string taskType, double waitDurationSeconds);

    /// <summary>
    /// Increments pending tasks count when task is queued.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="workflow">Workflow identifier</param>
    void IncrementPendingTasks(string taskType, string workflow);

    /// <summary>
    /// Decrements pending tasks count and increments running tasks count when task starts execution.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="workflow">Workflow identifier</param>
    void StartTaskExecution(string taskType, string workflow);

    /// <summary>
    /// Decrements running tasks count when task finishes execution.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="workflow">Workflow identifier</param>
    void FinishTaskExecution(string taskType, string workflow);

    /// <summary>
    /// Updates task pool size.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="size">Current pool size</param>
    void SetTaskPoolSize(string taskType, int size);

    /// <summary>
    /// Records database query duration.
    /// </summary>
    /// <param name="queryType">Type of query</param>
    /// <param name="table">Target table</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordDbQueryDuration(string queryType, string table, double durationSeconds);

    /// <summary>
    /// Records database transaction duration.
    /// </summary>
    /// <param name="operation">Transaction operation type</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordDbTransactionDuration(string operation, double durationSeconds);

    /// <summary>
    /// Records database query execution.
    /// </summary>
    /// <param name="queryType">Type of query</param>
    /// <param name="table">Target table</param>
    /// <param name="status">Query execution status</param>
    void RecordDbQuery(string queryType, string table, string status);

    /// <summary>
    /// Records database operation error.
    /// </summary>
    /// <param name="operation">Database operation type</param>
    /// <param name="table">Target table</param>
    /// <param name="errorType">Type of error</param>
    void RecordDbError(string operation, string table, string errorType);

    /// <summary>
    /// Records database connection attempt.
    /// </summary>
    /// <param name="connectionType">Type of connection</param>
    /// <param name="status">Connection status</param>
    void RecordDbConnection(string connectionType, string status);

    /// <summary>
    /// Records cache hit.
    /// </summary>
    /// <param name="cacheName">Name of cache</param>
    void RecordCacheHit(string cacheName);

    /// <summary>
    /// Records cache miss.
    /// </summary>
    /// <param name="cacheName">Name of cache</param>
    void RecordCacheMiss(string cacheName);

    /// <summary>
    /// Records cache eviction.
    /// </summary>
    /// <param name="cacheName">Name of cache</param>
    /// <param name="reason">Eviction reason</param>
    void RecordCacheEviction(string cacheName, string reason);

    /// <summary>
    /// Sets current cache size in bytes.
    /// </summary>
    /// <param name="cacheName">Name of cache</param>
    /// <param name="sizeBytes">Cache size in bytes</param>
    void SetCacheSize(string cacheName, long sizeBytes);

    /// <summary>
    /// Sets number of cached entries.
    /// </summary>
    /// <param name="cacheName">Name of cache</param>
    /// <param name="entries">Number of cached entries</param>
    void SetCacheEntries(string cacheName, int entries);

    /// <summary>
    /// Records HTTP request.
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="endpoint">Endpoint path</param>
    /// <param name="statusCode">Response status code</param>
    void RecordHttpRequest(string method, string endpoint, string statusCode);

    /// <summary>
    /// Records HTTP request error.
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="endpoint">Endpoint path</param>
    /// <param name="errorType">Type of error</param>
    void RecordHttpError(string method, string endpoint, string errorType);

    /// <summary>
    /// Records HTTP request duration.
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="endpoint">Endpoint path</param>
    /// <param name="statusCode">Response status code</param>
    /// <param name="durationSeconds">Request duration in seconds</param>
    void RecordHttpRequestDuration(string method, string endpoint, string statusCode, double durationSeconds);

    /// <summary>
    /// Records HTTP response size.
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="endpoint">Endpoint path</param>
    /// <param name="statusCode">Response status code</param>
    /// <param name="sizeBytes">Response size in bytes</param>
    void RecordHttpResponseSize(string method, string endpoint, string statusCode, long sizeBytes);

    /// <summary>
    /// Records background job execution.
    /// </summary>
    /// <param name="jobType">Type of job</param>
    /// <param name="status">Job completion status</param>
    void RecordJobExecuted(string jobType, string status);

    /// <summary>
    /// Updates pending jobs count.
    /// </summary>
    /// <param name="jobType">Type of job</param>
    /// <param name="count">Current pending count</param>
    void SetJobsPending(string jobType, int count);

    /// <summary>
    /// Records error occurrence.
    /// </summary>
    /// <param name="errorType">Type of error</param>
    /// <param name="severity">Error severity level</param>
    /// <param name="component">Component where error occurred</param>
    void RecordError(string errorType, string severity, string component);

    /// <summary>
    /// Records state transition.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="fromState">Source state</param>
    /// <param name="toState">Target state</param>
    void RecordStateTransition(string workflow, string fromState, string toState);

    /// <summary>
    /// Records state entry.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="state">State being entered</param>
    void RecordStateEntry(string workflow, string state);

    /// <summary>
    /// Records state duration.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="state">State identifier</param>
    /// <param name="durationSeconds">Duration spent in state in seconds</param>
    void RecordStateDuration(string workflow, string state, double durationSeconds);

    /// <summary>
    /// Sets current pool size for a task type.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="size">Current pool size</param>
    void SetTaskFactoryPoolSize(string taskType, int size);

    /// <summary>
    /// Sets available objects count in pool for a task type.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="available">Available objects count</param>
    void SetTaskFactoryPoolAvailable(string taskType, int available);

    /// <summary>
    /// Sets objects currently in use count for a task type.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="inUse">Objects in use count</param>
    void SetTaskFactoryPoolInUse(string taskType, int inUse);

    /// <summary>
    /// Records object rental from pool.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    void RecordTaskFactoryPoolRental(string taskType);

    /// <summary>
    /// Records object return to pool.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    void RecordTaskFactoryPoolReturn(string taskType);

    /// <summary>
    /// Records new object creation for pool.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    void RecordTaskFactoryPoolCreate(string taskType);

    #region External Service Integration Metrics

    /// <summary>
    /// Records external service call.
    /// </summary>
    /// <param name="serviceName">Name of external service</param>
    /// <param name="operation">Operation or endpoint called</param>
    /// <param name="status">Status of the call (success/failure)</param>
    void RecordExternalServiceCall(string serviceName, string operation, string status);

    /// <summary>
    /// Records external service call failure.
    /// </summary>
    /// <param name="serviceName">Name of external service</param>
    /// <param name="operation">Operation or endpoint called</param>
    /// <param name="failureType">Type of failure (e.g., network, timeout, server_error)</param>
    void RecordExternalServiceFailure(string serviceName, string operation, string failureType);

    /// <summary>
    /// Records external service call timeout.
    /// </summary>
    /// <param name="serviceName">Name of external service</param>
    /// <param name="operation">Operation or endpoint called</param>
    /// <param name="timeoutThreshold">Timeout threshold in seconds</param>
    void RecordExternalServiceTimeout(string serviceName, string operation, double timeoutThreshold);

    /// <summary>
    /// Records external service call duration.
    /// </summary>
    /// <param name="serviceName">Name of external service</param>
    /// <param name="operation">Operation or endpoint called</param>
    /// <param name="status">Status of the call</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordExternalServiceDuration(string serviceName, string operation, string status, double durationSeconds);

    #endregion

    #region DAPR Integration Metrics

    /// <summary>
    /// Records Dapr service invocation.
    /// </summary>
    /// <param name="serviceName">Name of the invoked service</param>
    /// <param name="methodName">Method or endpoint invoked</param>
    /// <param name="status">Status of the invocation (success/failure)</param>
    void RecordDaprServiceInvocation(string serviceName, string methodName, string status);

    /// <summary>
    /// Records Dapr pub/sub message published.
    /// </summary>
    /// <param name="pubsubName">Name of the pub/sub component</param>
    /// <param name="topic">Topic name</param>
    /// <param name="status">Status of the publish (success/failure)</param>
    void RecordDaprPubsubMessagePublished(string? pubsubName, string topic, string status);

    /// <summary>
    /// Records Dapr pub/sub message received.
    /// </summary>
    /// <param name="pubsubName">Name of the pub/sub component</param>
    /// <param name="topic">Topic name</param>
    /// <param name="status">Status of the receive (success/failure)</param>
    void RecordDaprPubsubMessageReceived(string pubsubName, string topic, string status);

    /// <summary>
    /// Records Dapr binding invocation.
    /// </summary>
    /// <param name="bindingName">Name of the binding component</param>
    /// <param name="operation">Operation type (invoke/create/read/update/delete)</param>
    /// <param name="status">Status of the invocation (success/failure)</param>
    void RecordDaprBindingInvocation(string bindingName, string operation, string status);

    #endregion

    #region Background Jobs Metrics

    /// <summary>
    /// Records background job scheduled.
    /// </summary>
    /// <param name="jobType">Type of the job</param>
    /// <param name="jobName">Name of the job</param>
    void RecordBackgroundJobScheduled(string jobType, string jobName);

    /// <summary>
    /// Records background job executed.
    /// </summary>
    /// <param name="jobType">Type of the job</param>
    /// <param name="jobName">Name of the job</param>
    /// <param name="status">Status of the execution (success/failure)</param>
    void RecordBackgroundJobExecuted(string jobType, string jobName, string status);

    /// <summary>
    /// Records background job failed.
    /// </summary>
    /// <param name="jobType">Type of the job</param>
    /// <param name="jobName">Name of the job</param>
    /// <param name="failureReason">Reason for the failure</param>
    void RecordBackgroundJobFailed(string jobType, string jobName, string failureReason);

    /// <summary>
    /// Records background job retry.
    /// </summary>
    /// <param name="jobType">Type of the job</param>
    /// <param name="jobName">Name of the job</param>
    /// <param name="retryCount">Current retry count</param>
    void RecordBackgroundJobRetried(string jobType, string jobName, int retryCount);

    /// <summary>
    /// Records background job execution duration.
    /// </summary>
    /// <param name="jobType">Type of the job</param>
    /// <param name="jobName">Name of the job</param>
    /// <param name="status">Status of the execution</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordBackgroundJobDuration(string jobType, string jobName, string status, double durationSeconds);

    /// <summary>
    /// Records background job queue wait time.
    /// </summary>
    /// <param name="jobType">Type of the job</param>
    /// <param name="jobName">Name of the job</param>
    /// <param name="waitDurationSeconds">Wait duration in seconds</param>
    void RecordBackgroundJobQueueWait(string jobType, string jobName, double waitDurationSeconds);

    /// <summary>
    /// Sets the count of pending background jobs.
    /// </summary>
    /// <param name="jobType">Type of the jobs</param>
    /// <param name="count">Number of pending jobs</param>
    void SetBackgroundJobsPending(string jobType, int count);

    /// <summary>
    /// Sets the count of currently running background jobs.
    /// </summary>
    /// <param name="jobType">Type of the jobs</param>
    /// <param name="count">Number of running jobs</param>
    void SetBackgroundJobsRunning(string jobType, int count);

    #endregion

    #region Script Engine Metrics

    /// <summary>
    /// Records script execution.
    /// </summary>
    /// <param name="scriptType">Type of script (evaluation, compilation)</param>
    /// <param name="language">Script language (csharp, etc.)</param>
    /// <param name="status">Status of execution (success/failure)</param>
    void RecordScriptExecution(string scriptType, string language, string status);

    /// <summary>
    /// Records script compilation error.
    /// </summary>
    /// <param name="scriptType">Type of script being compiled</param>
    /// <param name="language">Script language</param>
    /// <param name="errorType">Type of compilation error</param>
    void RecordScriptCompilationError(string scriptType, string language, string errorType);

    /// <summary>
    /// Records script runtime error.
    /// </summary>
    /// <param name="scriptType">Type of script that failed</param>
    /// <param name="language">Script language</param>
    /// <param name="errorType">Type of runtime error</param>
    void RecordScriptRuntimeError(string scriptType, string language, string errorType);

    /// <summary>
    /// Records script compilation duration.
    /// </summary>
    /// <param name="scriptType">Type of script compiled</param>
    /// <param name="language">Script language</param>
    /// <param name="status">Compilation status</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordScriptCompilationDuration(string scriptType, string language, string status, double durationSeconds);

    /// <summary>
    /// Records script execution duration.
    /// </summary>
    /// <param name="scriptType">Type of script executed</param>
    /// <param name="language">Script language</param>
    /// <param name="status">Execution status</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordScriptExecutionDuration(string scriptType, string language, string status, double durationSeconds);

    #endregion

    #region Error Handling and System Health Metrics

    /// <summary>
    /// Records workflow error.
    /// </summary>
    /// <param name="errorType">Type of error (validation, business, system, etc.)</param>
    /// <param name="severity">Severity level (low, medium, high, critical)</param>
    /// <param name="component">Component where error occurred</param>
    void RecordWorkflowError(string errorType, string severity, string component);

    /// <summary>
    /// Records unhandled exception.
    /// </summary>
    /// <param name="exceptionType">Type of exception</param>
    /// <param name="component">Component where exception occurred</param>
    /// <param name="operation">Operation being performed</param>
    void RecordWorkflowException(string exceptionType, string component, string operation);

    /// <summary>
    /// Records validation failure.
    /// </summary>
    /// <param name="validationType">Type of validation that failed</param>
    /// <param name="component">Component performing validation</param>
    /// <param name="field">Field or rule that failed</param>
    void RecordValidationFailure(string validationType, string component, string field);

    /// <summary>
    /// Sets the current error rate.
    /// </summary>
    /// <param name="component">Component for which error rate is being set</param>
    /// <param name="errorRate">Error rate as a percentage (0.0 to 100.0)</param>
    void SetWorkflowErrorRate(string component, double errorRate);

    /// <summary>
    /// Sets the overall system health status.
    /// </summary>
    /// <param name="component">Component for which health status is being set</param>
    /// <param name="isHealthy">Health status (true=healthy, false=unhealthy)</param>
    void SetWorkflowHealthStatus(string component, bool isHealthy);

    #endregion

    #region Additional Dashboard Metrics

    /// <summary>
    /// Records task execution completion.
    /// </summary>
    /// <param name="taskType">Type of task</param>
    /// <param name="status">Execution status (success, failure, cancelled)</param>
    void RecordTaskExecution(string taskType, string status);

    /// <summary>
    /// Records workflow instance completion.
    /// </summary>
    /// <param name="workflowType">Type of workflow</param>
    /// <param name="status">Completion status (completed, failed, cancelled)</param>
    /// <param name="durationSeconds">Total instance duration in seconds</param>
    void RecordWorkflowInstanceCompletion(string workflowType, string status, double durationSeconds);

    /// <summary>
    /// Sets count of active workflow instances.
    /// </summary>
    /// <param name="workflowType">Type of workflow</param>
    /// <param name="count">Number of active instances</param>
    void SetActiveWorkflowInstances(string workflowType, int count);

    #endregion

    #region Error Boundary Metrics

    /// <summary>
    /// Records error boundary policy resolution.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="level">Boundary level (Task, State, Global)</param>
    /// <param name="action">Action taken (Abort, Retry, Rollback, Ignore, Notify, Log)</param>
    void RecordErrorBoundaryResolution(string workflow, string level, string action);

    /// <summary>
    /// Records unhandled error (no matching policy found).
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="exceptionType">Type of the exception</param>
    /// <param name="scope">Scope where error occurred (Task, State, Global)</param>
    void RecordErrorBoundaryUnhandled(string workflow, string exceptionType, string scope);

    /// <summary>
    /// Records error boundary retry attempt.
    /// </summary>
    /// <param name="workflow">Workflow identifier</param>
    /// <param name="taskType">Type of task being retried</param>
    /// <param name="attempt">Current retry attempt number</param>
    void RecordErrorBoundaryRetry(string workflow, string taskType, int attempt);

    /// <summary>
    /// Records error action execution duration.
    /// </summary>
    /// <param name="action">Action type (Abort, Retry, etc.)</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    void RecordErrorActionDuration(string action, double durationSeconds);

    /// <summary>
    /// Records SubFlow error propagation.
    /// </summary>
    /// <param name="parentWorkflow">Parent workflow identifier</param>
    /// <param name="childWorkflow">Child workflow identifier</param>
    /// <param name="propagated">Whether error was propagated to parent</param>
    void RecordSubFlowErrorPropagation(string parentWorkflow, string childWorkflow, bool propagated);

    #endregion
}