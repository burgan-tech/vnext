using System.Text;

namespace BBT.Workflow.Benchmarks;

/// <summary>Deterministic nested-JSON payloads of a requested approximate size.</summary>
public static class PayloadFactory
{
    /// <summary>Produces ~<paramref name="approxKb"/> KB of nested JSON (objects + arrays).</summary>
    public static string Json(int approxKb)
    {
        var sb = new StringBuilder(approxKb * 1024 + 256);
        sb.Append("{\"customer\":{\"name\":\"Benchmark User\",\"segment\":\"retail\"},\"items\":[");
        var i = 0;
        while (sb.Length < approxKb * 1024)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(i)
              .Append(",\"sku\":\"SKU-").Append(i.ToString("D8"))
              .Append("\",\"amount\":").Append(((i * 37 % 10000) / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"tags\":[\"a\",\"b\",\"c\"],\"meta\":{\"channel\":\"web\",\"retry\":false}}");
            i++;
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
