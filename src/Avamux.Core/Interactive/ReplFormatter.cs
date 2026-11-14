using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Avamux.Core.Interactive;

/// <summary>Writes a value the way a C# REPL shows the result of an expression: <c>List&lt;string&gt;(3) { "a", "b", "c" }</c>.</summary>
public static class ReplFormatter
{
    private const int MaxProperties = 24;
    private static readonly HashSet<string> Telling = ["Name", "Id", "Title", "Header", "Content", "Text", "Value", "Count", "Path"];
    private const int MaxItems = 100, MaxDepth = 4, MaxWidth = 100, MaxString = 4000;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Format(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    /// <summary>A type's name as it is written in C#: <c>Dictionary&lt;string, int[]&gt;</c>, <c>int?</c>.</summary>
    public static string TypeName(Type type)
    {
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (Nullable.GetUnderlyingType(type) is { } inner) return TypeName(inner) + "?";
        if (Keywords.TryGetValue(type, out var keyword)) return keyword;
        if (IsAnonymous(type)) return "anonymous";
        var name = type.Name;
        if (!type.IsGenericType) return name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        if (type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
            return "(" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ")";
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
    }

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(string)] = "string", [typeof(int)] = "int", [typeof(long)] = "long", [typeof(bool)] = "bool", [typeof(double)] = "double",
        [typeof(float)] = "float", [typeof(decimal)] = "decimal", [typeof(char)] = "char", [typeof(byte)] = "byte", [typeof(object)] = "object",
        [typeof(short)] = "short", [typeof(uint)] = "uint", [typeof(ulong)] = "ulong", [typeof(ushort)] = "ushort", [typeof(sbyte)] = "sbyte", [typeof(void)] = "void",
    };

