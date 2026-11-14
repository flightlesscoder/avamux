using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;

namespace Avamux.Core.Interactive;

public enum ReplOutputKind
{
    /// <summary>The value of an expression.</summary>
    Result,
    /// <summary>Something the code printed (Print, Console.Write).</summary>
    Output,
    /// <summary>A compile error or an exception.</summary>
    Error,
    /// <summary>A remark from the session itself.</summary>
    Info,
}

/// <summary>What became of one submission.</summary>
public sealed record ReplResult(bool Ok, bool HasValue = false, object? Value = null, string? Error = null, bool Stopped = false);

/// <summary>
/// What code typed into C# Interactive can use without qualifying it. The page derives from this to add its
/// screen. Public members become names in the script, so everything else here is internal.
/// </summary>
public class ReplGlobals(AvamuxApi? api = null)
{
    internal ReplSession? Session;

    /// <summary>The running avamux: <c>avamux.Terminals</c>, <c>avamux.Wiki</c>, <c>await avamux.Command("terminal list")</c>, <c>avamux.Notify(…)</c>.</summary>
#pragma warning disable IDE1006 // lower case on purpose: it reads like the dashboard's window.avamux
    public AvamuxApi avamux => api ?? throw new InvalidOperationException("avamux is not available here.");
#pragma warning restore IDE1006

    /// <summary>Cancelled when Stop is pressed. Hand it to anything long-running: <c>await Task.Delay(60000, Cancel)</c>.</summary>
    public CancellationToken Cancel { get; internal set; }

    /// <summary>Writes a value to the terminal the way a result is shown. A string is written as it is.</summary>
    public void Print(object? value = null) => Session?.Emit(ReplOutputKind.Output, value as string ?? ReplFormatter.Format(value));

    /// <summary>Empties the terminal.</summary>
    public void Clear() => Session?.RaiseCleared();

    /// <summary>Shows a notification in avamux.</summary>
    public void Notify(string title, string message = "") => avamux.Notify(title, message);

    /// <summary>Lists the variables declared so far.</summary>
    public void Vars() => Print(Session?.Variables() is { Count: > 0 } all ? string.Join("\n", all) : "No variables yet.");

    /// <summary>What can be typed here.</summary>
    public virtual void Help() => Print(HelpText);

    protected virtual string HelpText => """
        Type C# and press Enter. An expression without a semicolon shows its value.
        Variables, usings, methods and classes stay for later lines. await works anywhere.
        Enter continues an unfinished statement, Shift+Enter always adds a line,
        Ctrl+Enter always runs, Up and Down walk the history.

          Print(value)                write a value here (Console.WriteLine works too)
          Clear()                     empty the terminal
          Vars()                      the variables declared so far
          Cancel                      a CancellationToken that Stop cancels
          avamux                      the running app: .Terminals .Wiki .Events .Watches
                                      .Repos .Settings .Host .Services
          avamux.Notify(title, text)  show a notification
          avamux.Navigate("wiki")     switch page
          avamux.Commands()           the names of all commands
          await avamux.Command("terminal new", new { name = "build" })
                                      run any of them
        """;
}

