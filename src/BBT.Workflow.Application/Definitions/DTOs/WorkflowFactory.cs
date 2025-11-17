using BBT.Workflow.Shared;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Factory class responsible for creating and configuring Workflow instances based on input specifications.
/// Provides methods to build complete workflows with all their components including states, transitions, functions, and features.
/// </summary>
public static class WorkflowFactory
{
    /// <summary>
    /// Creates a new Workflow instance based on the provided input configuration.
    /// This method constructs a complete workflow by setting up the base workflow properties
    /// and adding all specified components including states, transitions, functions, and features.
    /// </summary>
    /// <param name="input">The publish input containing all workflow configuration data including attributes, states, transitions, and other components.</param>
    /// <returns>A fully configured Workflow instance ready for use.</returns>
    /// <exception cref="ArgumentNullException">Thrown when input parameter is null.</exception>
    public static Workflow CreateWorkflow(PublishInput input)
    {
        var workflow = CreateBaseWorkflow(input);
        AddWorkflowComponents(input.Attributes, workflow);
        return workflow;
    }

    /// <summary>
    /// Creates the base workflow structure with core properties like reference, type, timeout, and labels.
    /// This method establishes the fundamental workflow configuration before adding components.
    /// </summary>
    /// <param name="input">The publish input containing basic workflow information.</param>
    /// <returns>A base Workflow instance with core properties configured.</returns>
    private static Workflow CreateBaseWorkflow(PublishInput input)
    {
        var workflow = Workflow.Create();
        workflow.SetReference(new Reference(
            input.Key,
            input.Domain,
            input.Key,
            input.Version
        ));

        workflow.SetType(input.Attributes.Type);

        if (input.Attributes.Timeout != null)
        {
            workflow.SetTimeout(WorkflowTimeout.Create(
                    input.Attributes.Timeout.Key,
                    input.Attributes.Timeout.Target,
                    input.Attributes.Timeout.VersionStrategy,
                    input.Attributes.Timeout.Timer.Reset,
                    input.Attributes.Timeout.Timer.Duration
                )
            );
        }

        foreach (var label in input.Attributes.Labels)
        {
            workflow.AddLanguage(label.Label, label.Language);
        }

        // Set master schema for instance data validation
        if (input.Attributes.Schema != null)
        {
            workflow.SetSchema(input.Attributes.Schema);
        }

        // Set instance data validation configuration
        if (input.Attributes.InstanceDataValidation != null)
        {
            workflow.SetInstanceDataValidation(new InstanceDataValidationConfig
            {
                ValidationMode = (InstanceDataValidationMode)input.Attributes.InstanceDataValidation.ValidationMode
            });
        }

        return workflow;
    }

    /// <summary>
    /// Adds all workflow components including functions, features, extensions, transitions, and states
    /// to the specified workflow instance.
    /// </summary>
    /// <param name="input">The workflow creation input containing component definitions.</param>
    /// <param name="workflow">The workflow instance to add components to.</param>
    private static void AddWorkflowComponents(CreateWorkflowInput input, Workflow workflow)
    {
        AddFunctions(workflow, input.Functions ?? []);
        AddFeatures(workflow, input.Features ?? []);
        AddExtensions(workflow, input.Extensions ?? []);
        AddTransitions(workflow, input);
        AddStates(workflow, input.States);
    }

    /// <summary>
    /// Adds function references to the workflow. Functions define reusable business logic
    /// that can be called during workflow execution.
    /// </summary>
    /// <param name="workflow">The workflow instance to add functions to.</param>
    /// <param name="inputFunctions">List of function reference inputs to be added.</param>
    private static void AddFunctions(Workflow workflow, List<ReferenceInput> inputFunctions)
    {
        foreach (var functionInput in inputFunctions)
        {
            workflow.AddFunction(functionInput);
        }
    }

    /// <summary>
    /// Adds feature references to the workflow. Features provide additional capabilities
    /// and extensions that can be utilized during workflow execution.
    /// </summary>
    /// <param name="workflow">The workflow instance to add features to.</param>
    /// <param name="inputFeatures">List of feature reference inputs to be added.</param>
    private static void AddFeatures(Workflow workflow, List<ReferenceInput> inputFeatures)
    {
        foreach (var featureInput in inputFeatures)
        {
            workflow.AddFeature(featureInput);
        }
    }

    /// <summary>
    /// Adds extension configurations to the workflow. Extensions provide custom functionality
    /// and integrations that extend the workflow's capabilities.
    /// </summary>
    /// <param name="workflow">The workflow instance to add extensions to.</param>
    /// <param name="inputExtensions">List of extension configurations to be added.</param>
    private static void AddExtensions(Workflow workflow, List<ReferenceInput> inputExtensions)
    {
        foreach (var extensionInput in inputExtensions)
        {
            workflow.AddExtension(extensionInput);
        }
    }

