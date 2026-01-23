using System.Diagnostics;

using IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((_, services) => services.AddGitHubActionServices())
    .Build();

static TService Get<TService>(IHost host)
    where TService : notnull =>
    host.Services.GetRequiredService<TService>();

static async Task StartAnalysisAsync(ActionInputs inputs, IHost host)
{
    using ProjectWorkspace workspace = Get<ProjectWorkspace>(host);
    using CancellationTokenSource tokenSource = new();

    Console.CancelKeyPress += delegate
    {
        tokenSource.Cancel();
    };

    var projectAnalyzer = Get<ProjectMetricDataAnalyzer>(host);

    var workspaceRoot = string.IsNullOrWhiteSpace(inputs.WorkspaceDirectory)
        ? Directory.GetCurrentDirectory()
        : inputs.WorkspaceDirectory;

    var analysisRoot = Path.IsPathRooted(inputs.Directory)
        ? Path.GetFullPath(inputs.Directory)
        : Path.GetFullPath(Path.Combine(workspaceRoot, inputs.Directory));

    Matcher matcher = new();
    matcher.AddIncludePatterns(new[] { "**/*.csproj", "**/*.vbproj" });

    var allProjects = matcher.GetResultsInFullPath(analysisRoot).ToArray();
    Dictionary<string, CodeAnalysisMetricData> metricData = new(StringComparer.OrdinalIgnoreCase);

    var projects = inputs.ChangedOnly
        ? await FilterProjectsToChangedAsync(allProjects, workspaceRoot, inputs, Get<ILoggerFactory>(host).CreateLogger(nameof(StartAnalysisAsync)))
        : allProjects;

    foreach (var project in projects)
    {
        var metrics =
            await projectAnalyzer.AnalyzeAsync(
                workspace, project, tokenSource.Token);

        foreach (var (path, metric) in metrics)
        {
            metricData[path] = metric;
        }
    }

    var updatedMetrics = false;
    var title = "";
    StringBuilder summary = new();
    if (metricData is { Count: > 0 })
    {
        var fileName = "CODE_METRICS.md";
        var fullPath = Path.Combine(analysisRoot, fileName);
        var logger = Get<ILoggerFactory>(host).CreateLogger(nameof(StartAnalysisAsync));
        var fileExists = File.Exists(fullPath);

        logger.LogInformation(
            $"{(fileExists ? "Updating" : "Creating")} {fileName} markdown file with latest code metric data.");

        summary.AppendLine(
            title = $"{(fileExists ? "Updated" : "Created")} {fileName} file, analyzed metrics for {metricData.Count} projects.");

        foreach (var (path, _) in metricData)
        {
            summary.AppendLine($"- *{path}*");
        }

        var contents = metricData.ToMarkDownBody(inputs);
        await File.WriteAllTextAsync(
            fullPath,
            contents,
            tokenSource.Token);

        updatedMetrics = true;
    }
    else
    {
        summary.Append("No metrics were determined.");
    }

    // https://docs.github.com/actions/reference/workflow-commands-for-github-actions#setting-an-output-parameter
    // ::set-output deprecated as mentioned in https://github.blog/changelog/2022-10-11-github-actions-deprecating-save-state-and-set-output-commands/
    var githubOutputFile = Environment.GetEnvironmentVariable("GITHUB_OUTPUT", EnvironmentVariableTarget.Process);
    if (!string.IsNullOrWhiteSpace(githubOutputFile))
    {
        using (var textWriter = new StreamWriter(githubOutputFile!, true, Encoding.UTF8))
        {
            textWriter.WriteLine($"updated-metrics={updatedMetrics}");
            textWriter.WriteLine($"summary-title={title}");
            textWriter.WriteLine("summary-details<<EOF");
            textWriter.WriteLine(summary);
            textWriter.WriteLine("EOF");
        }
    }
    else
    {
        Console.WriteLine($"::set-output name=updated-metrics::{updatedMetrics}");
        Console.WriteLine($"::set-output name=summary-title::{title}");
        Console.WriteLine($"::set-output name=summary-details::{summary}");
    }

    Environment.Exit(0);
}

static async Task<string[]> FilterProjectsToChangedAsync(
    string[] projects,
    string workspaceRoot,
    ActionInputs inputs,
    ILogger logger)
{
    if (projects.Length == 0)
    {
        return projects;
    }

    var baseRef = inputs.BaseRef;
    var (exitCode, stdout, stderr) = await RunGitAsync(
        $"diff --name-only {baseRef}...HEAD",
        workspaceRoot,
        logger);

    if (exitCode != 0)
    {
        logger.LogWarning("git diff failed ({ExitCode}). stderr: {Error}. Falling back to all projects.", exitCode, stderr);
        return projects;
    }

    var changedFiles = stdout
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => Path.GetFullPath(Path.Combine(workspaceRoot, path.Trim())))
        .ToArray();

    HashSet<string> projectSet = new(StringComparer.OrdinalIgnoreCase);
    foreach (var project in projects)
    {
        var projectDir = Path.GetDirectoryName(project);
        if (projectDir is null)
        {
            continue;
        }

        foreach (var changed in changedFiles)
        {
            if (string.Equals(changed, project, StringComparison.OrdinalIgnoreCase))
            {
                projectSet.Add(project);
                break;
            }

            var dirWithSep = projectDir.EndsWith(Path.DirectorySeparatorChar)
                ? projectDir
                : projectDir + Path.DirectorySeparatorChar;

            if (changed.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
            {
                projectSet.Add(project);
                break;
            }
        }
    }

    if (projectSet.Count == 0)
    {
        logger.LogInformation("No changed projects detected between {BaseRef} and HEAD.", baseRef);
    }
    else
    {
        logger.LogInformation("Analyzing {Count} changed project(s).", projectSet.Count);
    }

    return projectSet.Count > 0 ? projectSet.ToArray() : projects;
}

static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(
    string arguments,
    string workingDirectory,
    ILogger logger)
{
    ProcessStartInfo psi = new()
    {
        FileName = "git",
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    using var process = Process.Start(psi);
    ArgumentNullException.ThrowIfNull(process);

    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();

    await process.WaitForExitAsync();

    if (!string.IsNullOrWhiteSpace(stderr))
    {
        logger.LogDebug("git {Args} stderr: {Err}", arguments, stderr);
    }

    return (process.ExitCode, stdout, stderr);
}

var parser = Default.ParseArguments<ActionInputs>(() => new(), args);
parser.WithNotParsed(
    errors =>
    {
        Get<ILoggerFactory>(host)
            .CreateLogger("DotNet.GitHubAction.Program")
            .LogError(
                string.Join(Environment.NewLine, errors.Select(error => error.ToString())));

        Environment.Exit(2);
    });

await parser.WithParsedAsync(options => StartAnalysisAsync(options, host));
await host.RunAsync();
