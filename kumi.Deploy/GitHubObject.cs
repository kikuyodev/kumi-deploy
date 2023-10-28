using Newtonsoft.Json;

namespace kumi.Deploy;

public class GitHubObject
{
    [JsonProperty(@"id")]
    public int Id;

    [JsonProperty(@"name")]
    public string Name = string.Empty;
}
