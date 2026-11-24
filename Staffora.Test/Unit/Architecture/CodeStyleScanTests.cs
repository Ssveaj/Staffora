using System.IO.Compression;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public class CodeStyleScanTests
{
    private static readonly string[] projects = ["Staffora.Core", "Staffora.Data", "Staffora.Services", "Staffora.Blazor", "Staffora.E2ESetup", "Staffora.Test"];

    private static readonly Lazy<IReadOnlyList<CodeStyle.ProjectScan>> scans = new(() =>
        OwnThreads.Map(projects, 3, project => CodeStyle.Scan(project, RepoFiles.Root, AppContext.BaseDirectory)));

    [Fact]
    public void Every_project_compiles_for_the_scan_without_errors_from_every_source_its_build_used()
    {
        Assert.All(scans.Value, scan =>
        {
            Assert.True(scan.Errors.Count == 0, $"{scan.Project} does not compile for the scan: {string.Join(" | ", scan.Errors.Take(10))}");
            Assert.True(scan.Files > 0, $"{scan.Project}: no source file was read.");
        });
        Assert.Contains(scans.Value, scan => scan.Project == "Staffora.Blazor" && scan.GeneratedFiles > 0);
    }

    [Fact]
    public void No_class_or_struct_has_a_primary_constructor()
    {
        AssertNone(CodeStyle.PrimaryConstructor);
    }

    [Fact]
    public void No_static_field_nor_field_of_a_component_starts_with_an_underscore()
    {
        AssertNone(CodeStyle.UnderscoreField);
    }

    [Fact]
    public void Every_field_of_a_component_is_reached_through_this()
    {
        AssertNone(CodeStyle.UnqualifiedField);
    }

    [Fact]
    public void The_scan_catches_each_rule_on_a_sample_and_passes_the_allowed_forms()
    {
        const string sample = """
            using System;

            public sealed class Holder(int value)
            {
                public int Get() => value;
            }

            public record Point(int X, int Y);

            public record struct Size(int Width, int Height);

            public sealed class Other
            {
                public int count;
            }

            public sealed class Fields
            {
                private static int _shared;
                private static readonly int sharedLimit = 3;
                private readonly int total;
                private int _count;

                public Fields(int total)
                {
                    this.total = total;
                }

                public int Unqualified() => total + _shared + sharedLimit;

                public int Qualified() => this.total + this._count + Fields.sharedLimit;

                public Other Initialized() => new Other { count = 1 };

                public string Named() => nameof(total);

                public Func<int, int> Shadowed() => total => total + 1;
            }
            """;

        var code = CSharpSyntaxTree.ParseText(sample, path: "Sample.cs", cancellationToken: TestContext.Current.CancellationToken);
        var component = CSharpSyntaxTree.ParseText(sample.Replace("using System;", "using System; namespace Component;", StringComparison.Ordinal), path: "Sample.razor", cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Sample", [code, component], CodeStyle.References("Sample"), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var found = CodeStyle.Findings(compilation, location => location.SourceTree?.FilePath)
            .Select(finding => $"{finding.Path} {finding.Rule} {finding.Name} {finding.Line}")
            .Order(StringComparer.Ordinal)
            .ToList();

        string[] expected =
        [
            $"Sample.cs {CodeStyle.PrimaryConstructor} Holder 3",
            $"Sample.cs {CodeStyle.UnderscoreField} Fields._shared 19",
            $"Sample.razor {CodeStyle.PrimaryConstructor} Holder 3",
            $"Sample.razor {CodeStyle.UnderscoreField} Fields._count 22",
            $"Sample.razor {CodeStyle.UnderscoreField} Fields._shared 19",
            $"Sample.razor {CodeStyle.UnqualifiedField} Fields.total 29",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), found);
    }

    private static void AssertNone(string rule)
    {
        var offenders = scans.Value
            .SelectMany(scan => scan.Findings)
            .Where(finding => finding.Rule == rule)
            .Select(finding => $"{finding.Path}:{finding.Line} {finding.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, $"{offenders.Count} finding(s) of the rule '{rule}' (ADR 0005): {string.Join(", ", offenders.Take(50))}");
    }
}

internal static class CodeStyle
{
    public const string PrimaryConstructor = "primary constructor";
    public const string UnderscoreField = "underscore field";
    public const string UnqualifiedField = "field without this.";

    private static readonly Guid embeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    private static readonly CSharpParseOptions parseOptions = new(LanguageVersion.Latest, preprocessorSymbols: ["DEBUG", "TRACE", "NET", "NET10_0", "NET10_0_OR_GREATER", "NETCOREAPP"]);

    public sealed record Finding(string Rule, string Path, int Line, string Name);

    public sealed record ProjectScan(string Project, int Files, int GeneratedFiles, IReadOnlyList<string> Errors, IReadOnlyList<Finding> Findings);

    public static ProjectScan Scan(string project, string root, string binaries)
    {
        var trees = new List<SyntaxTree>();
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generated = 0;
        foreach (var (name, embedded) in PdbDocuments(Path.Combine(binaries, project + ".pdb")))
        {
            if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (embedded is not null)
            {
                trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(embedded, Encoding.UTF8), parseOptions, name));
                generated++;
            }
            else if (InThisTree(root, name) is { } file && File.Exists(file) && read.Add(file))
            {
                trees.Add(Parse(file));
            }
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            if (!InBuildOutput(root, file) && read.Add(file))
            {
                trees.Add(Parse(file));
            }
        }

        var compilation = CSharpCompilation.Create(project, trees, References(project), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));
        var errors = OwnThreads.Map([.. compilation.SyntaxTrees], OwnThreads.Wide, tree => compilation.GetSemanticModel(tree).GetDiagnostics())
            .SelectMany(diagnostics => diagnostics)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
        var findings = Findings(compilation, location => Place(root, location));
        return new ProjectScan(project, trees.Count, generated, errors, findings);
    }

    private static readonly Lazy<IReadOnlyList<(string Name, MetadataReference Reference)>> platformReferences = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (Path.GetFileNameWithoutExtension(path), (MetadataReference)MetadataReference.CreateFromFile(path)))]);

    public static IReadOnlyList<MetadataReference> References(string assemblyName) =>
        [.. platformReferences.Value
            .Where(each => !string.Equals(each.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            .Select(each => each.Reference)];

    public static IReadOnlyList<Finding> Findings(Compilation compilation, Func<Location, string?> place)
    {
        var instanceFields = InstanceFieldNames(compilation);
        return [.. OwnThreads.Map([.. compilation.SyntaxTrees], OwnThreads.Wide, tree => TreeFindings(compilation.GetSemanticModel(tree), instanceFields, place))
            .SelectMany(findings => findings)
            .OrderBy(finding => finding.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Line)
            .ThenBy(finding => finding.Rule, StringComparer.Ordinal)
            .ThenBy(finding => finding.Name, StringComparer.Ordinal)];
    }

    private static List<Finding> TreeFindings(SemanticModel model, HashSet<string> instanceFields, Func<Location, string?> place)
    {
        var findings = new List<Finding>();
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case TypeDeclarationSyntax type when type is ClassDeclarationSyntax or StructDeclarationSyntax && type.ParameterList is not null:
                    Add(PrimaryConstructor, type.Identifier.GetLocation(), type.Identifier.ValueText);
                    break;
                case VariableDeclaratorSyntax variable when variable.Parent?.Parent is FieldDeclarationSyntax && variable.Identifier.ValueText.StartsWith('_')
                    && model.GetDeclaredSymbol(variable) is IFieldSymbol { DeclaredAccessibility: Accessibility.Private } field && (field.IsStatic || InComponent(variable)):
                    Add(UnderscoreField, variable.Identifier.GetLocation(), $"{field.ContainingType.Name}.{field.Name}");
                    break;
                case IdentifierNameSyntax identifier when instanceFields.Contains(identifier.Identifier.ValueText) && InComponent(identifier) && !IsQualifiedOrNamed(identifier)
                    && model.GetSymbolInfo(identifier).Symbol is IFieldSymbol { IsStatic: false, IsConst: false } reached:
                    Add(UnqualifiedField, identifier.GetLocation(), $"{reached.ContainingType.Name}.{reached.Name}");
                    break;
            }
        }

        return findings;

        bool InComponent(SyntaxNode node) =>
            node.GetLocation().GetMappedLineSpan().Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);

        void Add(string rule, Location location, string name)
        {
            if (place(location) is { } path)
            {
                findings.Add(new Finding(rule, path, location.GetMappedLineSpan().StartLinePosition.Line + 1, name));
            }
        }
    }

    private static bool IsQualifiedOrNamed(IdentifierNameSyntax identifier) =>
        identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier
        || identifier.Parent is MemberBindingExpressionSyntax
        || identifier.Parent is QualifiedNameSyntax qualified && qualified.Right == identifier
        || identifier.Parent is NameColonSyntax or NameEqualsSyntax
        || identifier.Parent is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assignment && assignment.Left == identifier
        || identifier.Ancestors().Any(ancestor => ancestor is CrefSyntax)
        || identifier.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

    private static HashSet<string> InstanceFieldNames(Compilation compilation)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in TypesOf(compilation.Assembly.GlobalNamespace))
        {
            for (var each = type; each is not null; each = each.BaseType)
            {
                foreach (var field in each.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic && !field.IsConst && !field.IsImplicitlyDeclared))
                {
                    names.Add(field.Name);
                }
            }
        }

        return names;
    }

    private static IEnumerable<INamedTypeSymbol> TypesOf(INamespaceSymbol space) =>
        space.GetTypeMembers().SelectMany(WithNested).Concat(space.GetNamespaceMembers().SelectMany(TypesOf));

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type) =>
        type.GetTypeMembers().SelectMany(WithNested).Prepend(type);

    private static string? Place(string root, Location location)
    {
        if (location.SourceTree is not { } tree || tree.GetLineVisibility(location.SourceSpan.Start) == LineVisibility.Hidden)
        {
            return null;
        }

        var mapped = location.GetMappedLineSpan().Path;
        if (string.IsNullOrEmpty(mapped) || InThisTree(root, mapped) is not { } file || InBuildOutput(root, file))
        {
            return null;
        }

        return Path.GetRelativePath(root, file).Replace('\\', '/');
    }

    private static string? InThisTree(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        var segments = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var project = Array.FindLastIndex(segments, segment => segment.StartsWith("Staffora.", StringComparison.Ordinal) && Directory.Exists(Path.Combine(root, segment)));
        return project < 0 ? null : Path.Combine([root, .. segments[project..]]);
    }

    private static bool InBuildOutput(string root, string file) =>
        Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "bin" or "obj");

    private static SyntaxTree Parse(string file) =>
        CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(file), Encoding.UTF8), parseOptions, file);

    private static IReadOnlyList<(string Name, string? Embedded)> PdbDocuments(string pdb)
    {
        using var stream = File.OpenRead(pdb);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var documents = new List<(string, string?)>();
        foreach (var handle in reader.Documents)
        {
            string? text = null;
            foreach (var information in reader.GetCustomDebugInformation(handle).Select(each => reader.GetCustomDebugInformation(each)))
            {
                if (reader.GetGuid(information.Kind) == embeddedSource)
                {
                    text = EmbeddedText(reader.GetBlobBytes(information.Value));
                }
            }

            documents.Add((reader.GetString(reader.GetDocument(handle).Name), text));
        }

        return documents;
    }

    private static string EmbeddedText(byte[] blob)
    {
        if (BitConverter.ToInt32(blob, 0) == 0)
        {
            return Encoding.UTF8.GetString(blob, 4, blob.Length - 4);
        }

        using var compressed = new MemoryStream(blob, 4, blob.Length - 4);
        using var inflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var text = new MemoryStream();
        inflate.CopyTo(text);
        return Encoding.UTF8.GetString(text.ToArray());
    }
}
