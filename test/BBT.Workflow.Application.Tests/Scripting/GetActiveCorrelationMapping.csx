using System;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

public class GetActiveCorrelationMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var daprTask = task as DaprServiceTask;
        string? MethodName = daprTask?.MethodName.Replace("{workflow}", context.Workflow.Key);
        MethodName = MethodName?.Replace("{domain}", context.Runtime.Domain);
        MethodName = MethodName?.Replace("{instance}", context.Instance.Id.ToString());
        daprTask?.SetMethodName(MethodName);
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
         var responseData = context.Body?.data;
        return Task.FromResult(new ScriptResponse()
        {
            Data = responseData.activeCorrelations
        });
    }

}