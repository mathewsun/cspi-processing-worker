using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Cspi.ProcessingWorker;

public sealed record SemanticArticle(int Id,string Title,string? Body,DateTime Published,int? Source,long Generation,string Token)
{
 public string Input=>LocalCountryClassifier.Limit(Title,600)+"\n"+LocalCountryClassifier.Limit(Body??"",900);
 public string Hash=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Source}:{Published.ToUniversalTime():O}\n{Input}")));
}
public static class LocalSemantic
{
 public const string Revision="minilm-ae06c001-q8-mean128-v1";
 public const string Sha="98a01d88b7de996cdea58c32ca71208c09968d143798814b2ea09d3439dc334f";
 public const string TokenizerSha="2c3387be76557bd40970cec13153b3bbf80407865484b209e655e5e4729076b8";
 public static string Vector(float[] vector)=>"["+string.Join(',',vector.Select(x=>x.ToString("R",CultureInfo.InvariantCulture)))+"]";
 public static async Task Verify(HttpClient client,CancellationToken ct)
 {
  using var response=await client.GetAsync("health",ct);response.EnsureSuccessStatusCode();using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));Validate(data.RootElement);
 }
 static void Validate(JsonElement root)
 {if(root.GetProperty("model").GetString()!=Revision||root.GetProperty("sha256").GetString()!=Sha||root.GetProperty("tokenizer_sha256").GetString()!=TokenizerSha||root.GetProperty("dimensions").GetInt32()!=384||root.GetProperty("max_tokens").GetInt32()!=128)throw new InvalidDataException("Pinned embedding model mismatch");}
 public static async Task<float[][]> Infer(HttpClient client,IReadOnlyList<SemanticArticle> articles,CancellationToken ct)
 {
  using var response=await client.PostAsJsonAsync("v1/embeddings",new{model=Revision,input=articles.Select(x=>x.Input).ToArray()},ct);response.EnsureSuccessStatusCode();
  using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));Validate(data.RootElement);
  var rows=data.RootElement.GetProperty("data");if(rows.GetArrayLength()!=articles.Count)throw new InvalidDataException("Embedding count mismatch");
  var vectors=new float[articles.Count][];
  foreach(var row in rows.EnumerateArray())
  {
   var i=row.GetProperty("index").GetInt32();if(i<0||i>=vectors.Length||vectors[i]!=null)throw new InvalidDataException("Embedding index invalid");
   var vector=row.GetProperty("embedding").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
   if(vector.Length!=384||vector.Any(x=>!float.IsFinite(x))||Math.Abs(vector.Sum(x=>(double)x*x)-1)>0.02)throw new InvalidDataException("Embedding vector invalid");vectors[i]=vector;
  }
  return vectors;
 }
}
