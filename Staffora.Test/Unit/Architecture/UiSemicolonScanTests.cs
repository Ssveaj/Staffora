using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow;

namespace Staffora.Test.Unit.Architecture;

public partial class UiSemicolonScanTests
{
    private const string WorkflowAttributesNamespace = "Staffora.Core.Workflow.WorkflowAttributes";

    [Fact]
    public void No_Razor_text_or_rendered_attribute_value_holds_a_semicolon()
    {
        var files = UiWordScanTests.RazorFiles().ToList();
        var offences = files
            .SelectMany(file => RazorOffences(UiWordScanTests.Relative(file), File.ReadAllText(file)))
            .ToList();

        Assert.NotEmpty(files);
        Assert.Empty(offences);
    }

    [Fact]
    public void No_workflow_step_holds_a_semicolon()
    {
        var texts = UiWordScanTests.ProductAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(WorkflowRegistry.HasWorkflow)
            .SelectMany(type => WorkflowRegistry.Default.Get(type).Transitions
                .SelectMany(step => UiWordScanTests.StepTexts($"{type.Name}.{step.Key}", step)))
            .ToList();

        Assert.NotEmpty(texts);
        Assert.Empty(TextOffences(texts));
    }

    [Fact]
    public void No_reason_constant_holds_a_semicolon()
    {
        var texts = ReasonTexts().ToList();

        Assert.NotEmpty(texts);
        Assert.Empty(TextOffences(texts));
    }

    [Fact]
    public void No_display_or_description_attribute_holds_a_semicolon()
    {
        var texts = UiWordScanTests.DisplayTexts().ToList();

        Assert.NotEmpty(texts);
        Assert.Empty(TextOffences(texts));
    }

    [Fact]
    public void No_notice_of_the_message_catalogue_holds_a_semicolon()
    {
        var texts = MessageTypes.All
            .SelectMany(type => MessageCatalogue.Templates.TryGetValue(type, out var template)
                ? new[] { ($"{type.Key} subject", template.Subject), ($"{type.Key} text", template.Text) }
                : [])
            .Append(("message type names", string.Join(" ", MessageTypes.All.Select(type => type.Name))))
            .ToList();

        Assert.Empty(TextOffences(texts));
    }

    [Fact]
    public void The_Razor_scan_catches_text_and_attribute_values_and_leaves_code_css_scripts_entities_and_comments()
    {
        const string sample = """
            @page "/sample"
            @inject NavigationManager Navigation
            @* A comment; never shown. *@
            <!-- A comment; never shown. -->
            <style>
                .note { color: red; margin: 0; }
            </style>
            <p class="note" style="width: 100%; margin: 0">Nothing yet; add one.</p>
            <RadzenButton Text="Save; then go" Click="@(() => { this.a = 1; this.b = 2; })" @onclick:preventDefault="true" />
            <span title="@string.Join("; ", parts)">&nbsp;&amp; @(shown ? "a; b" : "c")</span>
            @if (shown)
            {
                var count = this.items.Count;
                <em>Shown; for now</em>
            }
            else
            {
                <text>None; yet</text>
            }
            <script>window.x = 1; window.y = 2;</script>
            @code {
                // A comment; code.
                private string Label => "Code; not markup";
            }
            """;

        Assert.Equal(
            ["sample.razor:8 text: Nothing yet; add one.", "sample.razor:9 Text: Save; then go", "sample.razor:14 text: Shown; for now", "sample.razor:18 text: None; yet"],
            RazorOffences("sample.razor", sample));
        Assert.Empty(RazorOffences("ok.razor", "<p>Jobs, slots and periods: a time on Monday. The staff member's work.</p>"));
        Assert.Equal(["A refusal: The job is canceled; it is kept."], TextOffences([("A refusal", "The job is canceled; it is kept.")]));
        Assert.Empty(TextOffences([("A refusal", "The job is canceled, and it is kept.")]));
    }

    private static List<string> RazorOffences(string path, string text) =>
        [.. RazorTextScanner.Scan(UiWordScanTests.WithoutComments(text.ReplaceLineEndings("\n")))
            .Select(found => $"{path}:{found.Line} {found.Where}: {found.Text}")];

