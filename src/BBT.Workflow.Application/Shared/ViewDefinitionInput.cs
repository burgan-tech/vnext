namespace BBT.Workflow.Shared;
public class ViewDefinitionInput
{
    public string[] Extensions { get; set; }
    public bool LoadData { get; set; }
    public ReferenceInput View { get; set; }
    public ViewDefinition ToViewDefinition() {
        return ViewDefinition.CreateDefault(View.ToReference(), Extensions, LoadData);
    }
}