    /// <summary>
    /// Adds shared transitions and start transition to the workflow. Transitions define
    /// the possible movements between workflow states and their associated conditions.
    /// </summary>
    /// <param name="workflow">The workflow instance to add transitions to.</param>
    /// <param name="input">The workflow creation input containing transition definitions.</param>
    private static void AddTransitions(Workflow workflow, CreateWorkflowInput input)
    {
        var transitionInputs = input.SharedTransitions ?? [];
        foreach (var transitionInput in transitionInputs)
        {
            workflow.AddSharedTransition(
                BindingTransition(transitionInput)
            );
        }

        if (input.StartTransition != null)
        {
            workflow.SetStartTransition(
                BindingTransition(input.StartTransition)
            );
        }
    }

    /// <summary>
    /// Creates and configures a Transition instance based on the provided input.
    /// This method binds all transition properties including labels, rules, timers, views, and execution tasks.
    /// </summary>
    /// <param name="transitionInput">The transition input containing configuration data.</param>
    /// <returns>A fully configured Transition instance.</returns>
    private static Transition BindingTransition(CreateTransitionInput transitionInput)
    {
        var transition = Transition.Create(
            transitionInput.Key,
            transitionInput.From,
            transitionInput.Target,
            transitionInput.TriggerType,
            transitionInput.VersionStrategy
        );

        foreach (var label in transitionInput.Labels)
        {
            transition.AddLanguage(label.Label, label.Language);
        }

        if (transitionInput.AvailableIn != null)
            foreach (var item in transitionInput.AvailableIn)
            {
                transition.AddAvailableIn(item);
            }

        if (transitionInput.Rule != null)
        {
            transition.SetRule(
                transitionInput.Rule.Location,
                transitionInput.Rule.Code
            );
        }

        if (transitionInput.Timer != null)
        {
            transition.SetTimer(
                transitionInput.Timer.Location,
                transitionInput.Timer.Code
            );
        }
        
        if (transitionInput.Mapping != null)
        {
            transition.SetMapping(
                transitionInput.Mapping.Location,
                transitionInput.Mapping.Code
            );
        }

        if (transitionInput.View != null)
            transition.SetView(transitionInput.View.ToViewDefinition());

        if (transitionInput.OnExecutionTasks != null)
            foreach (var taskInput in transitionInput.OnExecutionTasks)
            {
                transition.AddOnExecutionTask(
                    OnExecuteTask.Create(
                        taskInput.Order,
                        taskInput.Task,
                        new ScriptCode(taskInput.Mapping.Location, taskInput.Mapping.Code,taskInput.Mapping.Type)
                    )
                );
            }

        if (transitionInput.Schema != null)
        {
            transition.SetSchema(transitionInput.Schema);
        }

        return transition;
    }

    /// <summary>
    /// Adds state definitions to the workflow. States represent the various stages
    /// in the workflow lifecycle and contain their own transitions, entry/exit tasks, and configurations.
    /// </summary>
    /// <param name="workflow">The workflow instance to add states to.</param>
    /// <param name="inputStates">List of state input configurations to be added.</param>
    private static void AddStates(Workflow workflow, List<CreateStateInput> inputStates)
    {
        foreach (var stateInput in inputStates)
        {
            var state = State.Create(
                stateInput.Key,
                stateInput.StateType,
                stateInput.VersionStrategy
            );

            foreach (var label in stateInput.Labels)
            {
                state.AddLanguage(label.Label, label.Language);
            }

            if (stateInput.Transitions != null)
            {
                foreach (var transitionInput in stateInput.Transitions)
                {
                    state.AddTransition(
                        BindingTransition(transitionInput)
                    );
                }
            }

            if (stateInput.OnEntries != null)
                foreach (var taskInput in stateInput.OnEntries)
                {
                    state.AddOnEntry(
                        OnExecuteTask.Create(
                            taskInput.Order,
                            taskInput.Task,
                            new ScriptCode(taskInput.Mapping.Location, taskInput.Mapping.Code,taskInput.Mapping.Type)
                        )
                    );
                }

            if (stateInput.OnExits != null)
                foreach (var taskInput in stateInput.OnExits)
                {
                    state.AddOnExit(OnExecuteTask.Create(
                            taskInput.Order,
                            taskInput.Task,
                            new ScriptCode(taskInput.Mapping.Location, taskInput.Mapping.Code,taskInput.Mapping.Type)
                        )
                    );
                }

            if (stateInput.View != null)
            {
                state.SetView(stateInput.View.ToViewDefinition());
            }

            if (stateInput.SubFlow != null)
            {
                state.SetSubFlow(
                    stateInput.SubFlow.Type, 
                    stateInput.SubFlow.Process, 
                    new ScriptCode(stateInput.SubFlow.Mapping.Location, stateInput.SubFlow.Mapping.Code),
                    stateInput.SubFlow.ViewOverrides);
            }

            workflow.AddState(state);
        }
    }
}