    private static List<string> TextOffences(IEnumerable<(string Where, string Text)> texts) =>
        [.. texts.Where(entry => entry.Text.Contains(';')).Select(entry => $"{entry.Where}: {entry.Text}")];

    private static IEnumerable<(string Where, string Text)> ReasonTexts()
    {
        const BindingFlags statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = UiWordScanTests.ProductAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.ContainsGenericParameters && type.GetCustomAttribute<CompilerGeneratedAttribute>() is null);
        foreach (var type in types)
        {
            var workflowAttribute = type.Namespace == WorkflowAttributesNamespace;
            foreach (var field in type.GetFields(statics).Where(field => field.FieldType == typeof(string)))
            {
                if (!workflowAttribute && !IsReasonName(field.Name))
                {
                    continue;
                }

                var value = field.IsLiteral ? field.GetRawConstantValue() as string : field.IsInitOnly ? field.GetValue(null) as string : null;
                if (!string.IsNullOrEmpty(value))
                {
                    yield return ($"{type.Name}.{field.Name}", value);
                }
            }
        }
    }

    private static bool IsReasonName(string name) =>
        name.EndsWith("Reason", StringComparison.Ordinal) || name.EndsWith("Refusal", StringComparison.Ordinal);

    private sealed partial class RazorTextScanner
    {
        private static readonly HashSet<string> statementKeywords = ["if", "foreach", "for", "while", "switch", "using", "lock", "try", "do"];
        private static readonly HashSet<string> directives = ["page", "inject", "using", "attribute", "layout", "inherits", "implements", "namespace", "typeparam", "rendermode", "preservewhitespace", "model"];
        private static readonly HashSet<string> codeBlocks = ["code", "functions"];
        private static readonly HashSet<string> voidElements = new(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"], StringComparer.OrdinalIgnoreCase);

        private readonly string text;
        private readonly List<(int Line, string Where, string Text)> found = [];

        private RazorTextScanner(string text)
        {
            this.text = text;
        }

        public static List<(int Line, string Where, string Text)> Scan(string text)
        {
            var scanner = new RazorTextScanner(text);
            scanner.Markup(0, endTag: null);
            return scanner.found;
        }

        private int Line(int index)
        {
            var line = 1;
            for (var at = 0; at < index && at < this.text.Length; at++)
            {
                if (this.text[at] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private int Markup(int index, string? endTag)
        {
            var textStart = index;
            var depth = 0;
            while (index < this.text.Length)
            {
                var character = this.text[index];
                if (character == '@')
                {
                    this.Text(textStart, index);
                    index = this.Transition(index);
                    textStart = index;
                    continue;
                }

                if (character == '<')
                {
                    if (CloseTag().Match(this.text, index) is { Success: true } close)
                    {
                        this.Text(textStart, index);
                        index += close.Length;
                        textStart = index;
                        if (endTag is not null && close.Groups["name"].Value == endTag)
                        {
                            if (depth == 0)
                            {
                                return index;
                            }

                            depth--;
                        }

                        continue;
                    }

                    if (OpenTag().Match(this.text, index) is { Success: true } open)
                    {
                        this.Text(textStart, index);
                        var name = open.Groups["name"].Value;
                        (index, var selfClosing) = this.Tag(index + open.Length, name);
                        if (!selfClosing && (name.Equals("style", StringComparison.OrdinalIgnoreCase) || name.Equals("script", StringComparison.OrdinalIgnoreCase)))
                        {
                            var end = this.text.IndexOf($"</{name}", index, StringComparison.OrdinalIgnoreCase);
                            var closed = end < 0 ? -1 : this.text.IndexOf('>', end);
                            index = closed < 0 ? this.text.Length : closed + 1;
                        }
                        else if (!selfClosing && name == endTag)
                        {
                            depth++;
                        }

                        textStart = index;
                        continue;
                    }
                }

                index++;
            }

            this.Text(textStart, index);
            return index;
        }

        private void Text(int start, int end)
        {
            if (end <= start)
            {
                return;
            }

            var lines = Entity().Replace(this.text[start..end], string.Empty).Split('\n');
            var first = this.Line(start);
            for (var at = 0; at < lines.Length; at++)
            {
                if (lines[at].Contains(';'))
                {
                    this.found.Add((first + at, "text", lines[at].Trim()));
                }
            }
        }

        private (int Index, bool SelfClosing) Tag(int index, string name)
        {
            while (index < this.text.Length)
            {
                var character = this.text[index];
                if (char.IsWhiteSpace(character))
                {
                    index++;
                    continue;
                }

                if (this.text.AsSpan(index).StartsWith("/>"))
                {
                    return (index + 2, true);
                }

                if (character == '>')
                {
                    return (index + 1, voidElements.Contains(name));
                }

                if (this.text.AsSpan(index).StartsWith("@("))
                {
                    index = this.SkipBalanced(index + 1, '(', ')');
                    continue;
                }

                var attribute = AttributeName().Match(this.text, index);
                if (!attribute.Success)
                {
                    index++;
                    continue;
                }

                var at = index;
                index += attribute.Length;
                var equals = AttributeEquals().Match(this.text, index);
                if (!equals.Success)
                {
                    continue;
                }

                index += equals.Length;
                if (index < this.text.Length && this.text[index] is '"' or '\'')
                {
                    index = this.QuotedValue(at, attribute.Value, index);
                }
                else if (index < this.text.Length && this.text[index] == '@')
                {
                    index = this.text.AsSpan(index).StartsWith("@(") ? this.SkipBalanced(index + 1, '(', ')') : this.SkipImplicit(index + 1);
                }
                else
                {
                    while (index < this.text.Length && !char.IsWhiteSpace(this.text[index]) && this.text[index] != '>')
                    {
                        index++;
                    }
                }
            }

            return (index, false);
        }

        private int QuotedValue(int at, string name, int index)
        {
            var quote = this.text[index];
            var start = index + 1;
            var position = start;
            var segment = start;
            var literal = new System.Text.StringBuilder();
            while (position < this.text.Length && this.text[position] != quote)
            {
                if (this.text[position] == '@')
                {
                    literal.Append(this.text, segment, position - segment);
                    position = this.text.AsSpan(position).StartsWith("@(") ? this.SkipBalanced(position + 1, '(', ')')
                        : this.text.AsSpan(position).StartsWith("@@") ? position + 2
                        : this.SkipImplicit(position + 1);
                    segment = position;
                    continue;
                }

                position++;
            }

            literal.Append(this.text, segment, Math.Min(position, this.text.Length) - segment);
            var raw = this.text[start..Math.Min(position, this.text.Length)];
            var value = Entity().Replace(literal.ToString(), string.Empty);
            if (!name.StartsWith('@') && !name.Equals("style", StringComparison.OrdinalIgnoreCase) && !raw.Contains("=>", StringComparison.Ordinal) && value.Contains(';'))
            {
                this.found.Add((this.Line(at), name, value.Trim()));
            }

            return position + 1;
        }

        private int Transition(int index)
        {
            var rest = this.text.AsSpan(index);
            if (rest.StartsWith("@@"))
            {
                return index + 2;
            }

            if (rest.StartsWith("@("))
            {
                return this.SkipBalanced(index + 1, '(', ')');
            }

            if (rest.StartsWith("@{"))
            {
                return this.CodeBlock(index + 1);
            }

            if (rest.StartsWith("@:"))
            {
                return index + 2;
            }

            var word = Identifier().Match(this.text, index + 1);
            if (!word.Success)
            {
                return index + 1;
            }

            var after = index + 1 + word.Length;
            var lineStart = this.text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            var atLineStart = string.IsNullOrWhiteSpace(this.text[lineStart..index]);
            if (codeBlocks.Contains(word.Value))
            {
                var open = this.text.IndexOf('{', after);
                return open < 0 ? this.text.Length : this.SkipBalanced(open, '{', '}');
            }

            if (statementKeywords.Contains(word.Value) && !(word.Value == "using" && atLineStart && !OpenParenthesis().IsMatch(this.text[after..Math.Min(this.text.Length, after + 64)])))
            {
                return this.Statement(after);
            }

            if (atLineStart && directives.Contains(word.Value))
            {
                var end = this.text.IndexOf('\n', after);
                return end < 0 ? this.text.Length : end;
            }

            return this.SkipImplicit(index + 1);
        }

        private int Statement(int index)
        {
            while (true)
            {
                index = this.SkipWhiteSpace(index);
                if (index < this.text.Length && this.text[index] == '(')
                {
                    index = this.SkipWhiteSpace(this.SkipBalanced(index, '(', ')'));
                }

                if (index < this.text.Length && this.text[index] == '{')
                {
                    index = this.CodeBlock(index);
                }

                var tail = StatementTail().Match(this.text, index);
                if (!tail.Success)
                {
                    return index;
                }

                index += tail.Length;
            }
        }

        private int CodeBlock(int index)
        {
            var depth = 0;
            var position = index;
            while (position < this.text.Length)
            {
                var character = this.text[position];
                var next = position + 1 < this.text.Length ? this.text[position + 1] : '\0';
                if (character == '@' && next == ':')
                {
                    var end = this.text.IndexOf('\n', position);
                    end = end < 0 ? this.text.Length : end;
                    var line = new RazorTextScanner(this.text[..end]);
                    line.Markup(position + 2, endTag: null);
                    this.found.AddRange(line.found);
                    position = end;
                    continue;
                }

                if (character == '@' && (char.IsLetter(next) || next is '(' or '{'))
                {
                    position = this.Transition(position);
                    continue;
                }

                if (character is '"' or '$' or '@' && this.IsStringStart(position))
                {
                    position = this.SkipString(position);
                    continue;
                }

                if (character == '\'')
                {
                    position = this.SkipChar(position);
                    continue;
                }

                if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return position + 1;
                    }
                }
                else if (character == '<' && this.IsMarkupStart(position))
                {
                    var open = OpenTag().Match(this.text, position);
                    var name = open.Groups["name"].Value;
                    (var afterTag, var selfClosing) = this.Tag(position + open.Length, name);
                    position = selfClosing ? afterTag : this.Markup(afterTag, name);
                    continue;
                }

                position++;
            }

            return position;
        }

        private bool IsMarkupStart(int index)
        {
            if (index + 1 >= this.text.Length || !char.IsLetter(this.text[index + 1]))
            {
                return false;
            }

            var before = index - 1;
            while (before >= 0 && char.IsWhiteSpace(this.text[before]))
            {
                before--;
            }

            if (before < 0 || this.text[before] is '{' or '}' or ';' or '>')
            {
                return true;
            }

            var head = this.text[Math.Max(0, before - 200)..(before + 1)];
            return (this.text[before] == ')' && StatementHeader().IsMatch(head)) || head.EndsWith("else", StringComparison.Ordinal);
        }

        private int SkipWhiteSpace(int index)
        {
            while (index < this.text.Length && char.IsWhiteSpace(this.text[index]))
            {
                index++;
            }

            return index;
        }

        private bool IsStringStart(int index) =>
            StringStart().IsMatch(this.text, index) && (index == 0 || !(char.IsLetterOrDigit(this.text[index - 1]) || this.text[index - 1] == '_'));

        private int SkipString(int index)
        {
            if (RawStringStart().Match(this.text, index) is { Success: true } raw)
            {
                var quotes = raw.Groups["quotes"].Value;
                var close = this.text.IndexOf(quotes, index + raw.Length, StringComparison.Ordinal);
                return close < 0 ? this.text.Length : close + quotes.Length;
            }

            var start = StringStart().Match(this.text, index);
            var prefix = start.Groups["prefix"].Value;
            var verbatim = prefix.Contains('@');
            var interpolated = prefix.Contains('$');
            var position = index + start.Length;
            while (position < this.text.Length)
            {
                var character = this.text[position];
                if (interpolated && character == '{')
                {
                    position = this.text.AsSpan(position).StartsWith("{{") ? position + 2 : this.SkipBalanced(position, '{', '}');
                    continue;
                }

                if (verbatim)
                {
                    if (character == '"')
                    {
                        if (this.text.AsSpan(position).StartsWith("\"\""))
                        {
                            position += 2;
                            continue;
                        }

                        return position + 1;
                    }
                }
                else
                {
                    if (character == '\\')
                    {
                        position += 2;
                        continue;
                    }

                    if (character is '"' or '\n')
                    {
                        return position + 1;
                    }
                }

                position++;
            }

            return position;
        }

        private int SkipChar(int index) =>
            CharLiteral().Match(this.text, index) is { Success: true } literal ? index + literal.Length : index + 1;

        private int SkipBalanced(int index, char open, char close)
        {
            var depth = 0;
            var position = index;
            while (position < this.text.Length)
            {
                var character = this.text[position];
                if (character is '"' or '$' or '@' && this.IsStringStart(position))
                {
                    position = this.SkipString(position);
                    continue;
                }

                if (character == '\'')
                {
                    position = this.SkipChar(position);
                    continue;
                }

                if (character == open)
                {
                    depth++;
                }
                else if (character == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return position + 1;
                    }
                }

                position++;
            }

            return position;
        }

        private int SkipImplicit(int index)
        {
            var name = ImplicitStart().Match(this.text, index);
            if (!name.Success)
            {
                return index;
            }

            var position = index + name.Length;
            while (position < this.text.Length)
            {
                var character = this.text[position];
                var next = position + 1 < this.text.Length ? this.text[position + 1] : '\0';
                if (character == '(')
                {
                    position = this.SkipBalanced(position, '(', ')');
                }
                else if (character == '[')
                {
                    position = this.SkipBalanced(position, '[', ']');
                }
                else if (character == '.' && (char.IsLetter(next) || next == '_'))
                {
                    position += 1 + Identifier().Match(this.text, position + 1).Length;
                }
                else if (character is '!' or '?' && next is '.' or '[')
                {
                    position++;
                }
                else
                {
                    break;
                }
            }

            return position;
        }

        [GeneratedRegex(@"\G</\s*(?<name>[A-Za-z][\w:.-]*)?\s*>", RegexOptions.CultureInvariant)]
        private static partial Regex CloseTag();

        [GeneratedRegex(@"\G<(?<name>[A-Za-z!][\w:.-]*)", RegexOptions.CultureInvariant)]
        private static partial Regex OpenTag();

        [GeneratedRegex(@"\G@?[\w:.-]+", RegexOptions.CultureInvariant)]
        private static partial Regex AttributeName();

        [GeneratedRegex(@"\G\s*=\s*", RegexOptions.CultureInvariant)]
        private static partial Regex AttributeEquals();

        [GeneratedRegex(@"\G[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
        private static partial Regex Identifier();

        [GeneratedRegex(@"\G(?:await\s+)?[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
        private static partial Regex ImplicitStart();

        [GeneratedRegex(@"^\s*\(", RegexOptions.CultureInvariant)]
        private static partial Regex OpenParenthesis();

        [GeneratedRegex(@"\G\s*(?:else\s+if|else|catch|finally|while)\b", RegexOptions.CultureInvariant)]
        private static partial Regex StatementTail();

        [GeneratedRegex(@"(?:if|for|foreach|while|else|using|lock)\s*\(.*\)\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
        private static partial Regex StatementHeader();

        [GeneratedRegex(@"\G(?<prefix>\$*@?\$*)""", RegexOptions.CultureInvariant)]
        private static partial Regex StringStart();

        [GeneratedRegex(@"\G\$*@?\$*(?<quotes>""{3,})", RegexOptions.CultureInvariant)]
        private static partial Regex RawStringStart();

        [GeneratedRegex(@"\G'(?:\\.[^']*|[^'\\])'", RegexOptions.CultureInvariant)]
        private static partial Regex CharLiteral();

        [GeneratedRegex(@"&(?:#\d+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]*);", RegexOptions.CultureInvariant)]
        private static partial Regex Entity();
    }
}
