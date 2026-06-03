// CONSUMER-SUPPLIED MAPPING SCRIPT.
// Inherits the runtime ScriptBase (GetConfig/LogInformation come from the base)
// and calls the custom helper Acme.Helpers.TaxCalculator that was loaded from the
// script-helpers folder. The engine adds the helper namespace as an auto-using,
// so no explicit 'using Acme.Helpers;' is needed here.

public class OrderMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(ScriptContext context)
    {
        var net = Convert.ToDecimal(context.Data["netAmount"]);

        LogInformation($"Pricing order on transition '{context.TransitionKey}' (currency {GetConfig("currency", "TRY")})");

        // Custom helper from the consumer's own code:
        var gross = TaxCalculator.WithTax(net);
        var tax = TaxCalculator.TaxAmount(net);

        // Custom RSA helper — keys are supplied by the host via config (GetConfig),
        // not generated inside the helper. Encrypt a sensitive field, verify round-trip:
        var card = Convert.ToString(context.Data["cardNumber"]) ?? "";
        var publicKey = GetConfig("rsa:publicKey");
        var privateKey = GetConfig("rsa:privateKey");
        var encryptedCard = RsaCryptoHelper.Encrypt(card, publicKey);
        var roundTripOk = RsaCryptoHelper.Decrypt(encryptedCard, privateKey) == card;

        return Task.FromResult(new ScriptResponse
        {
            Data = new Dictionary<string, object?>
            {
                ["net"] = net,
                ["tax"] = tax,
                ["gross"] = gross,
                ["currency"] = GetConfig("currency", "TRY"),
                ["encryptedCard"] = encryptedCard,
                ["cardRoundTripOk"] = roundTripOk,
            }
        });
    }
}
