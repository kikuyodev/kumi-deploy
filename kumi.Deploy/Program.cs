using System.Configuration;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using osu.Framework;
using osu.Framework.IO.Network;
using Spectre.Console;

namespace kumi.Deploy;

public static class Program
{
    private static string packages => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    private static string nugetPath => getToolPath("NuGet.CommandLine", "NuGet.exe");
    private static string squirrelPath => getToolPath("Clowd.Squirrel", "Squirrel.exe");

    private const string staging_folder = "staging";
    private const string releases_folder = "releases";

    private const int keep_delta_count = 4;
    
    public static readonly string? GITHUB_ACCESS_TOKEN = ConfigurationManager.AppSettings["GitHubAccessToken"];
    public static readonly bool GITHUB_UPLOAD = bool.Parse(ConfigurationManager.AppSettings["GitHubUpload"] ?? "false");
    public static readonly string? GITHUB_USERNAME = ConfigurationManager.AppSettings["GitHubUsername"];
    public static readonly string? GITHUB_REPO_NAME = ConfigurationManager.AppSettings["GitHubRepoName"];
    public static readonly string? SOLUTION_NAME = ConfigurationManager.AppSettings["SolutionName"];
    public static readonly string? PROJECT_NAME = ConfigurationManager.AppSettings["ProjectName"];
    public static readonly string? NUSPEC_NAME = ConfigurationManager.AppSettings["NuSpecName"];
    public static readonly bool INCREMENT_VERSION = bool.Parse(ConfigurationManager.AppSettings["IncrementVersion"] ?? "true");
    public static readonly string? PACKAGE_NAME = ConfigurationManager.AppSettings["PackageName"];
    public static readonly string? CODE_SIGNING_CERTIFICATE = ConfigurationManager.AppSettings["CodeSigningCertificate"];

    public static string GitHubApiEndpoint => $"https://api.github.com/repos/{GITHUB_USERNAME}/{GITHUB_REPO_NAME}/releases";

    private static string? solutionPath;

    private static string stagingPath => Path.Combine(Environment.CurrentDirectory, staging_folder);
    private static string releasesPath => Path.Combine(Environment.CurrentDirectory, releases_folder);

    private static readonly Stopwatch stopwatch = new Stopwatch();

    private static bool interactive;
    
