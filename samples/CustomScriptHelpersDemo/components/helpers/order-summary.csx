// CONSUMER-SUPPLIED HELPER that CALLS ANOTHER HELPER.
// OrderSummary depends on TaxCalculator. Because both helpers are referenced by the
// same mapping, they are compiled together into one assembly, so this cross-helper
// call resolves with no 'using' (same Acme.Helpers namespace) and no extra reference.
using System;
using System.Collections.Generic;

namespace Acme.Helpers;

public static class OrderSummary
{
    public static Dictionary<string, object?> Build(decimal net) => new()
    {
        ["net"]   = net,
        ["tax"]   = TaxCalculator.TaxAmount(net),   // <-- another helper
        ["gross"] = TaxCalculator.WithTax(net),     // <-- another helper
    };
}
