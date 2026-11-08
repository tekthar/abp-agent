using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Toda.Agents.Host.Tests;

/// <summary>
/// Agents hosted alone (ADR 0091, A6), as another product hosts them: the sample help desk, its tickets in its own table,
/// on a database of its own, with the package's run queue and a scripted model. A run reads a ticket, sets its priority
/// under the agent's name in the run's change set, and answers with a note; a customer's ticket or a look at another
/// desk makes its writes ask; the switch, roles and pausing hold; a write a stop left unknown isn't repeated. Needs
/// Postgres: localhost:5432 (postgres/postgres), or the server TODA_AGENTS_TEST_POSTGRES names.
/// </summary>
public sealed class AgentsAloneTests : IAsyncLifetime
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("TODA_AGENTS_TEST_POSTGRES") is { Length: > 0 } server ? server : "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    // The sample's settings (appsettings.json): an admin, Ben and Cy at the desk DESK (Ben at OPS too), and Dee, a customer.
    private const string Admin = "00000000-0000-0000-0000-0000000000a1";
    private const string Ben = "00000000-0000-0000-0000-0000000000b1";
    private const string Cy = "00000000-0000-0000-0000-0000000000c1";

    private readonly string _database = "agents_host_" + Guid.NewGuid().ToString("N")[..12];
    private WebApplicationFactory<Program>? _root;
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        // The host reads its settings as it builds, so they're the process's environment for this test.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"{Server};Database={_database}");
        _root = _factory = new WebApplicationFactory<Program>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_root is not null)
        {
            await _root.DisposeAsync(); // and the host a test gave other limits
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection connection = new($"{Server};Database=postgres");
        await connection.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_agent_works_a_ticket_under_its_own_name_with_the_askers_rights()
    {
        string agent = await SetUpAgentAsync("triage", """
            demo: get_ticket {"key":"$ticket"}
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo-answer: I set it to high. ![chart](https://x.example/c.png)
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Printer on fire");

        JsonElement run = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Triage it" }));
        Assert.Equal("done", run.GetProperty("state").GetString());
        // The run keeps its answer cleaned too, as it's drawn for its readers: an image from elsewhere is its text and address.
        Assert.Equal("I set it to high. chart (https://x.example/c.png)", run.GetProperty("answer").GetString());
        Assert.True(Tools(run) is ["get_ticket:done", "set_priority:done"], run.ToString());
        Assert.True(run.GetProperty("transcript").GetBoolean());

        JsonElement after = await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben);
        Assert.Equal("high", after.GetProperty("priority").GetString());
        JsonElement change = after.GetProperty("changes")[0];
        string account = run.GetProperty("agent").GetProperty("id").GetString()!;
        Assert.Equal(Ben, change.GetProperty("actorId").GetString());
        Assert.Equal(account, change.GetProperty("actingAccountId").GetString());
        Assert.Equal(run.GetProperty("changeSetId").GetString(), change.GetProperty("changeSetId").GetString());

        // The answer is a note by Ben, run by the agent, cleaned: the image is text and its address.
        JsonElement note = after.GetProperty("notes")[0];
        Assert.Equal(account, note.GetProperty("actingAccountId").GetString());
        Assert.False(note.GetProperty("internal").GetBoolean());
        Assert.Equal("I set it to high. chart (https://x.example/c.png)", note.GetProperty("body").GetString());

        // Cy reads the ticket, but didn't start the run: Cy sees it, not its steps or transcript.
        JsonElement seen = await SendAsync(HttpMethod.Get, $"/api/agent-runs/{run.GetProperty("id").GetString()}", Cy);
        Assert.False(seen.GetProperty("transcript").GetBoolean());
        Assert.Equal(0, seen.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task A_customers_ticket_makes_the_runs_writes_ask_and_its_answer_internal()
    {
        string agent = await SetUpAgentAsync("careful", """
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo-answer: It needs a person.
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Please help", fromCustomer: true);

        JsonElement waiting = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Look" }));
        Assert.True(waiting.GetProperty("outside").GetBoolean());
        JsonElement question = waiting.GetProperty("question");
        Assert.Equal("set_priority", question.GetProperty("tool").GetString());
        Assert.False(question.GetProperty("canAlways").GetBoolean());
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{question.GetProperty("id").GetString()}/answer", Ben, new { answer = "refuse" });
        JsonElement run = await FinishedAsync(waiting);
        Assert.Equal(["set_priority:refused"], Tools(run));
        JsonElement after = await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben);
        Assert.Equal("normal", after.GetProperty("priority").GetString());
        Assert.True(after.GetProperty("notes")[0].GetProperty("internal").GetBoolean());
    }

    [Fact]
    public async Task A_first_change_beyond_the_runs_desk_asks_before_it_runs_whatever_the_settings_say()
    {
        // The agent's settings let set_priority run, and the run has read nothing yet: the change names another desk's
        // ticket, so it asks first and the run is marked as reaching beyond its desk.
        string elsewhere = await TicketAsync(Ben, "OPS", "Someone else's");
        string agent = await SetUpAgentAsync("reacher", $$"""
            demo: set_priority {"key":"{{elsewhere}}","priority":"high"}
            demo-answer: Done.
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Here");

        JsonElement waiting = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        Assert.Equal("waiting", waiting.GetProperty("state").GetString());
        Assert.True(Tools(waiting) is ["set_priority:asked"], waiting.ToString());
        Assert.True(waiting.GetProperty("wide").GetBoolean());
        Assert.NotEqual("high", (await SendAsync(HttpMethod.Get, $"/api/tickets/{elsewhere}", Ben)).GetProperty("priority").GetString());
    }

    [Theory]
    [InlineData("placeless", """{"priority":"high"}""")]
    [InlineData("keyless", """{"key":"not a ticket","priority":"high"}""")]
    public async Task A_change_that_names_nowhere_it_can_be_placed_asks_before_it_runs(string key, string call)
    {
        // The host's own tool, its settings set to run, called with no ticket and no desk, or with a "key" no kind's shape
        // fits: where it would land can't be told, so it counts as beyond the run's desk (fail closed) and asks.
        string agent = await SetUpAgentAsync(key, $"""
            demo: set_priority {call}
            demo-answer: Done.
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Here");

        JsonElement waiting = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        Assert.Equal("waiting", waiting.GetProperty("state").GetString());
        Assert.True(Tools(waiting) is ["set_priority:asked"], waiting.ToString());
        Assert.True(waiting.GetProperty("wide").GetBoolean());
    }

    [Fact]
    public async Task Reading_another_desk_makes_the_run_wide_and_its_answer_stays_in_the_run()
    {
        string agent = await SetUpAgentAsync("roamer", """
            demo: list_desks {}
            demo: set_priority {"key":"$ticket","priority":"low"}
            demo-answer: I looked elsewhere.
            """);
        await TicketAsync(Ben, "OPS", "Elsewhere");
        string ticket = await TicketAsync(Ben, "DESK", "Here");

        JsonElement waiting = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Look around" }));
        Assert.True(waiting.GetProperty("wide").GetBoolean());
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{waiting.GetProperty("question").GetProperty("id").GetString()}/answer", Ben, new { answer = "refuse" });
        JsonElement run = await FinishedAsync(waiting);
        Assert.Equal(["list_desks:done", "set_priority:refused"], Tools(run));
        Assert.Equal("I looked elsewhere.", run.GetProperty("answer").GetString());
        Assert.Equal(0, (await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben)).GetProperty("notes").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, (await SendAsync(HttpMethod.Get, $"/api/agent-runs/{run.GetProperty("id").GetString()}", Cy)).GetProperty("answer").ValueKind);
    }

    [Fact]
    public async Task The_switch_roles_settings_and_pausing_hold()
    {
        HttpResponseMessage early = await RawAsync(HttpMethod.Post, "/api/agents", Admin, new { key = "early" });
        Assert.False(early.IsSuccessStatusCode);
        Assert.Contains("Agents are off", await early.Content.ReadAsStringAsync());

        string agent = await SetUpAgentAsync("asker", """
            demo: set_priority {"key":"$ticket","priority":"high"}
            """, tools: new { set_priority = "ask" });
        HttpResponseMessage admin = await RawAsync(HttpMethod.Put, $"/api/desks/OPS/agents/{agent}/take", Admin, new { roleKey = "admin" });
        Assert.Contains("isn't a role an agent can have", await admin.Content.ReadAsStringAsync());

        string ticket = await TicketAsync(Ben, "DESK", "Ask first");
        JsonElement waiting = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        string question = waiting.GetProperty("question").GetProperty("id").GetString()!;
        JsonElement mine = await SendAsync(HttpMethod.Get, "/api/agent-questions", Ben);
        Assert.Contains(mine.EnumerateArray(), q => q.GetProperty("id").GetString() == question);
        Assert.False((await RawAsync(HttpMethod.Post, $"/api/agent-questions/{question}/answer", Cy, new { answer = "allow" })).IsSuccessStatusCode);
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{question}/answer", Ben, new { answer = "allow" });
        JsonElement run = await FinishedAsync(waiting);
        Assert.True(Tools(run) is ["set_priority:done"], run.ToString());
        Assert.Equal("high", (await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben)).GetProperty("priority").GetString());

        string elsewhere = await TicketAsync(Ben, "OPS", "Not taken on here");
        HttpResponseMessage notTaken = await RawAsync(HttpMethod.Post, $"/api/tickets/{elsewhere}/agent-runs", Ben, new { agentId = agent, prompt = "Go" });
        Assert.Contains("OPS hasn't taken asker on", await notTaken.Content.ReadAsStringAsync());

        await SendAsync(HttpMethod.Post, $"/api/agents/{agent}/pause", Admin);
        HttpResponseMessage paused = await RawAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Go" });
        Assert.Contains("asker is paused", await paused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Renaming_an_agent_renames_its_account_and_other_changes_leave_it()
    {
        string agent = await SetUpAgentAsync("namer", "demo-answer: Done.");
        JsonElement unchanged = await SendAsync(HttpMethod.Put, $"/api/agents/{agent}", Admin, new { instructions = "demo-answer: Still done." });
        Assert.Equal("namer", unchanged.GetProperty("account").GetProperty("displayName").GetString());

        JsonElement renamed = await SendAsync(HttpMethod.Put, $"/api/agents/{agent}", Admin, new { names = new Dictionary<string, string> { ["en"] = "Desk helper", ["ar"] = "مساعد المكتب" } });
        Assert.Equal("Desk helper", renamed.GetProperty("account").GetProperty("displayName").GetString());

        JsonElement other = await SendAsync(HttpMethod.Put, $"/api/agents/{agent}", Admin, new { instructions = "demo-answer: Done again." });
        Assert.Equal("Desk helper", other.GetProperty("account").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_write_a_stop_left_unknown_is_named_to_the_agent_not_repeated()
    {
        string agent = await SetUpAgentAsync("resumer", "demo-answer: I checked first.");
        string ticket = await TicketAsync(Ben, "DESK", "Half done");

        // A run whose server stopped while it set a priority: its step is still "started". Leased far ahead, so the
        // worker leaves it to this test.
        Guid runId = Guid.NewGuid();
        using (IServiceScope scope = _factory!.Services.CreateScope())
        {
            AgentsDb agentsDb = scope.ServiceProvider.GetRequiredService<AgentsDb>();
            AgentItemRef item = (await scope.ServiceProvider.GetServices<IAgentItemKind>().First(k => k.Kind == "ticket").FindAsync(ticket, default))!;
            await agentsDb.RunAsync(async db =>
            {
                AgentRun run = new(runId, Guid.Parse(agent), item.ScopeId, item, AgentTriggers.Ask, Guid.Parse(Ben), Guid.Parse(Ben), "Set it")
                {
                    State = AgentRunStates.Running, StartedAt = DateTime.UtcNow, LeasedUntil = DateTime.UtcNow.AddHours(1), Turns = 1,
                    Conversation = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = $"<request>\nSet it\n</request>\n\n<ticket key=\"{ticket}\">\n</ticket>" })
                        .ToJsonString(),
                };
                db.AgentRuns.Add(run);
                db.AgentRunSteps.Add(new AgentRunStep(Guid.NewGuid(), runId, 1, "tool", "set_priority", "{}") { Writes = true });
                await db.SaveChangesAsync();
            }, requiresNew: true);

            AgentTurn turn = await scope.ServiceProvider.GetRequiredService<AgentRunner>().TurnAsync(runId, default);
            Assert.True(turn.Over);
            (AgentRun after, List<AgentRunStep> steps) = await agentsDb.RunAsync(async db =>
                (await db.AgentRuns.AsNoTracking().FirstAsync(r => r.Id == runId), await db.AgentRunSteps.AsNoTracking().Where(s => s.RunId == runId).OrderBy(s => s.Number).ToListAsync()));
            Assert.Equal(AgentStepOutcomes.Unknown, steps[0].Outcome);
            Assert.Contains("whether that happened isn't known. Check before doing it again.", after.Conversation);
            Assert.Equal("I checked first.", after.Answer);
        }
    }

    [Fact]
    public async Task The_system_starts_a_run_with_the_agents_own_role_once_per_cause_and_runs_wait_their_turn()
    {
        string agent = await SetUpAgentAsync("worker", """
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo-answer: Done by the system.
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Nightly");
        using IServiceScope scope = _factory!.Services.CreateScope();
        AgentRunService runs = scope.ServiceProvider.GetRequiredService<AgentRunService>();
        AgentItemRef item = (await scope.ServiceProvider.GetServices<IAgentItemKind>().First(k => k.Kind == "ticket").FindAsync(ticket, default))!;

        // No one started it: it works with the role DESK gave the agent, and the same cause starts it once.
        AgentStartResult started = await runs.StartAsync(new AgentStart(Guid.Parse(agent), "ticket", item.Id, AgentTriggers.Schedule, null, "Nightly look", "night:1"), default);
        AgentStartResult again = await runs.StartAsync(new AgentStart(Guid.Parse(agent), "ticket", item.Id, AgentTriggers.Schedule, null, "Nightly look", "night:1"), default);
        Assert.NotNull(started.RunId);
        Assert.Null(again.RunId);
        Assert.Contains("has a run for that already", again.Why);
        await FinishedAsync(await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin));
        JsonElement run = await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin);
        Assert.Equal("done", run.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("startedBy").ValueKind);
        Assert.True(Tools(run) is ["set_priority:done"], run.ToString());
        JsonElement change = (await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben)).GetProperty("changes")[0];
        Assert.Equal(run.GetProperty("agent").GetProperty("id").GetString(), change.GetProperty("actorId").GetString());

        // A person who can't read the ticket starts nothing.
        AgentStartResult customer = await runs.StartAsync(new AgentStart(Guid.Parse(agent), "ticket", item.Id, AgentTriggers.Mentioned,
            Guid.Parse("00000000-0000-0000-0000-0000000000d1"), "Look"), default);
        Assert.Null(customer.RunId);

        // Two asks on one ticket: the second begins once the first has ended.
        JsonElement first = await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "One" });
        JsonElement second = await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Two" });
        JsonElement one = await FinishedAsync(first), two = await FinishedAsync(second);
        Assert.True(two.GetProperty("startedAt").GetDateTime() >= one.GetProperty("endedAt").GetDateTime(), $"{one} {two}");
    }

    [Fact]
    public async Task A_question_answered_after_the_runs_working_time_lets_it_go_on_since_waiting_isnt_work()
    {
        // Longer than the package's worker takes to pick the run up again (it looks every 5 seconds when idle).
        Limits(o => o.MaxTime = TimeSpan.FromSeconds(8));
        string agent = await SetUpAgentAsync("patient", """
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo-answer: Set once you said so.
            """, tools: new { set_priority = "ask" });
        string ticket = await TicketAsync(Ben, "DESK", "Wait for me");

        JsonElement waiting = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        await Task.Delay(TimeSpan.FromSeconds(9)); // longer than the run may work
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{waiting.GetProperty("question").GetProperty("id").GetString()}/answer", Ben, new { answer = "allow" });
        JsonElement run = await FinishedAsync(waiting);
        Assert.True(run.GetProperty("state").GetString() == "done", run.ToString());
        Assert.True(Tools(run) is ["set_priority:done"], run.ToString());
        Assert.Equal("high", (await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben)).GetProperty("priority").GetString());
    }

    [Fact]
    public async Task A_question_no_one_answers_is_refused_in_time_and_the_run_goes_on()
    {
        Limits(o => (o.MaxTime, o.QuestionTimeout) = (TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(1)));
        string agent = await SetUpAgentAsync("unheard", """
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo-answer: No one said, so I left it.
            """, tools: new { set_priority = "ask" });
        string ticket = await TicketAsync(Ben, "DESK", "Nobody home");

        JsonElement waiting = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        await Task.Delay(TimeSpan.FromSeconds(9)); // past its time, and longer than the run may work
        using (IServiceScope scope = _factory!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AgentRunService>().ExpireQuestionsAsync(default); // what the server's sweep does
        }

        JsonElement run = await FinishedAsync(waiting);
        Assert.Equal("done", run.GetProperty("state").GetString());
        Assert.True(Tools(run) is ["set_priority:refused"], run.ToString());
        Assert.Contains("No one answered in time", run.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("kind").GetString() == "tool")
            .GetProperty("output").GetString());
        Assert.Equal("normal", (await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben)).GetProperty("priority").GetString());
    }

    [Fact]
    public async Task A_run_no_one_started_can_lend_a_persons_rights_and_asks_the_agents_responsible_person()
    {
        string agent = await SetUpAgentAsync("lent", """
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo-answer: Done with Ben's rights.
            """, tools: new { set_priority = "ask" });
        string ticket = await TicketAsync(Ben, "DESK", "A rule's work");
        using IServiceScope scope = _factory!.Services.CreateScope();
        AgentRunService runs = scope.ServiceProvider.GetRequiredService<AgentRunService>();
        AgentItemRef item = (await scope.ServiceProvider.GetServices<IAgentItemKind>().First(k => k.Kind == "ticket").FindAsync(ticket, default))!;

        // A rule's run: Ben's rights, but no one is there to answer, so the agent's responsible person (the admin who made it) is asked.
        AgentStartResult started = await runs.StartAsync(new AgentStart(Guid.Parse(agent), "ticket", item.Id, AgentTriggers.Rule, null, "Set it", "rule:1")
        {
            ActorId = Guid.Parse(Ben),
        }, default);
        JsonElement waiting = await WaitingAsync(await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin));
        JsonElement asked = await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin);
        Assert.Equal(JsonValueKind.Null, asked.GetProperty("startedBy").ValueKind);
        JsonElement question = asked.GetProperty("question");
        Assert.Equal(Admin, question.GetProperty("askedOf").GetProperty("id").GetString());
        Assert.True(question.GetProperty("canAlways").GetBoolean()); // the admin may change the agent's settings: Always would set the tool to run
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{question.GetProperty("id").GetString()}/answer", Admin, new { answer = "allow" });
        await FinishedAsync(waiting);
        JsonElement change = (await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben)).GetProperty("changes")[0];
        Assert.Equal(Ben, change.GetProperty("actorId").GetString());

        // Lent by someone who can't read the ticket, it doesn't start.
        AgentStartResult customer = await runs.StartAsync(new AgentStart(Guid.Parse(agent), "ticket", item.Id, AgentTriggers.Rule, null, "Look", "rule:2")
        {
            ActorId = Guid.Parse("00000000-0000-0000-0000-0000000000d1"),
        }, default);
        Assert.Null(customer.RunId);
    }

    [Fact]
    public async Task An_outside_read_with_the_persons_own_account_runs_and_an_outside_change_asks_every_time_and_starts_off()
    {
        // Off until it's turned on (OptIn): offered to the model only once the settings name it (ADR 0096).
        string off = await SetUpAgentAsync("poster", """
            demo: post_design {"key":"$ticket"}
            demo-answer: Posted.
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Design it");
        JsonElement run = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = off, prompt = "Go" }));
        Assert.Equal("refused", ToolSteps(run)[0].GetProperty("outcome").GetString());

        // A read elsewhere with Ben's own account runs, and marks the run outside but not beyond its desk; then the change asks,
        // though its setting says run, never offers "Always", and a second call asks again.
        string agent = await SetUpAgentAsync("designer", """
            demo: design_notes {}
            demo: post_design {"key":"$ticket"}
            demo: post_design {"key":"$ticket"}
            demo-answer: Posted twice.
            """, tools: new { post_design = "run" });
        run = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        Assert.Equal(("design_notes", "done"), (ToolSteps(run)[0].GetProperty("name").GetString(), ToolSteps(run)[0].GetProperty("outcome").GetString()));
        Assert.Contains(Ben, ToolSteps(run)[0].GetProperty("output").GetString());
        Assert.True(run.GetProperty("outside").GetBoolean());
        Assert.False(run.GetProperty("wide").GetBoolean());
        JsonElement question = run.GetProperty("question");
        Assert.Equal("post_design", question.GetProperty("tool").GetString());
        Assert.Contains("asks every time", question.GetProperty("why").GetString());
        Assert.False(question.GetProperty("canAlways").GetBoolean());
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{question.GetProperty("id").GetString()}/answer", Ben, new { answer = "allow-run" });
        run = await WaitingAsync(run);
        Assert.Equal("post_design", run.GetProperty("question").GetProperty("tool").GetString());
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{run.GetProperty("question").GetProperty("id").GetString()}/answer", Ben, new { answer = "allow" });
        run = await FinishedAsync(run);
        Assert.Equal(["design_notes", "post_design", "post_design"], ToolSteps(run).Select(s => s.GetProperty("name").GetString()));
        Assert.All(ToolSteps(run), s => Assert.Equal("done", s.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task Always_allow_is_only_for_the_hosts_own_tools_and_a_standing_permission_is_listed_used_and_taken_back()
    {
        string agent = await SetUpAgentAsync("mailer", """
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo: email_customer {"key":"$ticket"}
            demo-answer: Set and mailed.
            """, tools: new { set_priority = "ask", email_customer = "ask" });
        string first = await TicketAsync(Ben, "DESK", "Mail them");

        // The host's own tool may be always allowed; a plugin's, which may send, may not: "always" counts for the run only.
        JsonElement run = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{first}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        Assert.Equal("set_priority", run.GetProperty("question").GetProperty("tool").GetString());
        Assert.True(run.GetProperty("question").GetProperty("canAlways").GetBoolean());
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{run.GetProperty("question").GetProperty("id").GetString()}/answer", Ben, new { answer = "always" });
        run = await WaitingAsync(run);
        Assert.Equal("email_customer", run.GetProperty("question").GetProperty("tool").GetString());
        Assert.False(run.GetProperty("question").GetProperty("canAlways").GetBoolean());
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{run.GetProperty("question").GetProperty("id").GetString()}/answer", Ben, new { answer = "always" });
        run = await FinishedAsync(run);
        Assert.Equal(["answer:always", "answer:allow-run"], ToolSteps(run).Select(s => s.GetProperty("allowed").GetString()));

        JsonElement mine = await SendAsync(HttpMethod.Get, "/api/me/agent-permissions", Ben);
        Assert.Equal(["set_priority"], mine.EnumerateArray().Select(p => p.GetProperty("tool").GetString()));
        Assert.Equal(1, (await SendAsync(HttpMethod.Get, $"/api/agents/{agent}/permissions", Admin)).GetArrayLength());
        Assert.False((await RawAsync(HttpMethod.Get, $"/api/agents/{agent}/permissions", Cy)).IsSuccessStatusCode);

        // Next time, Ben's standing permission lets set_priority run, and the step says so; the plugin's tool asks again.
        string second = await TicketAsync(Ben, "DESK", "Mail them too");
        run = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{second}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        Assert.Equal("email_customer", run.GetProperty("question").GetProperty("tool").GetString());
        Assert.Equal($"always:{Ben}", ToolSteps(run)[0].GetProperty("allowed").GetString());
        Assert.NotEqual(JsonValueKind.Null, (await SendAsync(HttpMethod.Get, "/api/me/agent-permissions", Ben))[0].GetProperty("lastUsedAt").ValueKind);
        await SendAsync(HttpMethod.Post, $"/api/agent-runs/{run.GetProperty("id").GetString()}/stop", Ben);

        // Taken back, it asks again.
        JsonElement permission = mine[0];
        await SendAsync(HttpMethod.Post, "/api/agent-permissions/remove", Ben, new
        {
            personId = Ben, agentId = agent, tool = "set_priority", scopeId = permission.GetProperty("scopeId").GetString(),
        });
        Assert.Equal(0, (await SendAsync(HttpMethod.Get, "/api/me/agent-permissions", Ben)).GetArrayLength());
        string third = await TicketAsync(Ben, "DESK", "Ask me again");
        run = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{third}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        Assert.Equal("set_priority", run.GetProperty("question").GetProperty("tool").GetString());
        await SendAsync(HttpMethod.Post, $"/api/agent-runs/{run.GetProperty("id").GetString()}/stop", Ben);
    }

    [Fact]
    public async Task Always_allow_in_a_run_the_system_started_is_the_agents_setting_for_whoever_may_change_it()
    {
        string agent = await SetUpAgentAsync("nightly", """
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo-answer: Done.
            """, tools: new { set_priority = "ask" });
        string ticket = await TicketAsync(Ben, "DESK", "Overnight");
        using IServiceScope scope = _factory!.Services.CreateScope();
        AgentItemRef item = (await scope.ServiceProvider.GetServices<IAgentItemKind>().First(k => k.Kind == "ticket").FindAsync(ticket, default))!;
        AgentStartResult started = await scope.ServiceProvider.GetRequiredService<AgentRunService>()
            .StartAsync(new AgentStart(Guid.Parse(agent), "ticket", item.Id, AgentTriggers.Schedule, null, "Nightly", "night:always"), default);

        // The admin who made it is its responsible person and may change its settings: "always" sets the tool to run.
        await WaitingAsync(await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin));
        JsonElement question = (await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin)).GetProperty("question");
        Assert.True(question.GetProperty("canAlways").GetBoolean());
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{question.GetProperty("id").GetString()}/answer", Admin, new { answer = "always" });
        await FinishedAsync(await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin));
        using IServiceScope after = _factory!.Services.CreateScope();
        Agent saved = await after.ServiceProvider.GetRequiredService<AgentsDb>().RunAsync(db => db.Agents.AsNoTracking().FirstAsync(a => a.Id == Guid.Parse(agent)));
        Assert.Equal(AgentToolSettings.Run, saved.Tools["set_priority"]);
        Assert.Equal(0, (await SendAsync(HttpMethod.Get, "/api/me/agent-permissions", Admin)).GetArrayLength());
    }

    [Fact]
    public async Task A_days_caps_stop_an_agents_runs_and_one_outside_senders()
    {
        Limits(o => (o.MaxRunsPerAgentPerDay, o.MaxRunsPerSenderPerDay) = (3, 1));
        string agent = await SetUpAgentAsync("capped", "demo-answer: Seen.");
        string ticket = await TicketAsync(Ben, "DESK", "Many asks");
        using IServiceScope scope = _factory!.Services.CreateScope();
        AgentRunService runs = scope.ServiceProvider.GetRequiredService<AgentRunService>();
        AgentItemRef item = (await scope.ServiceProvider.GetServices<IAgentItemKind>().First(k => k.Kind == "ticket").FindAsync(ticket, default))!;
        AgentStart Start(string cause, Guid? sender = null) =>
            new(Guid.Parse(agent), "ticket", item.Id, AgentTriggers.Mentioned, null, "Look", cause) { ActorId = Guid.Parse(Ben), Sender = sender };

        // One sender from outside the team starts one run a day here; the next is refused.
        Guid sender = Guid.Parse(Cy);
        Assert.NotNull((await runs.StartAsync(Start("a", sender), default)).RunId);
        AgentStartResult second = await runs.StartAsync(Start("b", sender), default);
        Assert.Null(second.RunId);
        Assert.Contains("a sender may start in a day", second.Why);

        // The agent starts three a day in all; the fourth is refused, whoever starts it.
        Assert.NotNull((await runs.StartAsync(Start("c"), default)).RunId);
        Assert.NotNull((await runs.StartAsync(Start("d"), default)).RunId);
        AgentStartResult fourth = await runs.StartAsync(Start("e"), default);
        Assert.Null(fourth.RunId);
        Assert.Contains("runs it may start in a day", fourth.Why);
    }

    private static List<JsonElement> ToolSteps(JsonElement run) =>
        [.. run.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("kind").GetString() == "tool")];

    /// <summary>This test's host, with other agents' limits; set before its first request.</summary>
    private void Limits(Action<AgentsOptions> limits) =>
        _factory = _root!.WithWebHostBuilder(host => host.ConfigureServices(services => services.PostConfigure(limits)));

    /// <summary>Agents on, then a site agent offered to every desk, with these instructions and tools, taken on by DESK.</summary>
    private async Task<string> SetUpAgentAsync(string key, string instructions, object? tools = null)
    {
        await SendAsync(HttpMethod.Put, "/api/agents/settings", Admin, new { enabled = true });
        JsonElement agent = await SendAsync(HttpMethod.Post, "/api/agents", Admin, new
        {
            key, offerAll = true, instructions,
            tools = tools ?? new { set_priority = "run", add_note = "run" },
        });
        string id = agent.GetProperty("id").GetString()!;
        Assert.Equal("agent", agent.GetProperty("account").GetProperty("kind").GetString());
        await SendAsync(HttpMethod.Put, $"/api/desks/DESK/agents/{id}/take", Admin, new { roleKey = "helper" });
        return id;
    }

    private async Task<string> TicketAsync(string who, string desk, string title, bool fromCustomer = false) =>
        (await SendAsync(HttpMethod.Post, "/api/tickets", who, new { desk, title, body = "Details", fromCustomer })).GetProperty("key").GetString()!;

    /// <summary>The run once it waits for an answer (or, if it doesn't ask, has ended).</summary>
    private async Task<JsonElement> WaitingAsync(JsonElement run)
    {
        string id = run.GetProperty("id").GetString()!;
        for (int i = 0; i < 150; i++)
        {
            JsonElement now = await SendAsync(HttpMethod.Get, $"/api/agent-runs/{id}", Ben);
            if (now.GetProperty("state").GetString() is "waiting" or "done" or "failed" or "stopped")
            {
                return now;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"The run {id} didn't ask.");
    }

    /// <summary>The run once the package's worker has worked it to its end.</summary>
    private async Task<JsonElement> FinishedAsync(JsonElement run)
    {
        string id = run.GetProperty("id").GetString()!;
        for (int i = 0; i < 150; i++)
        {
            JsonElement now = await SendAsync(HttpMethod.Get, $"/api/agent-runs/{id}", Ben);
            if (now.GetProperty("state").GetString() is "done" or "failed" or "stopped")
            {
                return now;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"The run {id} didn't finish.");
    }

    private static List<string> Tools(JsonElement run) =>
        [.. run.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("kind").GetString() == "tool")
            .Select(s => $"{s.GetProperty("name").GetString()}:{s.GetProperty("outcome").GetString()}")];

    private async Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, string account, object? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("X-Account", account);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _factory!.CreateClient().SendAsync(request);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string account, object? body = null)
    {
        HttpResponseMessage response = await RawAsync(method, path, account, body);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
