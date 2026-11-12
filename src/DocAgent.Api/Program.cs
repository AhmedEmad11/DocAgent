var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery, HTTP resilience.
builder.AddServiceDefaults();

// PostgreSQL connection (connection string "docagentdb"). Registers an
// NpgsqlDataSource plus a database health check used by /health/ready.
builder.AddNpgsqlDataSource("docagentdb");

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapDefaultEndpoints();

app.Run();

// Makes the generated Program class visible to WebApplicationFactory in integration tests.
public partial class Program;
