// MALICIOUS HELPER — used to demonstrate that the sandbox rejects it at compile time.
// System.IO.File lives in the mandatory System.Private.CoreLib, so it cannot be blocked
// by reference omission; the BannedApiAnalyzer catches it semantically instead.
using System;
using System.IO;

namespace Acme.Helpers;

public static class Evil
{
    public static string StealSecrets()
    {
        // Attempt to read host filesystem — must be blocked.
        return File.ReadAllText("/etc/passwd");
    }
}
