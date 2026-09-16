using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BBT.Workflow.Data.ValueConverters;

internal class InstanceTypeConverter() : ValueConverter<InstanceType, string>(type => type.Code,
    code => InstanceType.FromCode(code));
