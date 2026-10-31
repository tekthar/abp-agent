using System.Reflection;

namespace Toda.Agents.Tests;

public class AgentsTests
{
    [Fact]
    public void A_site_agent_is_offered_to_every_scope_or_the_named_ones_and_a_scopes_own_to_it_alone()
    {
        Guid one = Guid.NewGuid(), two = Guid.NewGuid();
        Agent everywhere = new(Guid.NewGuid(), Guid.NewGuid(), "triage", null, Guid.NewGuid(), Guid.NewGuid()) { OfferAll = true };
        Agent named = new(Guid.NewGuid(), Guid.NewGuid(), "helper", null, Guid.NewGuid(), Guid.NewGuid()) { OfferedTo = [one] };
        Agent own = new(Guid.NewGuid(), Guid.NewGuid(), "release", two, Guid.NewGuid(), Guid.NewGuid()) { OfferAll = true };

        Assert.True(everywhere.IsOfferedTo(one) && everywhere.IsOfferedTo(two));
        Assert.True(named.IsOfferedTo(one) && !named.IsOfferedTo(two));
        Assert.True(own.IsOfferedTo(two) && !own.IsOfferedTo(one));
    }

    [Fact]
    public void What_reads_bring_in_adds_up()
    {
        Assert.Equal(new AgentBrought(true, true), new AgentBrought(true, false).Or(new AgentBrought(false, true)));
        Assert.Equal(AgentBrought.Nothing, AgentBrought.Nothing.Or(AgentBrought.Nothing));
    }

    [Fact]
    public void An_act_is_set_for_the_code_under_it_and_put_back_after()
    {
        AgentActing outer = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReadOnly: false);
        AgentActing inner = outer with { ReadOnly = true };
        RecordingScope host = new();
        Assert.Null(AgentActs.Now);
        using (AgentActs.Begin(outer, host))
        {
            using (AgentActs.Begin(inner, host))
            {
                Assert.Equal(inner, AgentActs.Now);
            }

            Assert.Equal(outer, AgentActs.Now);
        }

        Assert.Null(AgentActs.Now);
        Assert.Equal([outer, inner], host.Begun);
        Assert.Equal(2, host.Ended);
    }

    [Fact]
    public void A_run_that_ended_is_over()
    {
        AgentRun run = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new AgentItemRef("record", Guid.NewGuid(), "DESK-1", Guid.NewGuid()), AgentTriggers.Ask,
            Guid.NewGuid(), Guid.NewGuid(), "Look at it");
        Assert.Equal("record", run.ItemKind);
        Assert.False(run.Ended);
        foreach (string state in new[] { AgentRunStates.Done, AgentRunStates.Failed, AgentRunStates.Stopped })
        {
            run.State = state;
            Assert.True(run.Ended);
        }
    }

    private sealed class RecordingScope : IAgentActingScope
    {
        public List<AgentActing> Begun { get; } = [];

        public int Ended { get; private set; }

        public IDisposable? Begin(AgentActing acting)
        {
            Begun.Add(acting);
            return new End(() => Ended++);
        }

        private sealed class End(Action end) : IDisposable
        {
            public void Dispose() => end();
        }
    }
}

public class ArchitectureTests
{
    private static IEnumerable<string> References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!);

    [Fact]
    public void The_package_needs_only_dotnet_abp_ef_core_npgsql_markdig_and_its_contracts()
    {
        string[] allowed = ["System.", "Microsoft.", "Volo.Abp.", "Npgsql", "Markdig", "netstandard"];
        Assert.All(References(typeof(TodaAgentsModule).Assembly),
            r => Assert.True(r == "Toda.Agents.Contracts" || allowed.Any(a => r.StartsWith(a, StringComparison.Ordinal)), r));
    }

    [Fact]
    public void The_contracts_need_only_dotnet()
    {
        Assert.All(References(typeof(IAgentItemKind).Assembly), r => Assert.True(r.StartsWith("System.", StringComparison.Ordinal) || r == "netstandard", r));
    }
}
