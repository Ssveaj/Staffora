using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

internal static partial class ServiceSources
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<(string Path, string Code)>> projectFiles = new(StringComparer.Ordinal);

    public static IReadOnlyList<Type> AllServices() =>
        [.. typeof(Staffora.Services.Services.UserService).Assembly.GetExportedTypes()
            .Where(type => type.IsClass && !type.IsNested && type.Name.EndsWith("Service", StringComparison.Ordinal))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)];

    public static IReadOnlyList<string> FilesOf(Type type) =>
        [.. DeclaringFiles(type).Select(file => file.Path)];

    public static string Of(Type type)
    {
        var files = DeclaringFiles(type);
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No source file of {ProjectOf(type)} declares {type.FullName}.");
        }

        return string.Join("\n", files.Select(file => file.Code));
    }

    public static IReadOnlyList<string> CompiledFilesOf(Type type)
    {
        var pdb = Path.ChangeExtension(type.Assembly.Location, ".pdb");
        using var stream = File.OpenRead(pdb);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var documents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in TypeAndNested(type).SelectMany(each => each.GetMethods(flags).Cast<MethodBase>().Concat(each.GetConstructors(flags))))
        {
            var information = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(member.MetadataToken & 0x00FFFFFF));
            foreach (var point in information.GetSequencePoints())
            {
                documents.Add(reader.GetString(reader.GetDocument(point.Document).Name));
            }
        }

        var project = ProjectOf(type);
        return [.. documents
            .Select(document => RelativeToProject(document, project))
            .OfType<string>()
            .Where(path => !path.StartsWith(project + "/obj/", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
    }

    public static IReadOnlyDictionary<string, Type> InjectedServices(Type type)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var assembly = typeof(Staffora.Services.Authorization.PermissionService).Assembly;
        var classes = assembly.GetTypes().Where(each => each.IsClass).ToList();
        bool IsService(Type candidate) =>
            (candidate.IsClass && candidate.Assembly == assembly && !typeof(Delegate).IsAssignableFrom(candidate))
            || (candidate.IsInterface && classes.Any(candidate.IsAssignableFrom));

        return type.GetConstructors(flags).SelectMany(constructor => constructor.GetParameters()).Select(parameter => (Name: parameter.Name!, Type: parameter.ParameterType))
            .Concat(type.GetFields(flags).Where(field => !field.Name.Contains('<', StringComparison.Ordinal)).Select(field => (field.Name, Type: field.FieldType)))
            .Where(each => IsService(each.Type))
            .GroupBy(each => each.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Type, StringComparer.Ordinal);
    }

    public static IEnumerable<string> PublicMethodNames(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Distinct();

    public static IReadOnlyList<string> PublicBodiesOf(string code, string method) =>
        [.. Members(code, "public").Where(member => member.Name == method).Select(member => member.Body)];

    public static IReadOnlyList<(string Name, string Body)> Members(string code, string modifier) =>
        [.. Regex.Matches(code, $@"\b{modifier}\s[^;{{}}=]*?\b(?<name>\w+)\s*(?:<[^>]*>)?\s*\(")
            .Where(match => !TypeDeclaration().IsMatch(match.Value))
            .Select(match => (match.Groups["name"].Value, BodyAfter(code, match.Index + match.Length - 1)))];

    public static string BodyAfter(string text, int openParen)
    {
        var index = openParen;
        for (var depth = 0; index < text.Length; index++)
        {
            depth += text[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                break;
            }
        }

        var block = text.IndexOf('{', index);
        var arrow = text.IndexOf("=>", index, StringComparison.Ordinal);
        var semicolon = text.IndexOf(';', index);
        if (semicolon >= 0 && (block < 0 || semicolon < block) && (arrow < 0 || semicolon < arrow))
        {
            return string.Empty;
        }

        if (arrow >= 0 && (block < 0 || arrow < block))
        {
            return text[arrow..(semicolon < 0 ? text.Length : semicolon)];
        }

        if (block < 0)
        {
            return string.Empty;
        }

        var end = block;
        for (var depth = 0; end < text.Length; end++)
        {
            depth += text[end] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                break;
            }
        }

        return text[block..end];
    }

    public static string Code(string source)
    {
        var output = new StringBuilder(source.Length);
        var index = 0;
        CopyCode(source, ref index, output, inHole: false);
        return output.ToString();
    }

    private static string ProjectOf(Type type) => type.Assembly.GetName().Name!;

    private static IReadOnlyList<(string Path, string Code)> DeclaringFiles(Type type)
    {
        var declaration = new Regex($@"\b(?:class|record|struct|interface)\s+{Regex.Escape(type.Name.Split('`')[0])}\b");
        return [.. ProjectFiles(ProjectOf(type))
            .Where(file => declaration.IsMatch(file.Code) && Namespace().Matches(file.Code).Any(match => match.Groups["name"].Value == type.Namespace))];
    }

    private static IReadOnlyList<(string Path, string Code)> ProjectFiles(string project) =>
        projectFiles.GetOrAdd(project, name =>
        [
            .. Directory.EnumerateFiles(Path.Combine(RepoFiles.Root, name), "*.cs", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/'))
                .Where(path => !path.Split('/').Any(segment => segment is "bin" or "obj"))
                .Order(StringComparer.Ordinal)
                .Select(path => (path, Code(File.ReadAllText(Path.Combine(RepoFiles.Root, path))))),
        ]);

    private static IEnumerable<Type> TypeAndNested(Type type) =>
        new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(TypeAndNested));

    private static string? RelativeToProject(string document, string project)
    {
        var segments = document.Replace('\\', '/').Split('/');
        var start = Array.LastIndexOf(segments, project);
        return start < 0 ? null : string.Join('/', segments[start..]);
    }

    private static void CopyCode(string source, ref int index, StringBuilder output, bool inHole)
    {
        var depth = 0;
        while (index < source.Length)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (current == '/' && next == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && next == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                var stop = end < 0 ? source.Length : end + 2;
                output.Append(' ').Append('\n', source[index..stop].Count(character => character == '\n'));
                index = stop;
                continue;
            }

            if (inHole && current == '{')
            {
                depth++;
            }
            else if (inHole && current == '}')
            {
                if (depth == 0)
                {
                    return;
                }

                depth--;
            }

            if (current == '\'')
            {
                output.Append('\'');
                index++;
                while (index < source.Length && source[index] != '\'' && source[index] != '\n')
                {
                    var escaped = source[index] == '\\' && index + 1 < source.Length;
                    output.Append(' ', escaped ? 2 : 1);
                    index += escaped ? 2 : 1;
                }

                if (index < source.Length && source[index] == '\'')
                {
                    output.Append('\'');
                    index++;
                }

                continue;
            }

            var prefix = index;
            while (prefix < source.Length && source[prefix] is '$' or '@')
            {
                prefix++;
            }

            if (prefix < source.Length && source[prefix] == '"' && (prefix == index || index == 0 || !IsIdentifierCharacter(source[index - 1])))
            {
                var dollars = source[index..prefix].Count(character => character == '$');
                var verbatim = source[index..prefix].Contains('@');
                output.Append(source, index, prefix - index);
                index = prefix;
                CopyLiteral(source, ref index, output, dollars, verbatim);
                continue;
            }

            output.Append(current);
            index++;
        }
    }

    private static void CopyLiteral(string source, ref int index, StringBuilder output, int dollars, bool verbatim)
    {
        var quotes = 0;
        while (index + quotes < source.Length && source[index + quotes] == '"')
        {
            quotes++;
        }

        if (quotes == 2)
        {
            output.Append("\"\"");
            index += 2;
            return;
        }

        var raw = quotes >= 3;
        var delimiter = raw ? new string('"', quotes) : "\"";
        output.Append(delimiter);
        index += delimiter.Length;
        while (index < source.Length)
        {
            var current = source[index];
            if (raw && source.AsSpan(index).StartsWith(delimiter, StringComparison.Ordinal))
            {
                output.Append(delimiter);
                index += delimiter.Length;
                return;
            }

            if (!raw && current == '"')
            {
                if (verbatim && index + 1 < source.Length && source[index + 1] == '"')
                {
                    output.Append("  ");
                    index += 2;
                    continue;
                }

                output.Append('"');
                index++;
                return;
            }

            if (!raw && !verbatim && current == '\\')
            {
                output.Append("  ");
                index += 2;
                continue;
            }

            if (dollars > 0 && current == '{')
            {
                var run = 0;
                while (index + run < source.Length && source[index + run] == '{')
                {
                    run++;
                }

                var opening = raw ? dollars : 1;
                if (!raw && run >= 2)
                {
                    output.Append(' ', 2);
                    index += 2;
                    continue;
                }

                if (run >= opening)
                {
                    output.Append(' ', run - opening).Append('{', opening);
                    index += run;
                    CopyCode(source, ref index, output, inHole: true);
                    var closing = 0;
                    while (closing < opening && index < source.Length && source[index] == '}')
                    {
                        output.Append('}');
                        index++;
                        closing++;
                    }

                    continue;
                }
            }

            output.Append(current == '\n' ? '\n' : ' ');
            index++;
        }
    }

    private static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    [GeneratedRegex(@"\b(?:class|record|struct|interface)\b")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"\bnamespace\s+(?<name>[\w.]+)")]
    private static partial Regex Namespace();
}
