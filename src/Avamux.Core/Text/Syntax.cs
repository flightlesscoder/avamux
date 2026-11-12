using System.Net;
using System.Text;

namespace Avamux.Core.Text;

public enum Tok { Text, Comment, String, Number, Keyword, Type, Func, Punct, Meta, Tag, Attr, Heading, Add, Del }

public readonly record struct Span(Tok Kind, string Text);

/// <summary>
/// A small syntax highlighter: one generic lexer driven by a per-language description (comment and string
/// delimiters, keyword lists), plus a few line-oriented modes for markup and data formats. It aims for readable
/// colouring of common languages, not for a parser's accuracy.
/// </summary>
public static class Syntax
{
    private sealed record Lang(string[] Line, (string Open, string Close)[] Block, string Quotes, HashSet<string> Keywords, HashSet<string> Types,
        bool IgnoreCase = false, bool UpperIsType = false, string[]? Triple = null, string Mode = "code", bool DollarVars = false, bool HashMeta = false);

    private static HashSet<string> W(string words, bool ignoreCase = false) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly (string, string)[] CBlock = [("/*", "*/")];
    private static readonly string[] Slash = ["//"];
    private static readonly string[] Hash = ["#"];
    private static readonly HashSet<string> None = [];

    private static readonly Dictionary<string, Lang> Langs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = new(Slash, CBlock, "\"'", W("auto break case const continue default do else enum extern for goto if inline register restrict return sizeof static struct switch typedef union volatile while NULL true false"),
            W("char double float int long short signed unsigned void bool size_t uint8_t uint16_t uint32_t uint64_t int8_t int16_t int32_t int64_t FILE"), HashMeta: true),
        ["cpp"] = new(Slash, CBlock, "\"'", W("alignas alignof auto break case catch class const constexpr const_cast continue decltype default delete do dynamic_cast else enum explicit export extern final for friend goto if inline mutable namespace new noexcept nullptr operator override private protected public reinterpret_cast return sizeof static static_cast struct switch template this throw try typedef typename union using virtual volatile while true false co_await co_return co_yield concept requires"),
            W("bool char double float int long short signed unsigned void wchar_t size_t string vector map set unique_ptr shared_ptr optional"), UpperIsType: true, HashMeta: true),
        ["csharp"] = new(Slash, CBlock, "\"'", W("abstract as async await base break case catch checked class const continue default delegate do else enum event explicit extern false finally fixed for foreach get global goto if implicit in init interface internal is lock namespace new null operator out override params partial private protected public readonly record ref required return sealed set sizeof stackalloc static struct switch this throw true try typeof unchecked unsafe using virtual volatile when where while with yield nameof not and or"),
            W("bool byte char decimal double dynamic float int long nint nuint object sbyte short string uint ulong ushort var void"), UpperIsType: true, Triple: ["\"\"\""], HashMeta: true),
        ["java"] = new(Slash, CBlock, "\"'", W("abstract assert break case catch class const continue default do else enum extends final finally for goto if implements import instanceof interface native new null package private protected public record return sealed static strictfp super switch synchronized this throw throws transient try volatile while true false yield permits var"),
            W("boolean byte char double float int long short void String Object Integer Long Double List Map Set Optional"), UpperIsType: true, Triple: ["\"\"\""]),
        ["javascript"] = new(Slash, CBlock, "\"'`", W("async await break case catch class const continue debugger default delete do else export extends finally for from function get if import in instanceof let new null of return set static super switch this throw try typeof undefined var void while with yield true false as"),
            W("Array Boolean Date Error JSON Map Math Number Object Promise RegExp Set String Symbol console document window globalThis"), UpperIsType: true),
        ["typescript"] = new(Slash, CBlock, "\"'`", W("abstract any as asserts async await break case catch class const continue declare default delete do else enum export extends finally for from function get if implements import in infer instanceof interface is keyof let module namespace never new null of override private protected public readonly return satisfies set static super switch this throw try type typeof undefined unique unknown var void while yield true false"),
            W("string number boolean bigint symbol object Array Promise Record Partial Readonly Map Set Date Error console"), UpperIsType: true),
        ["go"] = new(Slash, CBlock, "\"'`", W("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var nil true false iota"),
            W("bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any"), UpperIsType: true),
        ["rust"] = new(Slash, CBlock, "\"", W("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while macro_rules"),
            W("bool char f32 f64 i8 i16 i32 i64 i128 isize str u8 u16 u32 u64 u128 usize String Vec Option Result Box Rc Arc Some None Ok Err"), UpperIsType: true),
        ["kotlin"] = new(Slash, CBlock, "\"'", W("abstract as break by catch class companion const constructor continue data do else enum false final finally for fun if import in init inline interface internal is lateinit null object open operator override package private protected public return sealed super suspend this throw true try typealias val var when where while"),
            W("Any Boolean Byte Char Double Float Int Long Nothing Short String Unit List Map Set"), UpperIsType: true, Triple: ["\"\"\""]),
        ["swift"] = new(Slash, CBlock, "\"", W("actor as associatedtype async await break case catch class continue default defer deinit do else enum extension fallthrough false fileprivate final for func guard if import in init inout internal is let nil open operator override private protocol public repeat return self Self static struct subscript super switch throw throws true try typealias var where while"),
            W("Any Bool Character Double Float Int String Void Array Dictionary Set Optional"), UpperIsType: true, Triple: ["\"\"\""]),
        ["php"] = new(["//", "#"], CBlock, "\"'", W("abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty enum extends final finally fn for foreach function global if implements include instanceof interface isset list match namespace new null or print private protected public readonly require return static switch throw trait try unset use var while yield true false"),
            W("bool float int string void mixed iterable object self parent"), UpperIsType: true, DollarVars: true),
        ["python"] = new(Hash, [], "\"'", W("and as assert async await break case class continue def del elif else except finally for from global if import in is lambda match nonlocal not or pass raise return try while with yield True False None self"),
            W("bool bytes dict float frozenset int list object set str tuple type print len range open super isinstance enumerate zip map filter Exception"), UpperIsType: true, Triple: ["\"\"\"", "'''"]),
        ["ruby"] = new(Hash, [("=begin", "=end")], "\"'`", W("alias and begin break case class def defined? do else elsif end ensure false for if in module next nil not or redo rescue retry return self super then true undef unless until when while yield require require_relative attr_accessor attr_reader attr_writer puts"),
            None, UpperIsType: true),
        ["bash"] = new(Hash, [], "\"'`", W("if then else elif fi for while until do done case esac in function select time return exit break continue local export readonly declare typeset unset shift source alias set trap eval exec"),
            W("echo printf cd pwd read test true false ls cat grep sed awk find xargs mkdir rm cp mv chmod chown curl wget git sudo kill sleep"), DollarVars: true),
        ["powershell"] = new(Hash, [("<#", "#>")], "\"'", W("begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach from function if in param process return switch throw trap try until using while", true),
            None, IgnoreCase: true, DollarVars: true),
        ["lua"] = new(["--"], [("--[[", "]]")], "\"'", W("and break do else elseif end false for function goto if in local nil not or repeat return then true until while"),
            W("print pairs ipairs require string table math type tostring tonumber setmetatable getmetatable")),
        ["sql"] = new(["--"], CBlock, "'\"", W("add all alter and as asc begin between by case check column commit constraint create cross database default delete desc distinct drop else end exists foreign from full group having if in index inner insert into is join key left like limit not null offset on or order outer primary references returning right rollback select set table then transaction union unique update values view when where with", true),
            W("bigint binary bit blob boolean char date datetime decimal double float int integer json numeric real serial smallint text time timestamp uuid varchar count sum avg min max coalesce", true), IgnoreCase: true),
        ["css"] = new([], CBlock, "\"'", W("important inherit initial unset none auto"), None, Mode: "css"),
        ["scss"] = new(Slash, CBlock, "\"'", W("important inherit initial unset none auto"), None, Mode: "css", DollarVars: true),
        ["dockerfile"] = new(Hash, [], "\"'", W("FROM RUN CMD LABEL MAINTAINER EXPOSE ENV ADD COPY ENTRYPOINT VOLUME USER WORKDIR ARG ONBUILD STOPSIGNAL HEALTHCHECK SHELL AS", true), None, IgnoreCase: true, DollarVars: true),
        ["makefile"] = new(Hash, [], "\"'", W("ifeq ifneq ifdef ifndef else endif include define endef export unexport override vpath .PHONY"), None, DollarVars: true),
        ["gdscript"] = new(Hash, [], "\"'", W("and as assert await break breakpoint class class_name const continue elif else enum extends false for func if in is match not null or pass preload return self signal static super true var void while yield export onready tool"),
            W("bool float int String Array Dictionary Vector2 Vector3 Node Color Object"), UpperIsType: true, Triple: ["\"\"\""]),
        ["json"] = new([], [], "\"", W("true false null"), None, Mode: "json"),
        ["yaml"] = new(Hash, [], "\"'", W("true false null yes no on off ~", true), None, Mode: "yaml"),
        ["toml"] = new(Hash, [], "\"'", W("true false"), None, Mode: "ini", Triple: ["\"\"\"", "'''"]),
        ["ini"] = new(["#", ";"], [], "\"'", W("true false yes no on off", true), None, Mode: "ini"),
        ["xml"] = new([], [], "\"'", None, None, Mode: "markup"),
        ["markdown"] = new([], [], "", None, None, Mode: "markdown"),
        ["diff"] = new([], [], "", None, None, Mode: "diff"),
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["h"] = "c", ["cc"] = "cpp", ["cxx"] = "cpp", ["hpp"] = "cpp", ["hh"] = "cpp", ["c++"] = "cpp", ["cs"] = "csharp", ["c#"] = "csharp", ["csx"] = "csharp",
        ["js"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript", ["jsx"] = "javascript", ["node"] = "javascript",
        ["ts"] = "typescript", ["tsx"] = "typescript", ["mts"] = "typescript", ["golang"] = "go", ["rs"] = "rust", ["kt"] = "kotlin", ["kts"] = "kotlin",
        ["py"] = "python", ["py3"] = "python", ["python3"] = "python", ["pyw"] = "python", ["rb"] = "ruby",
        ["sh"] = "bash", ["shell"] = "bash", ["zsh"] = "bash", ["fish"] = "bash", ["console"] = "bash", ["shellscript"] = "bash", ["ksh"] = "bash",
        ["ps1"] = "powershell", ["pwsh"] = "powershell", ["psm1"] = "powershell", ["psql"] = "sql", ["mysql"] = "sql", ["sqlite"] = "sql", ["pgsql"] = "sql",
        ["less"] = "scss", ["sass"] = "scss", ["docker"] = "dockerfile", ["make"] = "makefile", ["mk"] = "makefile", ["gd"] = "gdscript",
        ["jsonc"] = "json", ["json5"] = "json", ["webmanifest"] = "json", ["yml"] = "yaml", ["conf"] = "ini", ["cfg"] = "ini", ["properties"] = "ini", ["env"] = "ini", ["editorconfig"] = "ini",
        ["html"] = "xml", ["htm"] = "xml", ["xhtml"] = "xml", ["svg"] = "xml", ["axaml"] = "xml", ["xaml"] = "xml", ["csproj"] = "xml", ["props"] = "xml", ["targets"] = "xml", ["slnx"] = "xml", ["plist"] = "xml", ["vue"] = "xml",
        ["md"] = "markdown", ["markdown"] = "markdown", ["mdx"] = "markdown", ["patch"] = "diff",
    };

