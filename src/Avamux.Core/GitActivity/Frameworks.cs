using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Avamux.Core.GitActivity;

/// <summary>
/// Frameworks and libraries, recognised from the projects in a repository: package.json, .csproj / .vbproj /
/// .fsproj, packages.config, requirements.txt, pyproject.toml, pom.xml, build.gradle, go.mod, Cargo.toml, Gemfile,
/// composer.json and pubspec.yaml, plus tell-tale files (next.config.js, angular.json). Each file a commit changes is
/// credited to the frameworks of every project folder it is in, and to the frameworks its own kind of file means
/// (a .tf file is Terraform wherever it is).
/// </summary>
public static class Frameworks
{
    public const string Frontend = "Front end", Backend = "Back end", Mobile = "Mobile", Desktop = "Desktop", Data = "Data & ML", Ai = "AI",
        Testing = "Testing", Infra = "Infrastructure", Platform = "Platform", Library = "Library", Tooling = "Tooling", Game = "Games", Messaging = "Messaging";

    public static readonly IReadOnlyList<FrameworkRule> BuiltIn = Build();

    private static List<FrameworkRule> Build()
    {
        var list = new List<FrameworkRule>();
        void F(string name, string cat, string[]? packages = null, string[]? sdks = null, string[]? markers = null, string[]? files = null) =>
            list.Add(new FrameworkRule { Name = name, Category = cat, Packages = [.. packages ?? []], Sdks = [.. sdks ?? []], Markers = [.. markers ?? []], Files = [.. files ?? []] });

        // JavaScript / TypeScript front ends
        F("Next.js", Frontend, ["next"], markers: ["next.config.*"]);
        F("React", Frontend, ["react", "react-dom"]);
        F("React Native", Mobile, ["react-native"], markers: ["metro.config.*"]);
        F("Expo", Mobile, ["expo"], markers: ["eas.json"]);
        F("Angular", Frontend, ["@angular/core"], markers: ["angular.json"]);
        F("AngularJS", Frontend, ["angular"]);
        F("Vue", Frontend, ["vue"]);
        F("Nuxt", Frontend, ["nuxt", "nuxt3"], markers: ["nuxt.config.*"]);
        F("Svelte", Frontend, ["svelte"]);
        F("SvelteKit", Frontend, ["@sveltejs/kit"]);
        F("Remix", Frontend, ["@remix-run/*"]);
        F("Gatsby", Frontend, ["gatsby"]);
        F("Astro", Frontend, ["astro"]);
        F("SolidJS", Frontend, ["solid-js"]);
        F("Preact", Frontend, ["preact"]);
        F("Ember", Frontend, ["ember-source"]);
        F("Lit", Frontend, ["lit", "lit-element"]);
        F("jQuery", Frontend, ["jquery"]);
        F("Redux", Frontend, ["redux", "@reduxjs/toolkit", "react-redux"]);
        F("Tailwind CSS", Frontend, ["tailwindcss", "@tailwindcss/*"], markers: ["tailwind.config.*"]);
        F("Material UI", Frontend, ["@mui/*", "@material-ui/*"]);
        F("Bootstrap", Frontend, ["bootstrap", "react-bootstrap"]);
        F("Ionic", Mobile, ["@ionic/*"]);
        F("Capacitor", Mobile, ["@capacitor/*"]);
        F("Electron", Desktop, ["electron"]);
        F("Tauri", Desktop, ["@tauri-apps/*", "tauri"]);
        F("Three.js", Frontend, ["three"]);
        F("D3", Frontend, ["d3"]);
        F("Storybook", Tooling, ["@storybook/*", "storybook"]);
        F("Vite", Tooling, ["vite"], markers: ["vite.config.*"]);
        F("Webpack", Tooling, ["webpack"], markers: ["webpack.config.*"]);
        F("Nx", Tooling, ["nx", "@nx/*", "@nrwl/*"], markers: ["nx.json"]);
        F("Turborepo", Tooling, ["turbo"], markers: ["turbo.json"]);
        // JavaScript back ends and data
        F("Express", Backend, ["express"]);
        F("NestJS", Backend, ["@nestjs/core"]);
        F("Fastify", Backend, ["fastify"]);
        F("Koa", Backend, ["koa"]);
        F("Hapi", Backend, ["@hapi/hapi", "hapi"]);
        F("tRPC", Backend, ["@trpc/*"]);
        F("GraphQL", Backend, ["graphql", "@apollo/*", "apollo-server*", "HotChocolate*", "GraphQL.*", "graphene", "strawberry-graphql"]);
        F("Prisma", Data, ["prisma", "@prisma/client"], files: ["**/*.prisma"]);
        F("TypeORM", Data, ["typeorm"]);
        F("Sequelize", Data, ["sequelize"]);
        F("Mongoose", Data, ["mongoose"]);
        F("Socket.IO", Messaging, ["socket.io", "socket.io-client"]);
        // JavaScript testing
        F("Jest", Testing, ["jest", "ts-jest", "@jest/*"]);
        F("Vitest", Testing, ["vitest"]);
        F("Cypress", Testing, ["cypress"]);
        F("Playwright", Testing, ["@playwright/test", "playwright", "Microsoft.Playwright*"]);
        F("Mocha", Testing, ["mocha"]);
        F("Testing Library", Testing, ["@testing-library/*"]);

        // .NET
        F(".NET", Platform, sdks: ["tfm:net5.0*", "tfm:net6.0*", "tfm:net7.0*", "tfm:net8.0*", "tfm:net9.0*", "tfm:net10.0*", "tfm:net11.0*", "tfm:netcoreapp*"]);
        F(".NET Framework", Platform, sdks: ["tfm:net4*", "tfm:v4*", "tfm:v3*", "tfm:v2*", "tfm:net3*", "tfm:net2*"]);
        F(".NET Standard", Platform, sdks: ["tfm:netstandard*"]);
        F("ASP.NET Core", Backend, ["Microsoft.AspNetCore.*", "Swashbuckle.AspNetCore*"], ["Microsoft.NET.Sdk.Web", "Microsoft.NET.Sdk.Razor"]);
        F("ASP.NET MVC / Web API", Backend, ["Microsoft.AspNet.Mvc", "Microsoft.AspNet.WebApi*", "Microsoft.AspNet.WebPages", "Microsoft.AspNet.Razor"], markers: ["Global.asax"]);
        F("ASP.NET Web Forms", Frontend, files: ["**/*.aspx", "**/*.ascx", "**/*.master"]);
        F("Blazor", Frontend, ["Microsoft.AspNetCore.Components.WebAssembly*", "Microsoft.AspNetCore.Components.Web", "MudBlazor", "Radzen.Blazor", "Blazored.*"], ["Microsoft.NET.Sdk.BlazorWebAssembly"], files: ["**/*.razor"]);
        F("Razor Pages / MVC views", Frontend, files: ["**/*.cshtml", "**/*.vbhtml"]);
        F("MediatR", Library, ["MediatR*", "Mediator.*"]);
        F("Entity Framework Core", Data, ["Microsoft.EntityFrameworkCore*", "Npgsql.EntityFrameworkCore*", "Pomelo.EntityFrameworkCore*"]);
        F("Entity Framework 6", Data, ["EntityFramework"]);
        F("Dapper", Data, ["Dapper*"]);
        F("AutoMapper", Library, ["AutoMapper*", "Mapster*"]);
        F("FluentValidation", Library, ["FluentValidation*"]);
        F("Serilog", Library, ["Serilog*"]);
        F("NLog", Library, ["NLog*"]);
        F("Polly", Library, ["Polly*", "Microsoft.Extensions.Http.Resilience"]);
        F("MassTransit", Messaging, ["MassTransit*"]);
        F("NServiceBus", Messaging, ["NServiceBus*"]);
        F("Azure Service Bus", Messaging, ["Azure.Messaging.ServiceBus", "Microsoft.Azure.ServiceBus", "@azure/service-bus", "azure-servicebus"]);
        F("Hangfire", Backend, ["Hangfire*"]);
        F("Quartz.NET", Backend, ["Quartz*"]);
        F("SignalR", Messaging, ["Microsoft.AspNetCore.SignalR*", "Microsoft.AspNet.SignalR*", "@microsoft/signalr"]);
        F("gRPC", Backend, ["Grpc.*", "Google.Protobuf", "@grpc/*", "grpcio", "google.golang.org/grpc", "io.grpc*", "tonic"], files: ["**/*.proto"]);
        F("Orleans", Backend, ["Microsoft.Orleans.*"]);
        F("Dapr", Backend, ["Dapr.*", "@dapr/*", "dapr"]);
        F(".NET Aspire", Platform, ["Aspire.*"], ["Aspire.AppHost.Sdk"]);
        F("Azure Functions", Backend, ["Microsoft.Azure.Functions.Worker*", "Microsoft.Azure.WebJobs*", "@azure/functions", "azure-functions"], ["Microsoft.NET.Sdk.Functions"], markers: ["host.json"]);
        F("MAUI", Mobile, ["Microsoft.Maui.*", "CommunityToolkit.Maui*"], ["UseMaui"]);
        F("Xamarin", Mobile, ["Xamarin.*"]);
        F("WPF", Desktop, sdks: ["UseWPF", "Microsoft.NET.Sdk.WindowsDesktop"]);
        F("Windows Forms", Desktop, sdks: ["UseWindowsForms"]);
        F("WinUI", Desktop, ["Microsoft.WindowsAppSDK", "Microsoft.UI.Xaml"]);
        F("Avalonia", Desktop, ["Avalonia*"]);
        F("Uno Platform", Desktop, ["Uno.*"], ["Uno.Sdk"]);
        F("CommunityToolkit.Mvvm", Library, ["CommunityToolkit.Mvvm", "Microsoft.Toolkit.Mvvm", "ReactiveUI*", "Prism.*"]);
        F("xUnit", Testing, ["xunit*"]);
        F("NUnit", Testing, ["NUnit*"]);
        F("MSTest", Testing, ["MSTest*", "Microsoft.VisualStudio.TestTools*", "Microsoft.VisualStudio.QualityTools*"]);
        F("Moq / NSubstitute", Testing, ["Moq", "NSubstitute", "FakeItEasy"]);
        F("SpecFlow / Reqnroll", Testing, ["SpecFlow*", "Reqnroll*"], files: ["**/*.feature"]);
        F("SQL Server project", Data, ["Microsoft.SqlServer.*"], ["Microsoft.Build.Sql", "MSBuild.Sdk.SqlProj"], files: ["**/*.sqlproj"]);
        F("Semantic Kernel", Ai, ["Microsoft.SemanticKernel*"]);
        F("OpenAI SDK", Ai, ["OpenAI", "Azure.AI.OpenAI", "openai", "@azure/openai", "github.com/openai/openai-go*", "com.openai*"]);
        F("Anthropic SDK", Ai, ["@anthropic-ai/sdk", "anthropic", "Anthropic.SDK", "Anthropic", "github.com/anthropics/anthropic-sdk-go*"]);
        F("LangChain", Ai, ["langchain*", "@langchain/*", "LangChain*"]);
        F("Model Context Protocol", Ai, ["@modelcontextprotocol/*", "mcp", "ModelContextProtocol*"]);
        F("Unity", Game, markers: ["ProjectSettings/ProjectVersion.txt"], files: ["**/*.unity", "**/*.prefab"]);
        F("Godot", Game, ["Godot.*"], ["Godot.NET.Sdk*"], markers: ["project.godot"]);
        F("MonoGame", Game, ["MonoGame*"]);

        // Python
        F("Django", Backend, ["django", "djangorestframework"], markers: ["manage.py"]);
        F("Flask", Backend, ["flask"]);
        F("FastAPI", Backend, ["fastapi"]);
        F("Celery", Backend, ["celery"]);
        F("SQLAlchemy", Data, ["sqlalchemy", "alembic"]);
        F("pandas", Data, ["pandas", "polars"]);
        F("NumPy / SciPy", Data, ["numpy", "scipy"]);
        F("PyTorch", Data, ["torch", "pytorch-lightning", "lightning"]);
        F("TensorFlow", Data, ["tensorflow", "keras", "tensorflow-*"]);
        F("scikit-learn", Data, ["scikit-learn", "sklearn"]);
        F("Hugging Face", Ai, ["transformers", "datasets", "huggingface-hub", "@huggingface/*"]);
        F("Spark", Data, ["pyspark", "org.apache.spark*", "spark-*"]);
        F("Airflow", Data, ["apache-airflow*"], markers: ["airflow.cfg"]);
        F("dbt", Data, ["dbt-*"], markers: ["dbt_project.yml"]);
        F("Streamlit", Data, ["streamlit"]);
        F("Jupyter", Data, ["jupyter*", "ipykernel", "notebook"], files: ["**/*.ipynb"]);
        F("pytest", Testing, ["pytest*"]);
        // JVM
        F("Spring Boot", Backend, ["spring-boot*", "org.springframework.boot*"]);
        F("Spring", Backend, ["spring-*", "org.springframework*"]);
        F("Quarkus", Backend, ["quarkus-*", "io.quarkus*"]);
        F("Micronaut", Backend, ["micronaut-*", "io.micronaut*"]);
        F("Hibernate", Data, ["hibernate-*", "org.hibernate*"]);
        F("Android", Mobile, ["com.android.*", "androidx.*"], markers: ["AndroidManifest.xml"]);
        F("Jetpack Compose", Mobile, ["androidx.compose*"]);
        F("Ktor", Backend, ["ktor-*", "io.ktor*"]);
        F("JUnit", Testing, ["junit*", "org.junit*"]);
        // Go, Rust, Ruby, PHP, Dart, Apple
        F("Gin", Backend, ["github.com/gin-gonic/gin"]);
        F("Echo", Backend, ["github.com/labstack/echo**"]);
        F("Fiber", Backend, ["github.com/gofiber/fiber**"]);
        F("Cobra", Tooling, ["github.com/spf13/cobra"]);
        F("Tokio", Backend, ["tokio"]);
        F("Actix", Backend, ["actix-web"]);
        F("Axum", Backend, ["axum"]);
        F("Rocket", Backend, ["rocket"]);
        F("Bevy", Game, ["bevy"]);
        F("Rails", Backend, ["rails", "railties"], markers: ["config/routes.rb"]);
        F("Sinatra", Backend, ["sinatra"]);
        F("RSpec", Testing, ["rspec*"]);
        F("Laravel", Backend, ["laravel/framework"], markers: ["artisan"]);
        F("Symfony", Backend, ["symfony/*"]);
        F("WordPress", Backend, ["johnpbloch/wordpress*", "roots/wordpress*"], markers: ["wp-config.php", "wp-config-sample.php"]);
        F("Drupal", Backend, ["drupal/*"]);
        F("Flutter", Mobile, ["flutter"]);
        F("Xcode / Apple", Mobile, files: ["**/*.xcodeproj/**", "**/*.xcworkspace/**", "**/Info.plist", "**/*.storyboard", "**/*.xib"]);

        // Infrastructure and delivery
        F("Docker", Infra, files: ["**/Dockerfile", "**/Dockerfile.*", "**/*.dockerfile", "**/docker-compose*.yml", "**/docker-compose*.yaml", "**/compose.yml", "**/compose.yaml", "**/Containerfile", "**/.dockerignore"]);
        F("Kubernetes", Infra, files: ["**/k8s/**", "**/kubernetes/**", "**/manifests/**/*.yaml", "**/kustomization.yaml", "**/*.k8s.yaml"]);
        F("Helm", Infra, markers: ["Chart.yaml"]);
        F("Terraform", Infra, files: ["**/*.tf", "**/*.tfvars", "**/.terraform.lock.hcl"]);
        F("Bicep / ARM", Infra, files: ["**/*.bicep", "**/*.bicepparam", "**/azuredeploy*.json", "**/*.arm.json"]);
        F("Pulumi", Infra, ["@pulumi/*", "pulumi", "Pulumi*"], markers: ["Pulumi.yaml"]);
        F("AWS CDK", Infra, ["aws-cdk-lib", "@aws-cdk/*", "Amazon.CDK*"], markers: ["cdk.json"]);
        F("Ansible", Infra, files: ["**/playbooks/**", "**/roles/*/tasks/**", "**/ansible.cfg"]);
        F("GitHub Actions", Infra, files: [".github/workflows/**", "**/action.yml", "**/action.yaml"]);
        F("Azure Pipelines", Infra, files: ["**/azure-pipelines*.yml", "**/azure-pipelines*.yaml", ".azure-pipelines/**", ".azuredevops/**", ".pipelines/**"]);
        F("GitLab CI", Infra, files: ["**/.gitlab-ci.yml", ".gitlab/**"]);
        F("Jenkins", Infra, files: ["**/Jenkinsfile"]);
        return list;
    }

