using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow.Definitions;

/// <summary>
/// It is in the possible statuses found in the flow.
/// </summary>
public sealed class State : IHasKey
{
    private State()
    {
    }

    private State(
        string key,
        StateType stateType,
        StateSubType subType)
    {
        SetKey(key);
        StateType = stateType;
        SubType = subType;
    
        labels = [];
        onEntries = [];
        onExits = [];
    }

    [JsonConstructor]
    private State(
        string key,
        StateType stateType,
        StateSubType subType,
        VersionStrategy versionStrategy,
        List<LanguageLabel>? labels,
        List<OnExecuteTask>? onEntries,
         List<OnExecuteTask>? onExits)
        : this(key, stateType, subType)
    {
        VersionStrategy = versionStrategy;
        this.labels = labels ?? [];
        this.onEntries = onEntries ?? [];
        this.onExits = onExits ?? [];
        // view property will be set by ViewDefinitionJsonConverter via JsonInclude attribute
    }

    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// Version strategy
    /// </summary>
    public VersionStrategy VersionStrategy { get; private set; }

    /// <summary>
    /// <see cref="StateType"/>
    /// </summary>
    public StateType StateType { get; private set; }

    /// <summary>
    /// <see cref="SubType"/>
    /// </summary>
    public StateSubType SubType { get; private set; }

    [JsonInclude] [JsonPropertyName("labels")]
    private List<LanguageLabel> labels = new();

    [JsonInclude] [JsonPropertyName("transitions")]
    private List<Transition> transitions = new();

    [JsonInclude] [JsonPropertyName("onEntries")]
    private List<OnExecuteTask> onEntries = new();

    [JsonInclude] [JsonPropertyName("onExits")]
    private List<OnExecuteTask> onExits = new();

    [JsonInclude] [JsonPropertyName("subFlow")]
    public SubFlow? SubFlow { get; private set; }
    
    [JsonInclude]
    [JsonPropertyName("view")]
    [JsonConverter(typeof(ViewDefinitionJsonConverter))]
    public ViewDefinition? view  { get; private set; }
    [JsonInclude]
    [JsonPropertyName("views")]
    [JsonConverter(typeof(ViewDefinitionJsonConverter))]
    public ViewDefinition? views { get; private set; }
    
    /// <summary>
    /// State-level error boundary.
    /// Applied when no task-level boundary handles the error.
    /// </summary>
    [JsonInclude] [JsonPropertyName("errorBoundary")]
    public ErrorBoundary? ErrorBoundary { get; private set; }

    /// <summary>
    /// Languages
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<LanguageLabel> Labels => labels.AsReadOnly();

    /// <summary>
    /// State view
    /// </summary>
    [JsonIgnore]
    public ViewDefinition? View => views==null?view:views;

    /// <summary>
    /// Transitions
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<Transition> Transitions => transitions.AsReadOnly();

    /// <summary>
    /// On entries
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<OnExecuteTask> OnEntries => onEntries.AsReadOnly();

    /// <summary>
    /// On exits
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<OnExecuteTask> OnExits => onExits.AsReadOnly();

    private void SetKey(string key)
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), StateConstants.MaxKeyLength);
    }

    public void AddLanguage(string label, string language)
    {
        if (labels.All(l => l.Language != language))
        {
            labels.Add(new LanguageLabel(label, language));
        }
        else
        {
            var languageLabel = labels.First(p => p.Language == language);
            labels.Remove(languageLabel);
            labels.Add(new LanguageLabel(label, language));
        }
    }

    public void AddOnEntry(OnExecuteTask task)
    {
        onEntries.Add(task);
    }

    public void AddOnExit(OnExecuteTask task)
    {
        onExits.Add(task);
    }

    public void SetView(ViewDefinition viewDefinition)
    {
        view = viewDefinition;
    }
    
    public void SetSubFlow(string type, IReference reference, ScriptCode mapping, Dictionary<string, Reference>? viewOverrides)
    {
        SubFlow = SubFlow.Create(type, reference, mapping, viewOverrides);
    }

    public void SetErrorBoundary(ErrorBoundary errorBoundary)
    {
        ErrorBoundary = errorBoundary;
    }

    public void AddTransition(Transition transition)
    {
        transitions.Add(transition);
    }

    public Transition? FindTransition(string key)
    {
        return Transitions.FirstOrDefault(t => t.Key == key);
    }

    public IEnumerable<Transition> AutoTransitions => Transitions.Where(p => p.TriggerType == TriggerType.Automatic);
    
    public IEnumerable<Transition> ScheduledTransitions => Transitions.Where(p => p.TriggerType == TriggerType.Scheduled);

    /// <summary>
    /// Returns true if the state has only manual/event transitions or no transitions at all.
    /// Used to determine if instance should become Available after transition.
    /// </summary>
    public bool HasOnlyManualOrEventTransitions => 
        !Transitions.Any() || 
        Transitions.All(t => t.TriggerType == TriggerType.Manual || t.TriggerType == TriggerType.Event);

    public IReadOnlyList<string> TransitionKeys() => Transitions.Select(t => t.Key).ToList();

    public static State Create(
        string key,
        StateType stateType,
        StateSubType stateSubType,
        string versionStrategy
    )
    {
        return new State(
            key,
            stateType,
            stateSubType)
        {
            VersionStrategy = VersionStrategy.FromCode(versionStrategy)
        };
    }
}