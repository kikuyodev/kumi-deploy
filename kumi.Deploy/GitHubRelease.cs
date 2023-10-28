using Newtonsoft.Json;

namespace kumi.Deploy;

public class GitHubRelease
{
    [JsonProperty(@"id")]
    public int Id;

    [JsonProperty(@"tag_name")]
    public string TagName => $"{Name}";

    [JsonProperty(@"name")]
    public string Name = string.Empty;

    [JsonProperty(@"draft")]
    public bool Draft;

    [JsonProperty("prerelease")]
    public bool PreRelease;

    [JsonProperty(@"upload_url")]
    public string UploadUrl = string.Empty;
}