    private static void Write(StringBuilder sb, object? value, int depth)
    {
        switch (value)
        {
            case null: sb.Append("null"); return;
            case string s: sb.Append(Quote(s.Length > MaxString ? s[..MaxString] + "…" : s)); return;
            case char c: sb.Append('\'').Append(c switch { '\'' => "\\'", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\0' => "\\0", '\\' => "\\\\", _ => c.ToString() }).Append('\''); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case float or double or decimal: sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture)); return;
            case Enum or Guid or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly or Uri or Version: sb.Append(value); return;
            case IFormattable f when value.GetType().IsPrimitive: sb.Append(f.ToString(null, CultureInfo.InvariantCulture)); return;
            case Type t: sb.Append("typeof(").Append(TypeName(t)).Append(')'); return;
            case JsonNode node: sb.Append(node.ToJsonString(Indented)); return;
            case JsonElement element: sb.Append(JsonSerializer.Serialize(element, Indented)); return;
            case JsonDocument document: sb.Append(JsonSerializer.Serialize(document.RootElement, Indented)); return;
            case Exception ex: sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message); return;
            case Delegate d: sb.Append(TypeName(d.GetType())).Append(" (").Append(d.Method.Name).Append(')'); return;
            case Task task: WriteTask(sb, task, depth); return;
            case ITuple tuple: WriteItems(sb, "", Enumerable.Range(0, tuple.Length).Select(i => Inline(tuple[i], depth + 1)).ToList(), false, "(", ")", depth); return;
        }

        var type = value.GetType();
        if (depth >= MaxDepth) { sb.Append(value is IEnumerable ? TypeName(type) + " { … }" : Short(value)); return; }

        if (value is IDictionary dictionary)
        {
            var entries = new List<string>();
            var more = false;
            foreach (DictionaryEntry e in dictionary)
            {
                if (entries.Count == MaxItems) { more = true; break; }
                entries.Add("[" + Inline(e.Key, depth + 1) + "] = " + Inline(e.Value, depth + 1));
            }
            WriteItems(sb, $"{TypeName(type)}({dictionary.Count}) ", entries, more, "{", "}", depth);
            return;
        }
        if (value is IEnumerable sequence)
        {
            var items = new List<string>();
            var more = false;
            try
            {
                foreach (var item in sequence)
                {
                    if (items.Count == MaxItems) { more = true; break; }
                    items.Add(Inline(item, depth + 1));
                }
            }
            catch (Exception ex) { items.Add($"!{ex.GetType().Name}: {ex.Message}"); }
            WriteItems(sb, SequenceName(type, sequence, more ? null : items.Count) + " ", items, more, "{", "}", depth);
            return;
        }

        // Something with a ToString of its own says it best: records, anonymous types, most value types.
        if (HasOwnToString(type) && !IsPlainData(type)) { sb.Append(value); return; }
        WriteProperties(sb, value, type, depth);
    }

    private static void WriteTask(StringBuilder sb, Task task, int depth)
    {
        var name = TaskResultType(task) is { } result ? $"Task<{TypeName(result)}>" : "Task";
        sb.Append(name).Append(" (").Append(task.Status).Append(')');
        if (task.IsCompletedSuccessfully && TaskResult(task) is (true, var done)) sb.Append(" = ").Append(Inline(done, depth + 1));
        else if (task.IsFaulted) sb.Append(": ").Append(task.Exception!.GetBaseException().Message);
    }

    /// <summary>The Task or Task&lt;T&gt; a task's own class derives from (async methods return compiler-made subclasses).</summary>
    private static Type TaskType(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t == typeof(Task) || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))) return t;
        return typeof(Task);
    }

    /// <summary>The result of a completed Task&lt;T&gt;. Has is false for a plain Task (and for the runtime's "no result" placeholder).</summary>
    public static (bool Has, object? Value) TaskResult(Task task)
    {
        if (TaskResultType(task) is null) return (false, null);
        return (true, TaskType(task.GetType()).GetProperty("Result")!.GetValue(task));
    }

    private static Type? TaskResultType(Task task)
    {
        var type = TaskType(task.GetType());
        return type.IsGenericType && type.GetGenericArguments()[0] is { Name: not "VoidTaskResult" } result ? result : null;
    }

    private static string SequenceName(Type type, IEnumerable sequence, int? count)
    {
        if (type.IsArray) return $"{TypeName(type.GetElementType()!)}[{count?.ToString() ?? "…"}]";
        // Iterators and LINQ results have names only a compiler could love; say what they yield instead.
        var visible = type.IsPublic || type.IsNestedPublic;
        if (!visible || type.Name.Contains('<'))
        {
            var element = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
            return element is null ? "IEnumerable" : $"IEnumerable<{TypeName(element)}>";
        }
        return sequence is ICollection collection ? $"{TypeName(type)}({collection.Count})" : TypeName(type);
    }

    private static void WriteProperties(StringBuilder sb, object value, Type type, int depth)
    {
        var members = new List<string>();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0).ToList();
        // Something big (a control, a service) is named by the few properties that tell one from another.
        var big = properties.Count > MaxProperties;
        if (big) properties = properties.Where(p => Telling.Contains(p.Name)).DistinctBy(p => p.Name).ToList();
        foreach (var p in properties)
        {
            string text;
            try { text = Inline(p.GetValue(value), depth + 1); }
            catch (Exception ex) { text = "!" + (ex.InnerException ?? ex).GetType().Name; }
            if (big && text is "null" or "\"\"") continue;
            members.Add(p.Name + " = " + text);
        }
        if (big && members.Count == 0) { sb.Append(TypeName(type)); return; }
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance).Take(40))
            members.Add(f.Name + " = " + Inline(f.GetValue(value), depth + 1));
        if (members.Count == 0) { sb.Append(value); return; }
        WriteItems(sb, IsAnonymous(type) ? "" : TypeName(type) + " ", members, false, "{", "}", depth);
    }

    /// <summary>On one line when it fits, otherwise one item per line.</summary>
    private static void WriteItems(StringBuilder sb, string head, List<string> items, bool more, string open, string close, int depth)
    {
        if (more) items.Add("…");
        var pad = open == "{" ? " " : "";
        if (items.Count == 0) { sb.Append(head).Append(open).Append(pad).Append(close); return; }
        var oneLine = head + open + pad + string.Join(", ", items) + pad + close;
        if (oneLine.Length <= MaxWidth && !oneLine.Contains('\n')) { sb.Append(oneLine); return; }
        var indent = new string(' ', 2);
        sb.Append(head).Append(open).Append('\n');
        for (var i = 0; i < items.Count; i++)
            sb.Append(indent).Append(items[i].Replace("\n", "\n" + indent)).Append(i < items.Count - 1 ? ",\n" : "\n");
        sb.Append(close);
    }

    private static string Inline(object? value, int depth)
    {
        var sb = new StringBuilder();
        Write(sb, value, depth);
        return sb.ToString();
    }

    private static string Short(object value)
    {
        try { return value.ToString() ?? TypeName(value.GetType()); } catch { return TypeName(value.GetType()); }
    }

    private static bool HasOwnToString(Type type) => type.GetMethod("ToString", Type.EmptyTypes)?.DeclaringType is { } d && d != typeof(object) && d != typeof(ValueType);

    private static bool IsAnonymous(Type type) => type.Name.StartsWith("<>", StringComparison.Ordinal) && type.Name.Contains("AnonymousType", StringComparison.Ordinal);

    /// <summary>Anonymous types and records print their properties; this class lays them out instead, so strings are quoted and long ones wrap.</summary>
    private static bool IsPlainData(Type type) => IsAnonymous(type) || type.GetMethod("<Clone>$") is not null;

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\0' => "\\0", _ => c.ToString() });
        return sb.Append('"').ToString();
    }
}
