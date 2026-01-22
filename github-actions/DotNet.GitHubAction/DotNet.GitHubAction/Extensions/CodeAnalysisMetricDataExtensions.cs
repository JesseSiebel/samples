using System.Reflection.Metadata;
using System.Text.RegularExpressions;

namespace DotNet.GitHubAction.Extensions;

static class CodeAnalysisMetricDataExtensions
{
    internal static string ToCyclomaticComplexityEmoji(this CodeAnalysisMetricData metric) =>
        metric.CyclomaticComplexity switch
        {
            >= 0 and <= 7 => ":heavy_check_mark:",  // ✔️
            8 or 9 => ":warning:",                  // ⚠️
            10 or 11 => ":radioactive:",            // ☢️
            >= 12 and <= 14 => ":x:",               // ❌
            _ => ":exploding_head:"                 // 🤯
        };

    internal static int CountNamespaces(this CodeAnalysisMetricData metric) =>
        metric.CountKind(SymbolKind.Namespace);

    internal static int CountNamedTypes(this CodeAnalysisMetricData metric) =>
        metric.CountKind(SymbolKind.NamedType);

    static int CountKind(this CodeAnalysisMetricData metric, SymbolKind kind) =>
        metric.Children
            .Flatten(child => child.Children)
            .Count(child => child.Symbol.Kind == kind);

    internal static (int Complexity, string Emoji) FindHighestCyclomaticComplexity(
        this CodeAnalysisMetricData metric) =>
        metric.Children
            .Flatten(child => child.Children)
            .Where(child =>
                child.Symbol.Kind is not SymbolKind.Assembly
                and not SymbolKind.Namespace
                and not SymbolKind.NamedType)
            .Select(m => (Metric: m, m.CyclomaticComplexity))
            .OrderByDescending(_ => _.CyclomaticComplexity)
            .Select(_ => (_.CyclomaticComplexity, _.Metric.ToCyclomaticComplexityEmoji()))
            .FirstOrDefault();

    static IEnumerable<TSource> Flatten<TSource>(
        this IEnumerable<TSource> parent, Func<TSource, IEnumerable<TSource>> childSelector) =>
        parent.SelectMany(
            source => childSelector(source).Flatten(childSelector))
            .Concat(parent);

    internal static string ToMermaidClassDiagram(
        this CodeAnalysisMetricData classMetric, string className)
    {
        // https://mermaid-js.github.io/mermaid/#/classDiagram
        StringBuilder builder = new("classDiagram");
        builder.AppendLine();

        className = className.Contains(".")
            ? className.Substring(className.IndexOf(".") + 1)
            : className;

        if (classMetric.Symbol is ITypeSymbol typeSymbol &&
            typeSymbol.Interfaces.Length > 0)
        {
            foreach (var @interface in typeSymbol.Interfaces)
            {
                if (@interface.IsGenericType)
                {
                    var typeArgs = string.Join(",", @interface.TypeArguments.Select(ta => ta.Name));
                    var name = $"{@interface.Name}~{typeArgs}~";
                    builder.AppendLine($"{name} <|-- {className} : implements");
                }
                else
                {
                    var name = @interface.Name;
                    builder.AppendLine($"{name} <|-- {className} : implements"); 
                }
            }
        }

        builder.AppendLine($"class {className}{{");

        static string? ToClassifier(CodeAnalysisMetricData member) =>
            (member.Symbol.IsStatic, member.Symbol.IsAbstract) switch
            {
                (true, _) => "$", (_, true) => "*", _ => null
            };

        static string ToAccessModifier(CodeAnalysisMetricData member)
        {
            // TODO: figure out how to get access modifiers.
            // + Public
            // - Private
            // # Protected
            // ~ Package / Internal

            return member.Symbol switch
            {
                IFieldSymbol field => "-",
                IPropertySymbol prop => "+",
                IMethodSymbol method => "+",
                _ => "+"
            };
        }
        
        static string ToMemberName(CodeAnalysisMetricData member, string className)
        {
            var name = member.Symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
                       ?? member.Symbol?.Name
                       ?? string.Empty;
        
            var idx = name.IndexOf(className, StringComparison.Ordinal);
            if (idx < 0)
            {
                return name;
            }
        
            var start = idx + className.Length;
            if (start >= name.Length)
            {
                return name;
            }
        
            var sep = name.IndexOf("::", start, StringComparison.Ordinal);
            if (sep < 0 || sep + 2 > name.Length)
            {
                return name.Substring(start).TrimStart('.');
            }
        
            var tail = sep + 2;
            return tail >= name.Length ? name : name.Substring(tail);
        }

        foreach (var member
            in classMetric.Children.OrderBy(
               m => m.Symbol.Kind switch
               {
                   SymbolKind.Field => 1,
                   SymbolKind.Property => 2,
                   SymbolKind.Method => 3,
                   _ => 4
               }))
        {
            _ = member.Symbol.Kind switch
            {
                SymbolKind.Field => builder.AppendLine(
                    $"    {ToMemberName(member, className)}{ToClassifier(member)}"),

                SymbolKind.Property => builder.AppendLine(
                    $"    {ToMemberName(member, className)}{ToClassifier(member)}"),

                SymbolKind.Method => builder.AppendLine(
                    $"    {ToMemberName(member, className)}"),

                _ => null
            };
        }

        builder.AppendLine("}");

        var mermaidCode = builder.ToString();
            //.Replace("<", "~")
            //.Replace(">", "~");

        return mermaidCode;
    }

    internal static string ToDisplayName(this CodeAnalysisMetricData metric) =>
        metric.Symbol.Kind switch
        {
            SymbolKind.Assembly => metric.Symbol.Name,

            SymbolKind.NamedType => DisplayName(metric.Symbol),

            SymbolKind.Method
            or SymbolKind.Field
            or SymbolKind.Event
            or SymbolKind.Property => metric.Symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),

            _ => metric.Symbol.ToDisplayString()
        };

    static string DisplayName(ISymbol symbol)
    {
        StringBuilder minimalTypeName =
            new(
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        var containingType = symbol.ContainingType;
        while (containingType is not null)
        {
            minimalTypeName.Insert(0, ".");
            minimalTypeName.Insert(0,
                containingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            containingType = containingType.ContainingType;
        }

        return minimalTypeName.ToString();
    }
}