/// <summary>
/// A C# REPL inside the running process: each submission continues from the one before, so variables, usings and
/// declarations carry over. Compiling happens off the calling thread; the code itself runs on the thread (and
/// synchronization context) <see cref="RunAsync"/> was called from, so on the UI thread it may touch controls.
/// </summary>
public sealed class ReplSession
{
    public static readonly string[] DefaultImports =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Text", "System.Text.Json", "System.Text.Json.Nodes",
        "System.Threading", "System.Threading.Tasks", "Avamux.Core",
    ];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, kind: SourceCodeKind.Script);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> PlatformReferences = new(BuildPlatformReferences);

    private const int MaxErrors = 6;
    private readonly ReplGlobals _globals;
    private readonly ScriptOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScriptState<object>? _state;
    private CancellationTokenSource? _running;
    private int _generation;

    public ReplSession(ReplGlobals? globals = null, IEnumerable<Assembly>? references = null, IEnumerable<string>? imports = null)
    {
        _globals = globals ?? new ReplGlobals();
        _globals.Session = this;
        _options = ScriptOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithAllowUnsafe(false)
            .WithEmitDebugInformation(true)
            .WithReferences(PlatformReferences.Value)
            .AddReferences(new[] { typeof(ReplSession).Assembly, _globals.GetType().Assembly }.Concat(references ?? []).Distinct())
            .WithImports(DefaultImports.Concat(imports ?? []).Distinct());
    }

    /// <summary>Text for the terminal. May be raised from any thread.</summary>
    public event Action<ReplOutputKind, string>? Output;
    public event Action? Cleared;
    /// <summary>Raised when a submission starts or ends running.</summary>
    public event Action? BusyChanged;

    public bool Busy { get; private set; }
    public ReplGlobals Globals => _globals;

    internal void Emit(ReplOutputKind kind, string text) => Output?.Invoke(kind, text);
    internal void RaiseCleared() => Cleared?.Invoke();

    /// <summary>
    /// Everything the process can already load: the framework and the app's own libraries. Assemblies that are
    /// loaded are referenced as themselves, so their types are the very ones the app uses.
    /// </summary>
    private static IReadOnlyList<MetadataReference> BuildPlatformReferences()
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) || !File.Exists(assembly.Location)) continue;
            if (seen.Add(assembly.GetName().Name ?? "")) references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }
        foreach (var path in ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!seen.Add(Path.GetFileNameWithoutExtension(path)) || !File.Exists(path)) continue;
            references.Add(MetadataReference.CreateFromFile(path));
        }
        return references;
    }

    /// <summary>
    /// False while the text is the start of something: an open brace, bracket or string, a statement without its
    /// semicolon. An expression is complete (its value is shown).
    /// </summary>
    public static bool IsComplete(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return true;
        return SyntaxFactory.IsCompleteSubmission(CSharpSyntaxTree.ParseText(code, ParseOptions));
    }

    /// <summary>Forgets every variable and declaration.</summary>
    public void Reset()
    {
        Stop();
        Interlocked.Increment(ref _generation);
        _state = null;
    }

    /// <summary>
    /// Gives up waiting for the running submission and cancels <see cref="ReplGlobals.Cancel"/>. Code that does
    /// not watch the token carries on in the background; what it declared is not kept.
    /// </summary>
    public void Stop() => _running?.Cancel();

    public IReadOnlyList<string> Variables() =>
        _state is null ? [] : _state.Variables.GroupBy(v => v.Name).Select(g => g.Last())
            .Select(v => $"{ReplFormatter.TypeName(v.Type)} {v.Name} = {OneLine(ReplFormatter.Format(v.Value))}").ToList();

    private static string OneLine(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length > 120 ? s[..120] + "…" : s;
    }

    /// <summary>
    /// Compiles and runs one submission. Compile errors, exceptions and the value of a final expression are
    /// reported through <see cref="Output"/> as well as returned. Submissions run one after another.
    /// </summary>
    public async Task<ReplResult> RunAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return new ReplResult(true);
        var generation = _generation;
        await _gate.WaitAsync();
        var cts = new CancellationTokenSource();
        try
        {
            if (generation != _generation) return new ReplResult(false, Stopped: true); // reset while waiting its turn
            _running = cts;
            Busy = true;
            BusyChanged?.Invoke();

            var previous = _state;
            var (script, errors, hasValue) = await Task.Run(() => Compile(previous, code));
            if (errors.Count > 0)
            {
                // One slip can set off dozens of follow-on errors; the first few say what is wrong.
                var text = string.Join("\n", errors.Take(MaxErrors)) + (errors.Count > MaxErrors ? $"\n… and {errors.Count - MaxErrors} more" : "");
                Emit(ReplOutputKind.Error, text);
                return new ReplResult(false, Error: text);
            }

            _globals.Cancel = cts.Token;
            ScriptState<object> state;
            try
            {
                var run = previous is null ? script.RunAsync(_globals, _ => true, cts.Token) : script.RunFromAsync(previous, _ => true, cts.Token);
                state = await run.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Emit(ReplOutputKind.Info, "Stopped.");
                return new ReplResult(false, Stopped: true);
            }
            if (generation == _generation) _state = state;

            if (state.Exception is { } ex)
            {
                if (ex is OperationCanceledException && cts.IsCancellationRequested)
                {
                    Emit(ReplOutputKind.Info, "Stopped.");
                    return new ReplResult(false, Stopped: true);
                }
                var text = Describe(ex);
                Emit(ReplOutputKind.Error, text);
                return new ReplResult(false, Error: text);
            }
            if (!hasValue) return new ReplResult(true);

            var value = state.ReturnValue;
            Emit(ReplOutputKind.Result, ReplFormatter.Format(value));
            // A task that was not awaited: say what it came to when it gets there.
            if (value is Task { IsCompleted: false } pending) _ = ReportWhenDone(pending);
            return new ReplResult(true, true, value);
        }
        finally
        {
            if (ReferenceEquals(_running, cts)) _running = null;
            Busy = false;
            _gate.Release();
            BusyChanged?.Invoke();
        }
    }

    private async Task ReportWhenDone(Task task)
    {
        try
        {
            await task;
            Emit(ReplOutputKind.Result, ReplFormatter.Format(task));
        }
        catch (Exception ex) { Emit(ReplOutputKind.Error, "Task failed. " + Describe(ex)); }
    }

    private (Script<object> Script, List<string> Errors, bool HasValue) Compile(ScriptState<object>? previous, string code)
    {
        var script = previous is null ? CSharpScript.Create<object>(code, _options, _globals.GetType()) : previous.Script.ContinueWith<object>(code, _options);
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).Select(Describe).ToList();
        return (script, errors, errors.Count == 0 && EndsInValue(script));
    }

    /// <summary>True when the submission ends in an expression that has a value: <c>1 + 1</c>, not <c>Print(1)</c> or <c>x = 1;</c>.</summary>
    private static bool EndsInValue(Script script)
    {
        var compilation = script.GetCompilation();
        var tree = compilation.SyntaxTrees.FirstOrDefault();
        if (tree?.GetRoot() is not CompilationUnitSyntax unit) return false;
        if (unit.Members.LastOrDefault() is not GlobalStatementSyntax { Statement: ExpressionStatementSyntax { SemicolonToken.IsMissing: true } last }) return false;
        var type = compilation.GetSemanticModel(tree).GetTypeInfo(last.Expression).ConvertedType;
        return type is not null && type.SpecialType != SpecialType.System_Void;
    }

    private static string Describe(Diagnostic d)
    {
        var at = d.Location.GetLineSpan().StartLinePosition;
        return $"({at.Line + 1},{at.Character + 1}): error {d.Id}: {d.GetMessage()}";
    }

    /// <summary>An exception as its type and message, with the causes underneath.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: 1 } aggregate) ex = aggregate.InnerExceptions[0];
        if (ex is TargetInvocationException { InnerException: { } target }) ex = target;
        var text = $"{ex.GetType().Name}: {ex.Message}";
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            text += $"\n  caused by {inner.GetType().Name}: {inner.Message}";
        return text;
    }
}