    /// <summary>
    /// args[0]: code signing passphrase
    /// args[1]: version
    /// args[2]: platform
    /// </summary>
    public static void Main(string[] args)
    {
        interactive = args.Length == 0;
        displayHeader();

        findSolutionPath();

        if (!Directory.Exists(releases_folder))
        {
            AnsiConsole.MarkupLine("[yellow][bold]WARNING:[/] No release directory found. Make sure you want this![/]");
            Directory.CreateDirectory(releases_folder);
        }

        GitHubRelease? lastRelease = null;

        if (canGithub)
        {
            AnsiConsole.MarkupLine("Checking GitHub releases...");
            lastRelease = getLastGithubRelease();

            if (lastRelease != null)
                AnsiConsole.MarkupLine($"[gray]Last release: {lastRelease.Name}[/]");
            else
            {
                AnsiConsole.MarkupLine("No releases found.");
                AnsiConsole.MarkupLine("[bold yellow]This will be the first release, make sure you want this![/]");
            }

            Console.WriteLine();
        }

        var verBase = DateTime.Now.ToString("yyyy.Mdd.");
        var increment = 0;

        if (lastRelease?.TagName.StartsWith(verBase, StringComparison.InvariantCulture) ?? false)
            increment = int.Parse(lastRelease.TagName.Split('.')[2]) + (INCREMENT_VERSION ? 1 : 0);

        var version = $"{verBase}{increment}";
        var targetPlatform = RuntimeInfo.OS;

        if (args.Length > 1 && !string.IsNullOrEmpty(args[1]))
            version = args[1];
        if (args.Length > 2 && !string.IsNullOrEmpty(args[2]))
            Enum.TryParse(args[2], true, out targetPlatform);

        AnsiConsole.MarkupLine($"[gray]Increment Version:   [/]{INCREMENT_VERSION}");
        AnsiConsole.MarkupLine($"[gray]Signing Certificate: [/]{CODE_SIGNING_CERTIFICATE}");
        AnsiConsole.MarkupLine($"[gray]Upload to GitHub:    [/]{GITHUB_UPLOAD}");
        Console.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Ready to deploy version {version} on platform {targetPlatform}![/]");
        
        pauseIfInteractive();
        
        stopwatch.Start();
        
        refreshDirectory(staging_folder);
        Debug.Assert(solutionPath != null);

        AnsiConsole.Status()
           .Start("Running build process...", ctx =>
            {
                switch (targetPlatform)
                {
                    case RuntimeInfo.Platform.Windows:
                        if (lastRelease != null)
                            getAssetsFromRelease(lastRelease);

                        runCommand("dotnet", $"publish -f net7.0 -r win-x64 {PROJECT_NAME} -o {stagingPath} --configuration Release /p:Version={version}");

                        ctx.Status("Creating NuGet deployment package...");
                        runCommand(nugetPath, $"pack {NUSPEC_NAME} -Version {version} -Properties Configuration=Deploy -OutputDirectory {stagingPath} -BasePath {stagingPath}");

                        pruneReleases();
                        checkReleaseFiles();

                        ctx.Status("Running squirrel build...");

                        var codeSigningCmd = string.Empty;

                        if (!string.IsNullOrEmpty(CODE_SIGNING_CERTIFICATE))
                        {
                            string? codeSigningPassword = null;

                            if (args.Length > 0)
                            {
                                codeSigningPassword = args[0];
                            }

                            codeSigningCmd = string.IsNullOrEmpty(codeSigningPassword)
                                                 ? ""
                                                 : $"--signParams=\"/td sha256 /fd sha256 /f {CODE_SIGNING_CERTIFICATE} /p {codeSigningPassword} /tr http://timestamp.comodoca.com\"";
                        }

                        var nupkgFilename = $"{PACKAGE_NAME}.{version}.nupkg";
                        
                        runCommand(squirrelPath,
                            $"releasify --package={stagingPath}\\{nupkgFilename} --releaseDir={releasesPath} {codeSigningCmd}");

                        pruneReleases();
                        
                        File.Copy(Path.Combine(releases_folder, "kikuyodev.kumiSetup.exe"), Path.Combine(releases_folder, "install.exe"), true);
                        File.Delete(Path.Combine(releases_folder, "kikuyodev.kumiSetup.exe"));
                        break;

                    case RuntimeInfo.Platform.Linux:
                        break;

                    case RuntimeInfo.Platform.macOS:
                        break;

                    case RuntimeInfo.Platform.iOS:
                        break;

                    case RuntimeInfo.Platform.Android:
                        break;
                }
            });

        if (GITHUB_UPLOAD)
            uploadBuild(version);
        
        AnsiConsole.MarkupLine("[bold green]Done![/]");
        pauseIfInteractive();
    }