    /// <summary>Canonical language name for a fence label or file extension, or null when it is not known.</summary>
    public static string? Normalize(string? lang)
    {
        var l = (lang ?? "").Trim().TrimStart('.').ToLowerInvariant();
        // Fence info strings may carry attributes: ```js {1,3} or ```python title="x".
        var cut = l.IndexOfAny([' ', '{', ',', ':']);
        if (cut > 0) l = l[..cut];
        if (l.Length == 0) return null;
        if (Aliases.TryGetValue(l, out var a)) l = a;
        return Langs.ContainsKey(l) ? l : null;
    }

    public static string? LanguageOf(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
        if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase) || name.Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase)) return "makefile";
        if (name is ".bashrc" or ".zshrc" or ".profile" or ".bash_profile" or ".zprofile") return "bash";
        if (name is ".gitconfig" or ".gitmodules") return "ini";
        return Normalize(Path.GetExtension(name));
    }

    /// <summary>Tokens per line. An unknown language gives one plain span per line.</summary>
    public static List<List<Span>> Highlight(string code, string? lang)
    {
        var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<List<Span>>(lines.Length);
        if (Normalize(lang) is not { } name)
        {
            foreach (var l in lines) result.Add([new Span(Tok.Text, l)]);
            return result;
        }
        var def = Langs[name];
        var state = new State();
        foreach (var line in lines)
        {
            var spans = new List<Span>();
            switch (def.Mode)
            {
                case "markup": Markup(line, spans, state); break;
                case "markdown": MarkdownLine(line, spans, state); break;
                case "diff": spans.Add(new Span(line.StartsWith("@@") ? Tok.Meta : line.StartsWith('+') ? Tok.Add : line.StartsWith('-') ? Tok.Del : line.StartsWith("diff ") || line.StartsWith("index ") ? Tok.Comment : Tok.Text, line)); break;
                default: Code(line, spans, state, def); break;
            }
            result.Add(Merge(spans));
        }
        return result;
    }

    private sealed class State
    {
        public string? Until;      // inside a block comment or multi-line string: the closing delimiter
        public Tok UntilKind;
        public string? Fence;      // markdown: inside a fenced block
        public bool InTag;         // markup: inside <tag …
    }

    private static List<Span> Merge(List<Span> spans)
    {
        var merged = new List<Span>(spans.Count);
        foreach (var s in spans)
        {
            if (s.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Kind == s.Kind) merged[^1] = new Span(s.Kind, merged[^1].Text + s.Text);
            else merged.Add(s);
        }
        return merged;
    }

    private static bool IsIdStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsId(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void Code(string line, List<Span> spans, State st, Lang def)
    {
        var i = 0;
        var n = line.Length;
        var text = new StringBuilder();
        void Flush() { if (text.Length > 0) { spans.Add(new Span(Tok.Text, text.ToString())); text.Clear(); } }

        if (st.Until is { } closing)
        {
            var end = line.IndexOf(closing, StringComparison.Ordinal);
            if (end < 0) { spans.Add(new Span(st.UntilKind, line)); return; }
            spans.Add(new Span(st.UntilKind, line[..(end + closing.Length)]));
            i = end + closing.Length;
            st.Until = null;
        }
        // Line-level forms.
        var trimmed = line.TrimStart();
        if (i == 0 && def.HashMeta && trimmed.StartsWith('#')) { spans.Add(new Span(Tok.Meta, line)); return; }
        if (i == 0 && def.Mode == "ini" && trimmed.StartsWith('[') && trimmed.TrimEnd().EndsWith(']')) { spans.Add(new Span(Tok.Heading, line)); return; }
        var keyDone = def.Mode is not ("yaml" or "ini");

        while (i < n)
        {
            var c = line[i];
            // comments
            if (def.Line.FirstOrDefault(p => string.CompareOrdinal(line, i, p, 0, p.Length) == 0) is { } lc
                && (lc != "#" || i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                Flush(); spans.Add(new Span(Tok.Comment, line[i..])); return;
            }
            var block = def.Block.FirstOrDefault(b => string.CompareOrdinal(line, i, b.Open, 0, b.Open.Length) == 0);
            if (block.Open is not null)
            {
                Flush();
                var end = line.IndexOf(block.Close, i + block.Open.Length, StringComparison.Ordinal);
                if (end < 0) { spans.Add(new Span(Tok.Comment, line[i..])); st.Until = block.Close; st.UntilKind = Tok.Comment; return; }
                spans.Add(new Span(Tok.Comment, line[i..(end + block.Close.Length)]));
                i = end + block.Close.Length;
                continue;
            }
            // strings
            if (def.Triple?.FirstOrDefault(t => string.CompareOrdinal(line, i, t, 0, t.Length) == 0) is { } triple)
            {
                Flush();
                var end = line.IndexOf(triple, i + triple.Length, StringComparison.Ordinal);
                if (end < 0) { spans.Add(new Span(Tok.String, line[i..])); st.Until = triple; st.UntilKind = Tok.String; return; }
                spans.Add(new Span(Tok.String, line[i..(end + triple.Length)]));
                i = end + triple.Length;
                continue;
            }
            if (def.Quotes.Contains(c))
            {
                // An apostrophe inside a word (don't) or a lifetime ('a) is not a string.
                if (c == '\'' && i > 0 && IsId(line[i - 1])) { text.Append(c); i++; continue; }
                var j = i + 1;
                while (j < n && line[j] != c) j += line[j] == '\\' && j + 1 < n ? 2 : 1;
                var closed = j < n;
                j = Math.Min(n, j + 1);
                Flush();
                var str = line[i..j];
                var isKey = def.Mode == "json" && closed && line.AsSpan(j).TrimStart().StartsWith(":");
                spans.Add(new Span(isKey ? Tok.Attr : Tok.String, str));
                if (!closed && c == '`') { st.Until = "`"; st.UntilKind = Tok.String; }
                i = j;
                continue;
            }
            if (def.DollarVars && c == '$' && i + 1 < n && (IsIdStart(line[i + 1]) || line[i + 1] is '{' or '('))
            {
                var j = i + 1;
                if (line[j] is '{' or '(') { var close = line[j] == '{' ? '}' : ')'; j = line.IndexOf(close, j); j = j < 0 ? n : j + 1; }
                else while (j < n && IsId(line[j])) j++;
                Flush(); spans.Add(new Span(Tok.Attr, line[i..j])); i = j; continue;
            }
            if (char.IsDigit(c) && (i == 0 || !IsId(line[i - 1])))
            {
                var j = i + 1;
                while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] is '.' or '_' || (line[j] is '+' or '-' && line[j - 1] is 'e' or 'E' && !line[i..j].StartsWith("0x", StringComparison.OrdinalIgnoreCase)))) j++;
                Flush(); spans.Add(new Span(Tok.Number, line[i..j])); i = j; continue;
            }
            if (IsIdStart(c) || (c == '@' && i + 1 < n && IsIdStart(line[i + 1])))
            {
                var j = i + 1;
                while (j < n && (IsId(line[j]) || (def.Mode is "css" or "yaml" or "ini" && line[j] is '-' or '.') || (line[j] == '?' && def.Keywords.Contains(line[i..(j + 1)])))) j++;
                var word = line[i..j];
                var after = j < n ? line.AsSpan(j).TrimStart() : default;
                Tok kind;
                if (c == '@') kind = Tok.Meta;
                else if (!keyDone && after.Length > 0 && (after[0] == ':' || (def.Mode == "ini" && after[0] == '='))) { kind = Tok.Attr; keyDone = true; }
                else if (def.Mode == "css" && after.Length > 0 && after[0] == ':' && line.TrimStart().StartsWith(word)) kind = Tok.Attr;
                else if (def.Keywords.Contains(word)) kind = Tok.Keyword;
                else if (def.Types.Contains(word)) kind = Tok.Type;
                else if (def.Mode == "code" && j < n && line[j] == '(') kind = Tok.Func;
                else if (def.Mode == "code" && j < n && line[j] == '!' && def == Langs["rust"]) kind = Tok.Func;
                else if (def.UpperIsType && char.IsUpper(c) && word.Length > 1 && word.Any(char.IsLower)) kind = Tok.Type;
                else kind = Tok.Text;
                if (kind == Tok.Text) text.Append(word); else { Flush(); spans.Add(new Span(kind, word)); }
                i = j;
                continue;
            }
            if (!char.IsWhiteSpace(c) && c != '-') keyDone = keyDone || def.Mode != "yaml" || c != '-';
            text.Append(c);
            i++;
        }
        Flush();
    }

    private static void Markup(string line, List<Span> spans, State st)
    {
        var i = 0;
        var n = line.Length;
        while (i < n)
        {
            if (st.Until is { } closing)
            {
                var end = line.IndexOf(closing, i, StringComparison.Ordinal);
                if (end < 0) { spans.Add(new Span(Tok.Comment, line[i..])); return; }
                spans.Add(new Span(Tok.Comment, line[i..(end + closing.Length)]));
                i = end + closing.Length; st.Until = null;
                continue;
            }
            if (!st.InTag)
            {
                var lt = line.IndexOf('<', i);
                if (lt < 0) { spans.Add(new Span(Tok.Text, line[i..])); return; }
                spans.Add(new Span(Tok.Text, line[i..lt]));
                if (string.CompareOrdinal(line, lt, "<!--", 0, 4) == 0) { st.Until = "-->"; i = lt; continue; }
                var j = lt + 1;
                while (j < n && (line[j] is '/' or '!' or '?' || IsId(line[j]) || line[j] is ':' or '-' or '.')) j++;
                spans.Add(new Span(Tok.Tag, line[lt..j]));
                i = j; st.InTag = true;
                continue;
            }
            var c = line[i];
            if (c == '>') { spans.Add(new Span(Tok.Tag, ">")); st.InTag = false; i++; }
            else if (c is '/' or '?' && i + 1 < n && line[i + 1] == '>') { spans.Add(new Span(Tok.Tag, line[i..(i + 2)])); st.InTag = false; i += 2; }
            else if (c is '"' or '\'')
            {
                var end = line.IndexOf(c, i + 1);
                end = end < 0 ? n : end + 1;
                spans.Add(new Span(Tok.String, line[i..end])); i = end;
            }
            else if (IsIdStart(c))
            {
                var j = i + 1;
                while (j < n && (IsId(line[j]) || line[j] is ':' or '-' or '.')) j++;
                spans.Add(new Span(Tok.Attr, line[i..j])); i = j;
            }
            else { spans.Add(new Span(Tok.Text, c.ToString())); i++; }
        }
    }

    private static void MarkdownLine(string line, List<Span> spans, State st)
    {
        var t = line.TrimStart();
        if (t.StartsWith("```") || t.StartsWith("~~~"))
        {
            st.Fence = st.Fence is null ? t[..3] : t.StartsWith(st.Fence) ? null : st.Fence;
            spans.Add(new Span(Tok.Meta, line));
        }
        else if (st.Fence is not null) spans.Add(new Span(Tok.String, line));
        else if (t.StartsWith('#')) spans.Add(new Span(Tok.Heading, line));
        else if (t.StartsWith('>')) spans.Add(new Span(Tok.Comment, line));
        else if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("+ ") || (t.Length > 2 && char.IsDigit(t[0]) && t.IndexOf(". ", StringComparison.Ordinal) is > 0 and < 4))
        {
            var marker = line.Length - t.Length + t.IndexOf(' ') + 1;
            spans.Add(new Span(Tok.Keyword, line[..marker]));
            Inline(line[marker..], spans);
        }
        else Inline(line, spans);
    }

    private static void Inline(string text, List<Span> spans)
    {
        var i = 0;
        while (i < text.Length)
        {
            var tick = text.IndexOf('`', i);
            var link = text.IndexOf('[', i);
            var next = tick >= 0 && (link < 0 || tick < link) ? tick : link;
            if (next < 0) { spans.Add(new Span(Tok.Text, text[i..])); return; }
            spans.Add(new Span(Tok.Text, text[i..next]));
            if (next == tick)
            {
                var end = text.IndexOf('`', tick + 1);
                end = end < 0 ? text.Length : end + 1;
                spans.Add(new Span(Tok.String, text[tick..end])); i = end;
            }
            else
            {
                var end = text.IndexOf(')', link);
                var mid = text.IndexOf("](", link, StringComparison.Ordinal);
                var wiki = text.AsSpan(link).StartsWith("[[") ? text.IndexOf("]]", link, StringComparison.Ordinal) : -1;
                if (wiki > 0) { spans.Add(new Span(Tok.Func, text[link..(wiki + 2)])); i = wiki + 2; }
                else if (mid > 0 && end > mid) { spans.Add(new Span(Tok.Func, text[link..(end + 1)])); i = end + 1; }
                else { spans.Add(new Span(Tok.Text, "[")); i = link + 1; }
            }
        }
    }

    public static string CssClass(Tok kind) => kind switch
    {
        Tok.Comment => "tk-c", Tok.String => "tk-s", Tok.Number => "tk-n", Tok.Keyword => "tk-k", Tok.Type => "tk-t", Tok.Func => "tk-f",
        Tok.Meta => "tk-m", Tok.Tag => "tk-g", Tok.Attr => "tk-a", Tok.Heading => "tk-h", Tok.Add => "tk-add", Tok.Del => "tk-del", _ => "",
    };

    /// <summary>Theme token whose colour a kind of token is drawn in.</summary>
    public static string ThemeToken(Tok kind) => kind switch
    {
        Tok.Comment => "faint", Tok.String => "ok", Tok.Number => "orange", Tok.Keyword => "purple", Tok.Type => "warn", Tok.Func => "accent",
        Tok.Meta => "cyan", Tok.Tag => "error", Tok.Attr => "orange", Tok.Heading => "accent", Tok.Add => "okHi", Tok.Del => "errorText", _ => "textCode",
    };

    /// <summary>Highlighted code as HTML spans (no surrounding pre/code).</summary>
    public static string ToHtml(string code, string? lang)
    {
        var sb = new StringBuilder();
        var lines = Highlight(code, lang);
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (var s in lines[i])
            {
                var cls = CssClass(s.Kind);
                if (cls.Length == 0) sb.Append(WebUtility.HtmlEncode(s.Text));
                else sb.Append("<span class=\"").Append(cls).Append("\">").Append(WebUtility.HtmlEncode(s.Text)).Append("</span>");
            }
            if (i < lines.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }
}
