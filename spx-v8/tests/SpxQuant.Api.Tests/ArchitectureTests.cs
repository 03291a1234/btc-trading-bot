using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace SpxQuant.Api.Tests;

/// <summary>Clean-architecture dependency rules and the "no live orders" invariant, enforced in CI.</summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(SpxQuant.Domain.Risk.RiskLimits).Assembly;
    private static readonly Assembly Application = typeof(SpxQuant.Application.Abstractions.IMarketDataProvider).Assembly;
    private static readonly Assembly Infrastructure = typeof(SpxQuant.Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;

    private static string[] Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!).ToArray();

    [Fact] public void DomainReferencesNoOtherLayerOrFramework()
    {
        var refs = Refs(Domain);
        Assert.DoesNotContain(refs, r => r.StartsWith("SpxQuant.", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || r.StartsWith("Microsoft.Extensions", StringComparison.Ordinal));
    }

    [Fact] public void ApplicationDependsOnlyOnDomain()
    {
        var refs = Refs(Application).Where(r => r.StartsWith("SpxQuant.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["SpxQuant.Domain"], refs);
    }

    [Fact] public void InfrastructureDoesNotDependOnApi() =>
        Assert.DoesNotContain("SpxQuant.Api", Refs(Infrastructure));

    [Fact] public void NoOrderPlacementMembersExistAnywhere()
    {
        var forbidden = new Regex("(Place|Submit|Send|Cancel|Modify|Replace)Order|Brokerage(Order|Account)|OrderTicket", RegexOptions.IgnoreCase);
        var offenders = new[] { Domain, Application, Infrastructure, Api }
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => $"{t.FullName}.{m.Name}").Append(t.FullName ?? ""))
            .Where(n => forbidden.IsMatch(n))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact] public void SourceContainsNoBrokerageEndpointLiterals()
    {
        // String literals naming Tradier trading/account routes are forbidden in production source.
        var literal = new Regex("\"[^\"\\n]*/v1/(accounts|user|watchlists)[^\"\\n]*\"", RegexOptions.IgnoreCase);
        var src = Path.Combine(ApiFactory.Root, "src");
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => literal.IsMatch(x.line))
            .Select(x => $"{x.f}:{x.i + 1}")
            .ToArray();
        Assert.Empty(hits);
    }
}