    private static void displayHeader()
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("  [bold #FF674D]Kumi Deployer :rocket:[/]");
        AnsiConsole.MarkupLine("  [#FF674D]===============[/]");
        Console.WriteLine();
    }

    private static void checkReleaseFiles()
    {
        if (!canGithub)
            return;

        var releaseLines = getReleaseLines();

        foreach (var l in releaseLines)
        {
            if (!File.Exists(Path.Combine(releases_folder, l.Filename)))
                AnsiConsole.MarkupLine($"[red]Local file missing {l.Filename}[/]");
        }
    }

    private static IEnumerable<ReleaseLine> getReleaseLines()
        => File.ReadAllLines(Path.Combine(releases_folder, "RELEASES")).Select(i => new ReleaseLine(i));
    
    private static void pruneReleases()
    {
        if (!canGithub)
            return;
        
        AnsiConsole.MarkupLine("Pruning RELEASES...");

        var releasesLines = getReleaseLines().ToList();
        var fulls = releasesLines.Where(l => l.Filename.Contains("-full")).Reverse().Skip(1);

        foreach (var l in fulls)
        {
            AnsiConsole.MarkupLine($"[yellow]- Removing old release {l.Filename}[/]");
            File.Delete(Path.Combine(releases_folder, l.Filename));
            releasesLines.Remove(l);
        }

        var deltas = releasesLines.Where(l => l.Filename.Contains("-delta")).ToArray();
        if (deltas.Length > keep_delta_count)
        {
            foreach (var l in deltas.Take(deltas.Length - keep_delta_count))
            {
                AnsiConsole.MarkupLine($"[yellow]- Removing old delta {l.Filename}[/]");
                File.Delete(Path.Combine(releases_folder, l.Filename));
                releasesLines.Remove(l);
            }
        }

        var lines = new List<string>();
        releasesLines.ForEach(l => lines.Add(l.ToString()));
        File.WriteAllLines(Path.Combine(releases_folder, "RELEASES"), lines);
    }

    private static void uploadBuild(string version)
    {
        if (!canGithub)
            return;
        
        AnsiConsole.MarkupLine("Publishing to GitHub...");

        var req = new JsonWebRequest<GitHubRelease>($"{GitHubApiEndpoint}")
        {
            Method = HttpMethod.Post
        };

        GitHubRelease? targetRelease = getLastGithubRelease(true);

        if (targetRelease == null || targetRelease.TagName != version)
        {
            AnsiConsole.MarkupLine($"[yellow]- Creating release {version}...[/]");
            req.AddRaw(JsonConvert.SerializeObject(new GitHubRelease
            {
                Name = version,
                Draft = true
            }));
            req.AuthenticatedBlockingPerform();

            targetRelease = req.ResponseObject;
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]- Adding to existing release {version}...[/]");
        }
        
        Debug.Assert(targetRelease.UploadUrl != null);

        var assetUploadUrl = targetRelease.UploadUrl.Replace("{?name,label}", "?name={0}");
        foreach (var a in Directory.GetFiles(releases_folder).Reverse())
        {
            if (Path.GetFileName(a).StartsWith('.'))
                continue;
            
            AnsiConsole.MarkupLine($"[yellow]- Adding asset {a}...[/]");
            var upload = new WebRequest(assetUploadUrl, Path.GetFileName(a))
            {
                Method = HttpMethod.Post,
                Timeout = 240000,
                ContentType = "application/octet-stream"
            };
            
            upload.AddRaw(File.ReadAllBytes(a));
            upload.AuthenticatedBlockingPerform();
        }

        openGitHubReleasePage();
    }

    private static void openGitHubReleasePage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"https://github.com/{GITHUB_USERNAME}/{GITHUB_REPO_NAME}/releases",
            UseShellExecute = true
        });
    }
    
    private static bool canGithub => !string.IsNullOrEmpty(GITHUB_ACCESS_TOKEN);

    private static GitHubRelease? getLastGithubRelease(bool includeDrafts = false)
    {
        var req = new JsonWebRequest<List<GitHubRelease>>($"{GitHubApiEndpoint}");
        req.AuthenticatedBlockingPerform();
        return req.ResponseObject.FirstOrDefault(r => includeDrafts || !r.Draft);
    }

    private static void getAssetsFromRelease(GitHubRelease release)
    {
        if (!canGithub)
            return;

        var assetReq = new JsonWebRequest<List<GitHubObject>>($"{GitHubApiEndpoint}/{release.Id}/assets");
        assetReq.AuthenticatedBlockingPerform();
        var assets = assetReq.ResponseObject;

        var releaseAsset = assets.FirstOrDefault(a => a.Name == "RELEASES");
        
        if (releaseAsset == null)
            return;

        var requireDownload = false;
        
        if (!File.Exists(Path.Combine(releases_folder, $"{PACKAGE_NAME}-{release.Name}-full.nupkg")))
        {
            AnsiConsole.MarkupLine("[red]Last version's package not found locally.[/]");
            requireDownload = true;
        }
        else
        {
            var lastReleases = new RawFileWebRequest($"{GitHubApiEndpoint}/assets/{releaseAsset.Id}");
            lastReleases.AuthenticatedBlockingPerform();

            if (File.ReadAllText(Path.Combine(releases_folder, "RELEASES")) != lastReleases.GetResponseString())
            {
                AnsiConsole.MarkupLine("[red]Server's RELEASES differed from ours.[/]");
                requireDownload = true;
            }
        }
        
        if (!requireDownload)
            return;

        Console.WriteLine("Refreshing local releases directory...");
        refreshDirectory(releases_folder);

        AnsiConsole.Progress()
           .HideCompleted(false)
           .Start(ctx =>
            {
                foreach (var a in assets)
                {
                    if (a.Name != "RELEASES" && !a.Name.EndsWith(".nupkg", StringComparison.InvariantCulture))
                        continue;

                    var task = ctx.AddTask($"[yellow]- Downloading {a.Name}[/]", maxValue: 1d);
                    var req = new FileWebRequest(Path.Combine(releases_folder, a.Name), $"{GitHubApiEndpoint}/assets/{a.Id}");

                    req.DownloadProgress += (progress, length) =>
                    {
                        task.Value(progress / (double)length);
                    };
                    
                    req.AuthenticatedBlockingPerform();
                }
            });
    }

    private static void refreshDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);
    }

    private static void findSolutionPath()
    {
        var path = Path.GetDirectoryName(Environment.CommandLine.Replace("\"", "").Trim());

        if (string.IsNullOrEmpty(path))
            path = Environment.CurrentDirectory;

        while (true)
        {
            if (File.Exists(Path.Combine(path, $"{SOLUTION_NAME}.sln")))
                break;

            if (Directory.Exists(Path.Combine(path, "kumi")) && File.Exists(Path.Combine(path, "kumi", $"{SOLUTION_NAME}.sln")))
            {
                path = Path.Combine(path, "kumi");
                break;
            }

            path = path.Remove(path.LastIndexOf(Path.DirectorySeparatorChar));
        }

        path += Path.DirectorySeparatorChar;
        solutionPath = path;
    }

    private static bool runCommand(string command, string args, bool useSolutionPath = true)
    {
        AnsiConsole.MarkupLine($"[gray]Running: [/]{command} {args}");

        var psi = new ProcessStartInfo(command, args)
        {
            WorkingDirectory = useSolutionPath ? solutionPath : Environment.CurrentDirectory,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        var p = Process.Start(psi);
        if (p == null) return false;

        var output = p.StandardOutput.ReadToEnd();
        output += p.StandardError.ReadToEnd();

        p.WaitForExit();

        if (p.ExitCode == 0)
            return true;
        
        AnsiConsole.MarkupLine("[red]Command failed![/]");
        AnsiConsole.MarkupLineInterpolated($"[red]{output}[/]");
        return false;
    }

    private static string getToolPath(string packageName, string toolExecutable)
    {
        var process = Process.Start(new ProcessStartInfo("dotnet", "list kumi.Deploy/kumi.Deploy.csproj package")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        
        Debug.Assert(process != null);

        process.WaitForExit();

        var output = process.StandardOutput.ReadToEnd();
        var match = Regex.Matches(output, $@"(?m){packageName.Replace(".", "\\.")}.*\s(\d{{1,3}}\.\d{{1,3}}\.\d.*?)$");

        if (match.Count == 0)
            throw new InvalidOperationException($"Missing tool for {toolExecutable}");

        return Path.Combine(packages, packageName.ToLowerInvariant(), match[0].Groups[1].Value.Trim(), "tools", toolExecutable);
    }

    private static void error(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[default on red]FATAL ERROR:[/] [red]{message}[/]");
        
        pauseIfInteractive();
        Environment.Exit(-1);
    }

    private static void pauseIfInteractive()
    {
        if (interactive)
            Console.ReadLine();
        else
        {
            Console.WriteLine();
        }
    }

    private static void AuthenticatedBlockingPerform(this WebRequest r)
    {
        r.AddHeader("Authorization", $"token {GITHUB_ACCESS_TOKEN}");
        r.Perform();
    }

    internal class RawFileWebRequest : WebRequest
    {
        public RawFileWebRequest(string url)
            : base(url)
        {
        }

        protected override string Accept => "application/octet-stream";
    }

    internal class ReleaseLine
    {
        public readonly string Hash;
        public readonly string Filename;
        public readonly int Filesize;

        public ReleaseLine(string line)
        {
            var split = line.Split(' ');
            Hash = split[0];
            Filename = split[1];
            Filesize = int.Parse(split[2]);
        }
        
        public override string ToString() => $"{Hash} {Filename} {Filesize}";
    }
}