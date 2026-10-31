using Toda.Agents.SampleHost;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

// A host of toda-agents alone (ADR 0091, A6): a help desk's tickets in its own table, worked on by agents with the
// package's own run queue, a scripted model, and the package's defaults for the audit, safe mode and acting.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseAutofac();
await builder.AddApplicationAsync<SampleHostModule>();

WebApplication app = builder.Build();
await app.InitializeApplicationAsync();
await CreateDatabaseAsync(app.Services);
await app.RunAsync();

// The sample makes its tables from the model. A real host adds the agents' tables to its own migrations
// (dotnet ef migrations add), as it does its own, and runs them on start or in its deploy.
static async Task CreateDatabaseAsync(IServiceProvider services)
{
    using IServiceScope scope = services.CreateScope();
    using IUnitOfWork uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin();
    SampleDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextProvider<SampleDbContext>>().GetDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await uow.CompleteAsync();
}

/// <summary>The program, for the tests' WebApplicationFactory.</summary>
public partial class Program;
