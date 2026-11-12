var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with the pgvector extension available (SRS D5).
// The pgvector image is the official Postgres image plus the extension;
// the extension itself is enabled by an EF Core migration in Phase 1.
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "pg17")
    .WithDataVolume("docagent-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("docagentdb");

// Mailpit: local SMTP catcher with a web UI, so confirmation / reset emails can be read in development.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.33.1")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp");
var mailpitSmtp = mailpit.GetEndpoint("smtp");

builder.AddProject<Projects.DocAgent_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(mailpit)
    .WithEnvironment("Email__Smtp__Host", mailpitSmtp.Property(EndpointProperty.Host))
    .WithEnvironment("Email__Smtp__Port", mailpitSmtp.Property(EndpointProperty.Port))
    .WithEnvironment("Email__Smtp__UseStartTls", "false")
    .WithEnvironment("Email__FromAddress", "noreply@docagent.local")
    .WithHttpHealthCheck("/health/ready");

builder.Build().Run();
