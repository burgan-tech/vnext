// CONSUMER-SUPPLIED HELPER that uses a THIRD-PARTY NuGet package (Newtonsoft.Json).
// This only compiles because:
//   1. The operator mounted Newtonsoft.Json.dll into the plugin directory (a Docker volume), and
//   2. it is allow-listed in Scripting:Sandbox:AllowedAssemblies (the global baseline).
// The sandbox analyzer still polices THIS source, but cannot see inside Newtonsoft —
// third-party DLLs are full-trust, hence operator-curated only.
using Newtonsoft.Json;

namespace Acme.Helpers;

public static class JsonHelper
{
    public static string Serialize(object value) => JsonConvert.SerializeObject(value);
}
