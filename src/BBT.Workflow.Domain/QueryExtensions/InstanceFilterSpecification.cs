using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Definitions;

public class InstanceFilterSpecification : FilterSpecification<Instance>
{
    public InstanceFilterSpecification(string? filter)
        : base(filter, CreateFilterMappings())
    {
    }

    private static Dictionary<string, Func<string, Expression<Func<Instance, bool>>>> CreateFilterMappings()
    {
        return new Dictionary<string, Func<string, Expression<Func<Instance, bool>>>>
        {
            ["attributes"] = value => 
            {
                try
                {
                    var match = KeyValueRegex.Match(value);
                    if (!match.Success) return x => false;

                    var jsonString = match.Groups[2].Value;
                    return x=> x.DataList != null && x.DataList.Any(dtList=>EF.Functions.JsonContains(dtList.Data.Json, jsonString));
                }
                catch
                {
                    return x => false;
                }
            },
            ["status"] = value => 
            {
                var status = (InstanceStatus)Enum.Parse(typeof(InstanceStatus), value, true);
                return x => x.Status == status;
            },
            ["effectiveStateType"] = value => 
            {
                var stateType = (StateType)Enum.Parse(typeof(StateType), value, true);
                return x => x.EffectiveStateType == stateType;
            },
            ["effectiveStateSubType"] = value => 
            {
                var stateSubType = (StateSubType)Enum.Parse(typeof(StateSubType), value, true);
                return x => x.EffectiveStateSubType == stateSubType;
            },
            ["currentStateType"] = value =>
            {
                var stateType = (StateType)Enum.Parse(typeof(StateType), value, true);
                return x => x.CurrentStateType == stateType;
            },
            ["currentStateSubType"] = value =>
            {
                var stateSubType = (StateSubType)Enum.Parse(typeof(StateSubType), value, true);
                return x => x.CurrentStateSubType == stateSubType;
            },
            ["stage"] = value => x => x.Stage == value,
            ["flow"] = value => x => x.Flow == value,
            ["key"] = value => 
            {
                var match = KeyValueRegex.Match(value);
                if (!match.Success) return x => false;
                var keyValue = match.Groups[2].Value;
                return x => x.Key == keyValue;
            },
            ["tag"] = value => 
            {
                var match = KeyValueRegex.Match(value);
                if (!match.Success) return x => false;
                var tagValue = match.Groups[2].Value;
                return x => x.Tags.Contains(tagValue);
            }
        };
    }
} 