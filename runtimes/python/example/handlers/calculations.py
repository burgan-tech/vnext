from vnext_runtime import HandlerContext, handler


@handler("sum_values")
def sum_values(input):
    return {"total": sum(input["values"])}


@handler("risk_score")
def risk_score(input, context: HandlerContext):
    score = min(100, input.get("income", 0) // 1000)
    return {"score": score, "instanceId": context.instance_id}
