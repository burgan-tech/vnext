using System.Threading.Tasks;
using BBT.Workflow.Scripting.Evaluators;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Pins the split cache-key algorithm: <see cref="CSharpEvaluator.BuildProfile"/> +
/// <see cref="CSharpEvaluator.ComputeCacheKey"/> must be usable independently by a caller (the
/// engine) to precompute a key that routes to the very same compiled type as the raw
/// <c>CompileToInstanceAsync</c> path, and the profile itself must be order-insensitive for the
/// same inputs that today's <c>GenerateCacheKey</c> treats as order-insensitive.
/// </summary>
public class CSharpEvaluatorCacheKeyTests
{
    public interface ICacheKeyProbe
    {
        int Run();
    }

    private static Microsoft.CodeAnalysis.MetadataReference[] ProbeReferences() =>
    [
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
            System.Reflection.Assembly.Load("System.Runtime").Location),
        Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
            typeof(CSharpEvaluatorCacheKeyTests).Assembly.Location)
    ];

    [Fact]
    public async Task PrecomputedKey_MustEqualComputedKey_AndServeSameCompiledType()
    {
        var evaluator = new CSharpEvaluator();
        var source = "public class KeyProbe : BBT.Workflow.Scripting.CSharpEvaluatorCacheKeyTests.ICacheKeyProbe { public int Run() => 7; }";
        var refs = ProbeReferences();

        // 1) Normal yol derler
        var first = await evaluator.CompileToInstanceAsync<ICacheKeyProbe>(source, extraReferences: refs);
        first.Compiled.ShouldBeTrue();

        // 2) Precomputed yol: profile + sourceHash'ten üretilen anahtar AYNI derlenmiş tipe hit etmeli
        var profile = evaluator.BuildProfile(refs, usingDirectives: null, sandboxGrant: null, loadContext: null);
        var sourceHash = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(source)));
        var key = evaluator.ComputeCacheKey(sourceHash, typeof(ICacheKeyProbe), profile);

        var second = await evaluator.CompileToInstanceAsync<ICacheKeyProbe>(
            source, extraReferences: refs, precomputedCacheKey: key);

        second.Compiled.ShouldBeFalse(); // hit — iki yol aynı anahtarı üretti
        second.Instance.GetType().ShouldBe(first.Instance.GetType());
    }

    [Fact]
    public void BuildProfile_IsOrderInsensitive_ForUsingsAndGrant()
    {
        var evaluator = new CSharpEvaluator();
        var p1 = evaluator.BuildProfile(null, new[] { "System", "System.Linq" }, new[] { "B", "A" }, null);
        var p2 = evaluator.BuildProfile(null, new[] { "System.Linq", "System" }, new[] { "A", "B" }, null);
        p1.ShouldBe(p2); // OrderBy + OrdinalIgnoreCase grant — bugünkü GenerateCacheKey semantiği
        // (grant sıralaması case-insensitive ama grant METNİ case-preserving — bu yüzden test verisi
        // burada sadece SIRAYI değiştiriyor, harf büyüklüğünü değil: farklı case farklı anahtar üretir,
        // GenerateCacheKey'in bugünkü davranışı budur.)
    }
}
