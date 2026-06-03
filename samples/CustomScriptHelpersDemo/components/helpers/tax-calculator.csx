// CONSUMER-SUPPLIED HELPER (compiled at runtime into the sandboxed ScriptHelpers assembly).
// Plain C# — only allowed namespaces. A mapping script can call Acme.Helpers.TaxCalculator.
using System;

namespace Acme.Helpers;

public static class TaxCalculator
{
    public const decimal StandardRate = 0.18m;

    /// <summary>Adds standard VAT to a net amount, rounded to 2 decimals.</summary>
    public static decimal WithTax(decimal net) => Math.Round(net * (1 + StandardRate), 2);

    public static decimal TaxAmount(decimal net) => Math.Round(net * StandardRate, 2);
}
