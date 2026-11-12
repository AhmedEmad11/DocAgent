var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with the pgvector extension available (SRS D5).
// The pgvector image is the official Postgres image plus the extension;
// the extension itself is enabled by an EF Core migration in Phase 1.
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "pg17")
    .WithDataVolume("docagent-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("docagentdb");

builder.AddProject<Projects.DocAgent_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health/ready");

builder.Build().Run();
