namespace Avamux.Core.GitActivity;

/// <summary>
/// File types: what language a file is in and what kind of file that is (code, markup, data, scripts…). Commits
/// keep a key per file (its extension, or its name for files such as Dockerfile) and the language is looked up
/// when the figures are drawn, so changing a mapping needs no new scan.
/// </summary>
public static class Languages
{
    public const string Code = "Code", Web = "Web", Markup = "Markup", Style = "Styles", Data = "Data", Config = "Config", Script = "Scripts",
        Database = "Database", BuildFiles = "Build", Docs = "Docs", Notebook = "Notebooks", Infra = "Infrastructure", Test = "Tests", Asset = "Assets", Other = "Other";

    public sealed record Info(string Language, string Category);

    private static readonly Dictionary<string, Info> ByKey = Build();

    private static Dictionary<string, Info> Build()
    {
        var d = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        void Add(string lang, string cat, params string[] keys) { foreach (var k in keys) d[k] = new Info(lang, cat); }

        // .NET and Visual Studio
        Add("C#", Code, ".cs", ".csx");
        Add("VB.NET", Code, ".vb", ".vbs.net");
        Add("F#", Code, ".fs", ".fsi", ".fsx", ".fsscript");
        Add("Razor", Web, ".razor", ".cshtml", ".vbhtml");
        Add("ASP.NET", Web, ".aspx", ".ascx", ".asax", ".ashx", ".asmx", ".master", ".svc");
        Add("XAML", Markup, ".xaml", ".axaml", ".paml");
        Add("MSBuild", BuildFiles, ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".vcproj", ".sqlproj", ".proj", ".props", ".targets", ".shproj", ".projitems", ".wapproj", ".esproj", ".dcproj", ".nuspec", ".ruleset", ".pubxml");
        Add("Visual Studio", BuildFiles, ".sln", ".slnx", ".slnf", ".vsix", ".vsixmanifest", ".vsct", ".user", ".runsettings", ".testsettings");
        Add(".NET Config", Config, ".config", ".resx", ".resw", ".settings", ".manifest", ".appxmanifest", ".editorconfig", "global.json", "nuget.config", "launchSettings.json", "packages.config");
        Add("T4", Code, ".tt", ".t4", ".ttinclude");
        Add("PowerShell", Script, ".ps1", ".psm1", ".psd1", ".ps1xml");
        Add("Batch", Script, ".bat", ".cmd");
        Add("VBScript", Script, ".vbs", ".vba", ".bas", ".frm", ".cls");
        // Web
        Add("JavaScript", Web, ".js", ".mjs", ".cjs", ".es6");
        Add("TypeScript", Web, ".ts", ".mts", ".cts");
        Add("JSX", Web, ".jsx");
        Add("TSX", Web, ".tsx");
        Add("Vue", Web, ".vue");
        Add("Svelte", Web, ".svelte");
        Add("Astro", Web, ".astro");
        Add("HTML", Markup, ".html", ".htm", ".xhtml", ".shtml");
        Add("Templates", Markup, ".hbs", ".handlebars", ".mustache", ".ejs", ".pug", ".jade", ".njk", ".liquid", ".twig", ".erb", ".haml", ".slim", ".jinja", ".j2", ".tmpl", ".gohtml");
        Add("CSS", Style, ".css", ".pcss", ".postcss");
        Add("SCSS", Style, ".scss", ".sass");
        Add("Less", Style, ".less");
        Add("Stylus", Style, ".styl");
        Add("WebAssembly", Code, ".wat", ".wast");
        Add("GraphQL", Data, ".graphql", ".gql");
        // Systems and general purpose
        Add("C", Code, ".c", ".h");
        Add("C++", Code, ".cpp", ".cc", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".h++", ".inl", ".ipp", ".ixx", ".cppm");
        Add("Objective-C", Code, ".m", ".mm");
        Add("Swift", Code, ".swift");
        Add("Java", Code, ".java");
        Add("Kotlin", Code, ".kt", ".kts");
        Add("Scala", Code, ".scala", ".sc", ".sbt");
        Add("Groovy", Code, ".groovy", ".gvy", ".gradle");
        Add("Clojure", Code, ".clj", ".cljs", ".cljc", ".edn");
        Add("Go", Code, ".go");
        Add("Rust", Code, ".rs");
        Add("Zig", Code, ".zig");
        Add("Nim", Code, ".nim");
        Add("D", Code, ".d");
        Add("Dart", Code, ".dart");
        Add("Python", Code, ".py", ".pyw", ".pyi", ".pyx", ".pxd");
        Add("Ruby", Code, ".rb", ".rake", ".gemspec", "Gemfile", "Rakefile");
        Add("PHP", Code, ".php", ".phtml", ".php3", ".php4", ".php5", ".phps");
        Add("Perl", Script, ".pl", ".pm", ".t", ".pod");
        Add("Lua", Script, ".lua");
        Add("R", Code, ".r", ".rmd");
        Add("Julia", Code, ".jl");
        Add("MATLAB", Code, ".mat", ".mlx");
        Add("Elixir", Code, ".ex", ".exs");
        Add("Erlang", Code, ".erl", ".hrl");
        Add("Haskell", Code, ".hs", ".lhs");
        Add("OCaml", Code, ".ml", ".mli");
        Add("Elm", Code, ".elm");
        Add("Lisp", Code, ".lisp", ".lsp", ".el", ".scm", ".rkt");
        Add("Fortran", Code, ".f", ".f90", ".f95", ".f03", ".for");
        Add("COBOL", Code, ".cob", ".cbl", ".cpy");
        Add("Pascal", Code, ".pas", ".dpr", ".pp");
        Add("Assembly", Code, ".asm", ".s", ".nasm");
        Add("Solidity", Code, ".sol");
        Add("Apex", Code, ".apex", ".trigger");
        Add("ABAP", Code, ".abap");
        Add("Shaders", Code, ".hlsl", ".glsl", ".shader", ".frag", ".vert", ".wgsl", ".metal", ".cginc");
        Add("GDScript", Code, ".gd");
        Add("Protocol Buffers", Data, ".proto");
        Add("Thrift", Data, ".thrift");
        Add("Avro", Data, ".avsc", ".avdl");
        // Scripting
        Add("Shell", Script, ".sh", ".bash", ".zsh", ".fish", ".ksh", ".csh", ".tcsh", ".bashrc", ".zshrc", ".profile", ".envrc");
        Add("AutoHotkey", Script, ".ahk");
        Add("AppleScript", Script, ".applescript", ".scpt");
        Add("Awk", Script, ".awk");
        Add("Tcl", Script, ".tcl");
        // Data
        Add("JSON", Data, ".json", ".jsonc", ".json5", ".jsonl", ".ndjson", ".geojson", ".webmanifest", ".har");
        Add("YAML", Config, ".yaml", ".yml");
        Add("TOML", Config, ".toml");
        Add("INI", Config, ".ini", ".cfg", ".conf", ".properties", ".env", ".env.example", ".env.local");
        Add("XML", Data, ".xml", ".xsd", ".xsl", ".xslt", ".wsdl", ".dtd", ".rss", ".atom", ".plist", ".svgz");
        Add("CSV", Data, ".csv", ".tsv", ".psv");
        Add("Parquet / Arrow", Data, ".parquet", ".arrow", ".feather", ".orc");
        Add("Spreadsheets", Data, ".xlsx", ".xls", ".xlsm", ".ods");
        Add("Data files", Data, ".dat", ".avro", ".pkl", ".pickle", ".npy", ".npz", ".h5", ".hdf5", ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb");
        Add("SQL", Database, ".sql", ".psql", ".pgsql", ".plsql", ".pls", ".pkb", ".pks", ".tsql", ".ddl", ".dml", ".prc", ".udf", ".viw", ".tab");
        Add("Prisma", Database, ".prisma");
        Add("Cypher", Database, ".cypher", ".cql");
        Add("KQL", Database, ".kql", ".csl");
        Add("DAX / Power BI", Database, ".dax", ".pbix", ".pbit", ".pq", ".m.pq", ".tmdl", ".bim");
        Add("SSIS / SSRS", Database, ".dtsx", ".rdl", ".rdlc", ".rds", ".dtproj", ".rptproj");
        Add("Notebook", Notebook, ".ipynb", ".dib", ".rmarkdown", ".qmd");
        // Infrastructure and build
        Add("Terraform", Infra, ".tf", ".tfvars", ".hcl");
        Add("Bicep", Infra, ".bicep", ".bicepparam");
        Add("ARM templates", Infra, ".arm.json", "azuredeploy.json");
        Add("Docker", Infra, "Dockerfile", ".dockerfile", ".dockerignore", "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml", "Containerfile");
        Add("Kubernetes / Helm", Infra, "Chart.yaml", "values.yaml", "kustomization.yaml", "helmfile.yaml");
        Add("Nix", Infra, ".nix");
        Add("Puppet", Infra, ".pp.puppet");
        Add("Pipelines", Infra, "azure-pipelines.yml", "azure-pipelines.yaml", ".gitlab-ci.yml", "Jenkinsfile", ".travis.yml", "bitbucket-pipelines.yml", "appveyor.yml", "cloudbuild.yaml", "buildspec.yml");
        Add("Make", BuildFiles, "Makefile", "GNUmakefile", ".mk", ".mak", "makefile");
        Add("CMake", BuildFiles, "CMakeLists.txt", ".cmake");
        Add("Bazel", BuildFiles, "BUILD", "BUILD.bazel", "WORKSPACE", ".bzl", ".bazel");
        Add("Build files", BuildFiles, "package.json", "tsconfig.json", "jsconfig.json", "pom.xml", "build.gradle", "settings.gradle", "build.gradle.kts", "go.mod", "Cargo.toml",
            "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile", "composer.json", "pubspec.yaml", "Podfile", "Package.swift", ".csproj.user", "vite.config.ts",
            "vite.config.js", "webpack.config.js", "rollup.config.js", "babel.config.js", ".babelrc", ".eslintrc", ".eslintrc.js", ".eslintrc.json", ".prettierrc", "angular.json", "nx.json",
            "turbo.json", "lerna.json", "next.config.js", "next.config.mjs", "next.config.ts", "nuxt.config.ts", "svelte.config.js", "tailwind.config.js", "tailwind.config.ts", "postcss.config.js",
            "metro.config.js", "app.json", "eas.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props");
        Add("Git", Config, ".gitignore", ".gitattributes", ".gitmodules", ".gitkeep", "CODEOWNERS");
        // Documents
        Add("Markdown", Docs, ".md", ".markdown", ".mdx", ".mdown");
        Add("reStructuredText", Docs, ".rst");
        Add("AsciiDoc", Docs, ".adoc", ".asciidoc");
        Add("Text", Docs, ".txt", ".text", "README", "LICENSE", "CHANGELOG", "AUTHORS", "NOTICE");
        Add("TeX", Docs, ".tex", ".bib", ".sty", ".cls.tex");
        Add("Office documents", Docs, ".docx", ".doc", ".pptx", ".ppt", ".pdf", ".rtf", ".odt", ".vsdx", ".one");
        // Assets
        Add("Images", Asset, ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".icns", ".webp", ".tif", ".tiff", ".psd", ".ai", ".sketch", ".fig", ".xcf", ".heic", ".avif");
        Add("SVG", Asset, ".svg");
        Add("Fonts", Asset, ".ttf", ".otf", ".woff", ".woff2", ".eot");
        Add("Media", Asset, ".mp3", ".wav", ".ogg", ".flac", ".mp4", ".mov", ".webm", ".avi", ".mkv");
        Add("3D / game assets", Asset, ".fbx", ".obj", ".blend", ".gltf", ".glb", ".unity", ".prefab", ".mat.unity", ".asset", ".uasset", ".umap", ".tscn", ".tres");
        Add("Binaries", Asset, ".dll", ".exe", ".so", ".dylib", ".a", ".lib", ".jar", ".war", ".nupkg", ".zip", ".tar", ".gz", ".tgz", ".7z", ".rar", ".whl", ".apk", ".aab", ".ipa", ".pdb", ".snk", ".pfx", ".p12", ".cer");
        Add("Localization", Data, ".po", ".pot", ".xlf", ".xliff", ".strings", ".arb");
        return d;
    }

    /// <summary>The key a file is counted under: a known file name, else its (double) extension, else its name.</summary>
    public static string KeyFor(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (ByKey.ContainsKey(name)) return name;
        var dot = name.IndexOf('.', 1);
        // ".min.js" style double extensions only when they mean something on their own ("arm.json", "m.pq").
        while (dot > 0)
        {
            var ext = name[dot..].ToLowerInvariant();
            if (ByKey.ContainsKey(ext)) return ext;
            dot = name.IndexOf('.', dot + 1);
        }
        var last = name.LastIndexOf('.');
        return last > 0 ? name[last..].ToLowerInvariant() : name.StartsWith('.') ? name.ToLowerInvariant() : "(no extension)";
    }

    /// <summary>The language and category of a file key; the config's own mappings win.</summary>
    public static Info Lookup(string key, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (overrides is not null && overrides.TryGetValue(key, out var lang) && lang.Length > 0)
            return new Info(lang, ByLanguage.TryGetValue(lang, out var cat) ? cat : Other);
        return ByKey.TryGetValue(key, out var info) ? info : new Info(key.StartsWith('.') ? key : "Other", Other);
    }

    private static readonly Dictionary<string, string> ByLanguage =
        ByKey.Values.GroupBy(v => v.Language, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Category, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> KnownLanguages => ByLanguage.Keys;
    public static int KnownKeys => ByKey.Count;

    /// <summary>True for files that are about testing, whatever their language.</summary>
    public static bool IsTest(string path)
    {
        var p = path.ToLowerInvariant();
        return p.Contains("/test/") || p.Contains("/tests/") || p.StartsWith("test/") || p.StartsWith("tests/") || p.Contains("__tests__/") || p.Contains(".test.")
               || p.Contains(".spec.") || p.Contains("_test.") || p.Contains(".tests/") || p.Contains(".test/") || p.Contains("/spec/") || p.EndsWith("tests.cs") || p.EndsWith("test.cs");
    }
}
