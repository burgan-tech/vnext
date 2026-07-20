using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Human Task Definition
/// </summary>
public class HumanTask : WorkflowTask
{
    private HumanTask()
    {
        
    }
    
    [JsonConstructor]
    private HumanTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.Human).ToString();
    }
    
    /// <summary>
    /// Title
    /// </summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>
    /// Instructions
    /// </summary>
    public string Instructions { get; private set; } = string.Empty;

    /// <summary>
    /// AssignedTo
    /// </summary>
    public string AssignedTo { get; private set; } = string.Empty;

    /// <summary>
    /// DueDate
    /// </summary>
    public DateTime? DueDate { get; private set; }

    /// <summary>
    /// Form
    /// </summary>
    [JsonConverter(typeof(SafeJsonElementConverter))]
    public JsonElement Form { get; private set; }

    /// <summary>
    /// ReminderIntervalMinutes
    /// </summary>
    public int ReminderIntervalMinutes { get; private set; }

    /// <summary>
    /// EscalationTimeoutMinutes
    /// </summary>
    public int EscalationTimeoutMinutes { get; private set; }

    /// <summary>
    /// EscalationAssignee
    /// </summary>
    public string EscalationAssignee { get; private set; } = string.Empty;

    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetTitleInternal(string title) => Title = title;
    internal void SetInstructionsInternal(string instructions) => Instructions = instructions;
    internal void SetAssignedToInternal(string assignedTo) => AssignedTo = assignedTo;
    internal void SetDueDateInternal(DateTime? dueDate) => DueDate = dueDate;
    internal void SetFormInternal(JsonElement form) => Form = form;
    internal void SetReminderIntervalMinutesInternal(int reminderIntervalMinutes) => ReminderIntervalMinutes = reminderIntervalMinutes;
    internal void SetEscalationTimeoutMinutesInternal(int escalationTimeoutMinutes) => EscalationTimeoutMinutes = escalationTimeoutMinutes;
    internal void SetEscalationAssigneeInternal(string escalationAssignee) => EscalationAssignee = escalationAssignee;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("title", out var title))
            Title = title.GetString() ?? throw new ArgumentNullException(nameof(title));

        if (config.TryGetProperty("instructions", out var instructions))
            Instructions = instructions.GetString() ?? throw new ArgumentNullException(nameof(instructions));

        if (config.TryGetProperty("assignedTo", out var assignedTo))
            AssignedTo = assignedTo.GetString() ?? throw new ArgumentNullException(nameof(assignedTo));

        if (config.TryGetProperty("dueDate", out var dueDate))
            DueDate = dueDate.GetDateTime();

        if (config.TryGetProperty("form", out var form))
            Form = form;

        if (config.TryGetProperty("reminderIntervalMinutes", out var reminder))
            ReminderIntervalMinutes = reminder.GetInt32();

        if (config.TryGetProperty("escalationTimeoutMinutes", out var timeout))
            EscalationTimeoutMinutes = timeout.GetInt32();

        if (config.TryGetProperty("escalationAssignee", out var escalation))
            EscalationAssignee = escalation.GetString() ?? throw new ArgumentNullException(nameof(escalation));
    }

    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }
    
    /// <summary>
    /// Creates a typed deep copy of the current HumanTask instance.
    /// </summary>
    public HumanTask CloneTyped()
    {
        var cloned = new HumanTask();
        CopyBaseTo(cloned);
        
        cloned.Title = Title;
        cloned.Instructions = Instructions;
        cloned.AssignedTo = AssignedTo;
        cloned.DueDate = DueDate;
        cloned.Form = Form;
        cloned.ReminderIntervalMinutes = ReminderIntervalMinutes;
        cloned.EscalationTimeoutMinutes = EscalationTimeoutMinutes;
        cloned.EscalationAssignee = EscalationAssignee;
        
        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(HumanTask source)
    {
        source.CopyBaseToInternal(this);
        SetTitleInternal(source.Title);
        SetInstructionsInternal(source.Instructions);
        SetAssignedToInternal(source.AssignedTo);
        SetDueDateInternal(source.DueDate);
        SetFormInternal(source.Form);
        SetReminderIntervalMinutesInternal(source.ReminderIntervalMinutes);
        SetEscalationTimeoutMinutesInternal(source.EscalationTimeoutMinutes);
        SetEscalationAssigneeInternal(source.EscalationAssignee);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        Title = string.Empty;
        Instructions = string.Empty;
        AssignedTo = string.Empty;
        DueDate = null;
        Form = default;
        ReminderIntervalMinutes = 0;
        EscalationTimeoutMinutes = 0;
        EscalationAssignee = string.Empty;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static HumanTask CreateEmpty()
    {
        return new HumanTask();
    }

    public static HumanTask Create(
        JsonElement config)
    {
        return new HumanTask(config);
    }
}