    /// <summary>Files that say what a project folder uses.</summary>
    public static bool IsManifest(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name is "package.json" or "packages.config" or "requirements.txt" or "requirements-dev.txt" or "pyproject.toml" or "Pipfile" or "setup.py" or "pom.xml"
                   or "build.gradle" or "build.gradle.kts" or "go.mod" or "Cargo.toml" or "Gemfile" or "composer.json" or "pubspec.yaml" or "Directory.Build.props"
               || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex XmlSdk = new(@"<Project[^>]*\bSdk\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
    private static readonly Regex XmlSdkElement = new(@"<Sdk\s+Name\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
    private static readonly Regex XmlPackage = new(@"<(?:PackageReference|PackageVersion|package)\s+(?:Include|Update|id)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
    private static readonly Regex XmlSwitch = new(@"<(Use\w+)>\s*true\s*</\1>", RegexOptions.IgnoreCase);
    private static readonly Regex XmlTfm = new(@"<(?:TargetFrameworks?|TargetFrameworkVersion)>([^<]+)</", RegexOptions.IgnoreCase);
    private static readonly Regex XmlReference = new(@"<Reference\s+Include\s*=\s*""([^"",]+)", RegexOptions.IgnoreCase);
    private static readonly Regex MavenArtifact = new(@"<groupId>([^<]+)</groupId>\s*<artifactId>([^<]+)</artifactId>", RegexOptions.IgnoreCase);
    private static readonly Regex GradleDep = new(@"[""']([\w.\-]+):([\w.\-]+)(?::[^""']*)?[""']");
    private static readonly Regex GradlePlugin = new(@"id\s*\(?\s*[""']([\w.\-]+)[""']");
    private static readonly Regex GoRequire = new(@"^\s*(?:require\s+)?([\w.\-]+\.[a-z]{2,}/[\w.\-/]+)\s+v", RegexOptions.Multiline);
    private static readonly Regex PyRequirement = new(@"^\s*([A-Za-z0-9][A-Za-z0-9._\-]*)(?:\[[^\]]*\])?\s*(?:[<>=!~;].*)?$", RegexOptions.Multiline);
    private static readonly Regex PyQuoted = new(@"[""']([A-Za-z0-9][A-Za-z0-9._\-]*)(?:\[[^\]]*\])?\s*(?:[<>=!~;][^""']*)?[""']");
    private static readonly Regex TomlKey = new(@"^\s*([A-Za-z0-9_\-]+)\s*=", RegexOptions.Multiline);
    private static readonly Regex Gem = new(@"^\s*gem\s+[""']([^""']+)[""']", RegexOptions.Multiline);
    private static readonly Regex YamlKey = new(@"^\s{2}([A-Za-z0-9_]+):", RegexOptions.Multiline);

    /// <summary>The packages, SDKs and switches a manifest names (SDKs and switches as they appear; target frameworks as "tfm:…").</summary>
    public static (HashSet<string> Packages, HashSet<string> Sdks) Read(string path, string text)
    {
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sdks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = path[(path.LastIndexOf('/') + 1)..];
        try
        {
            if (name.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || name is "packages.config" or "Directory.Build.props")
            {
                foreach (Match m in XmlSdk.Matches(text)) foreach (var s in m.Groups[1].Value.Split(';')) sdks.Add(s.Split('/')[0].Trim());
                foreach (Match m in XmlSdkElement.Matches(text)) sdks.Add(m.Groups[1].Value.Trim());
                foreach (Match m in XmlPackage.Matches(text)) packages.Add(m.Groups[1].Value.Trim());
                foreach (Match m in XmlReference.Matches(text)) packages.Add(m.Groups[1].Value.Trim());
                foreach (Match m in XmlSwitch.Matches(text)) sdks.Add(m.Groups[1].Value);
                foreach (Match m in XmlTfm.Matches(text)) foreach (var t in m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries)) sdks.Add("tfm:" + t.Trim().ToLowerInvariant());
            }
            else if (name is "package.json" or "composer.json")
            {
                if (JsonUtil.Parse(text) is JsonObject o)
                    foreach (var key in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies", "require", "require-dev" })
                        if (o[key] is JsonObject deps) foreach (var (k, _) in deps) packages.Add(k);
            }
            else if (name.StartsWith("requirements")) foreach (Match m in PyRequirement.Matches(text)) packages.Add(m.Groups[1].Value);
            else if (name is "pyproject.toml" or "Pipfile" or "setup.py")
            {
                foreach (Match m in PyQuoted.Matches(text)) packages.Add(m.Groups[1].Value);
                if (name == "Pipfile") foreach (Match m in TomlKey.Matches(text)) packages.Add(m.Groups[1].Value);
            }
            else if (name == "pom.xml") foreach (Match m in MavenArtifact.Matches(text)) { packages.Add(m.Groups[2].Value); packages.Add(m.Groups[1].Value + "." + m.Groups[2].Value); }
            else if (name.StartsWith("build.gradle"))
            {
                foreach (Match m in GradleDep.Matches(text)) { packages.Add(m.Groups[2].Value); packages.Add(m.Groups[1].Value + "." + m.Groups[2].Value); }
                foreach (Match m in GradlePlugin.Matches(text)) packages.Add(m.Groups[1].Value);
            }
            else if (name == "go.mod") foreach (Match m in GoRequire.Matches(text)) packages.Add(m.Groups[1].Value);
            else if (name == "Cargo.toml") foreach (Match m in TomlKey.Matches(text)) packages.Add(m.Groups[1].Value);
            else if (name == "Gemfile") foreach (Match m in Gem.Matches(text)) packages.Add(m.Groups[1].Value);
            else if (name == "pubspec.yaml") foreach (Match m in YamlKey.Matches(text)) packages.Add(m.Groups[1].Value);
        }
        catch (Exception ex) { Logging.Log.Debug("activity", $"Could not read {path}: {ex.Message}"); }
        return (packages, sdks);
    }
}

/// <summary>Which frameworks each folder of one repository uses, and which files mean a framework wherever they are.</summary>
public sealed class FrameworkMap
{
    private readonly Dictionary<string, string[]> _folders = new(StringComparer.Ordinal);   // "" is the top
    private readonly List<(FrameworkRule Rule, System.Text.RegularExpressions.Regex[] Files)> _fileRules;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _testing;

    public static readonly FrameworkMap Empty = new([], new Dictionary<string, (HashSet<string>, HashSet<string>)>(), []);

    /// <summary>The frameworks found anywhere in the repository.</summary>
    public IReadOnlyList<string> All { get; }

    /// <param name="paths">Every file in the repository (at the tip of its main branch).</param>
    /// <param name="manifests">What each manifest names, by path.</param>
    public FrameworkMap(IReadOnlyList<FrameworkRule> rules, IReadOnlyDictionary<string, (HashSet<string> Packages, HashSet<string> Sdks)> manifests, IReadOnlyList<string> paths)
    {
        _fileRules = rules.Where(r => r.Files.Count > 0).Select(r => (r, r.Files.Select(Glob.ToRegex).ToArray())).ToList();
        _testing = rules.Where(r => r.Category == Frameworks.Testing).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var byFolder = new Dictionary<string, (HashSet<string> Packages, HashSet<string> Sdks, List<string> Names)>(StringComparer.Ordinal);
        (HashSet<string>, HashSet<string>, List<string>) Folder(string f)
        {
            if (!byFolder.TryGetValue(f, out var v)) byFolder[f] = v = (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), []);
            return v;
        }
        foreach (var (path, (packages, sdks)) in manifests)
        {
            var (p, s, _) = Folder(Dir(path));
            p.UnionWith(packages); s.UnionWith(sdks);
        }
        // Marker files: a folder with next.config.js is a Next.js project, one with Chart.yaml a Helm chart.
        var markers = rules.SelectMany(r => r.Markers.Select(m => (r.Name, Depth: m.Count(c => c == '/'), Regex: Glob.ToRegex(m.Trim())))).ToList();
        if (markers.Count > 0)
            foreach (var path in paths)
            {
                var parts = path.Split('/');
                foreach (var (fw, depth, regex) in markers)
                {
                    // A marker may name a file in a sub-folder (ProjectSettings/ProjectVersion.txt): it marks the folder above that.
                    if (parts.Length <= depth) continue;
                    if (regex.IsMatch(depth == 0 ? parts[^1] : string.Join('/', parts[^(depth + 1)..]))) Folder(string.Join('/', parts[..^(depth + 1)])).Item3.Add(fw);
                }
            }
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (folder, (packages, sdks, names)) in byFolder)
        {
            var found = new SortedSet<string>(names, StringComparer.Ordinal);
            foreach (var rule in rules)
                if (rule.Packages.Any(g => packages.Any(p => Glob.Match(g, p))) || rule.Sdks.Any(g => sdks.Any(s => Glob.Match(g, s)))) found.Add(rule.Name);
            if (found.Count > 0) { _folders[folder] = [.. found]; all.UnionWith(found); }
        }
        foreach (var path in paths) foreach (var (rule, files) in _fileRules) if (files.Any(f => f.IsMatch(path))) all.Add(rule.Name);
        All = all.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Dir(string path) { var i = path.LastIndexOf('/'); return i < 0 ? "" : path[..i]; }

    public bool IsEmpty => _folders.Count == 0 && _fileRules.Count == 0;

    /// <summary>
    /// The frameworks a file belongs to, joined with '|' (empty when none), sorted so equal sets compare equal.
    /// Test frameworks (xUnit, Jest, pytest…) only count for test files, so a project's test runner does not claim its production code.
    /// </summary>
    public string For(string path)
    {
        lock (_cache)
        {
            var dir = Dir(path);
            var test = Languages.IsTest(path);
            var cacheKey = (test ? "t|" : "c|") + dir;
            var fileHits = _fileRules.Where(r => r.Files.Any(f => f.IsMatch(path))).Select(r => r.Rule.Name).ToList();
            if (fileHits.Count == 0 && _cache.TryGetValue(cacheKey, out var hit)) return hit;
            var set = new SortedSet<string>(fileHits, StringComparer.Ordinal);
            for (var d = dir; ; d = Dir(d))
            {
                if (_folders.TryGetValue(d, out var names)) set.UnionWith(test ? names : names.Where(n => !_testing.Contains(n)));
                if (d.Length == 0) break;
            }
            var joined = string.Join('|', set);
            if (fileHits.Count == 0) _cache[cacheKey] = joined;
            return joined;
        }
    }
}
