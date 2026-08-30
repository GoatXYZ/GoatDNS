using GoatDNS.Core.Config;
using GoatDNS.Core.Hosts;
using GoatDNS.Core.Rules;
using Xunit;

namespace GoatDNS.Tests;

public class RulesEngineTests
{
    [Theory]
    [InlineData("example.com", "*", true)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "*.example.com", true)]       // apex matches wildcard
    [InlineData("www.example.com", "*.example.com", true)]
    [InlineData("a.b.example.com", "*.example.com", true)]
    [InlineData("notexample.com", "*.example.com", false)]
    [InlineData("example.org", "*.example.com", false)]
    [InlineData("chrome.exe", "chrome*", true)]
    [InlineData("firefox.exe", "chrome*", false)]
    public void WildcardMatch_Works(string value, string pattern, bool expected)
    {
        Assert.Equal(expected, RulesEngine.WildcardMatch(value, pattern));
    }

    [Fact]
    public void FirstMatchWins_AndDefaultCatchesRest()
    {
        var rules = new List<RuleDefinition>
        {
            new() { Name = "block-ads", Hosts = ["*.ads.example"], Action = RuleActionType.Block },
            new() { Name = "Default", Action = RuleActionType.Process, Pool = "p" },
        };
        var engine = new RulesEngine(rules, EmptyHosts(), _ => true);

        Assert.Equal("block-ads", engine.Match("tracker.ads.example", new QueryContext())!.Name);
        Assert.Equal("Default", engine.Match("example.com", new QueryContext())!.Name);
    }

    [Fact]
    public void ProcessCriteria_Filters()
    {
        var rules = new List<RuleDefinition>
        {
            new() { Name = "browser-only", Hosts = ["*"], Processes = ["chrome*", "firefox*"], Action = RuleActionType.Block },
            new() { Name = "Default", Action = RuleActionType.Process, Pool = "p" },
        };
        var engine = new RulesEngine(rules, EmptyHosts(), _ => true);

        // Capture layer reports bare names; rules may be written with or without ".exe".
        Assert.Equal("browser-only", engine.Match("x.com", new QueryContext { ProcessName = "chrome" })!.Name);
        Assert.Equal("Default", engine.Match("x.com", new QueryContext { ProcessName = "curl" })!.Name);
    }

    [Fact]
    public void ProcessCriteria_ExactExePattern_MatchesBareName()
    {
        var rules = new List<RuleDefinition>
        {
            new() { Name = "exact", Hosts = ["*"], Processes = ["firefox.exe"], Action = RuleActionType.Block },
            new() { Name = "Default", Action = RuleActionType.Process, Pool = "p" },
        };
        var engine = new RulesEngine(rules, EmptyHosts(), _ => true);

        Assert.Equal("exact", engine.Match("x.com", new QueryContext { ProcessName = "firefox" })!.Name);
        Assert.Equal("Default", engine.Match("x.com", new QueryContext { ProcessName = "chrome" })!.Name);
    }

    [Fact]
    public void IgnoreWhenInterfaceDown_SkipsRule()
    {
        var rules = new List<RuleDefinition>
        {
            new() { Name = "vpn-only", Hosts = ["*"], InterfaceName = "VPN", IgnoreWhenInterfaceDown = true, Action = RuleActionType.Block },
            new() { Name = "Default", Action = RuleActionType.Process, Pool = "p" },
        };

        var whenUp = new RulesEngine(rules, EmptyHosts(), _ => true);
        Assert.Equal("vpn-only", whenUp.Match("x.com", new QueryContext())!.Name);

        var whenDown = new RulesEngine(rules, EmptyHosts(), _ => false);
        Assert.Equal("Default", whenDown.Match("x.com", new QueryContext())!.Name);
    }

    private static HostsProvider EmptyHosts() => new([]);
}
