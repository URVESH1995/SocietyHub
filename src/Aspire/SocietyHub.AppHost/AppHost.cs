// SocietyHub — local development topology.
//
// This file is the single source of truth for how the system is composed: which backing
// services exist, which service owns which database, and who is allowed to talk to whom.
// `aspire run` starts every container and project below and wires the connection strings,
// service discovery and OpenTelemetry between them.

var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Backing services
// ---------------------------------------------------------------------------

// One SQL Server instance hosting a database per service. Sharing the instance keeps
// local development cheap; the boundary that matters is that no service ever opens a
// connection to a database it does not own.
var sql = builder.AddSqlServer("sql")
                 .WithDataVolume()
                 .WithLifetime(ContainerLifetime.Persistent);

var identityDb = sql.AddDatabase("identitydb");
var societyDb = sql.AddDatabase("societydb");
var gateDb = sql.AddDatabase("gatedb");
var helpdeskDb = sql.AddDatabase("helpdeskdb");
var notificationDb = sql.AddDatabase("notificationdb");

// Shared cache: society master data, visitor OTP codes, idempotency keys and the
// distributed locks that keep bulk-service quorum counting correct in Phase 2.
var redis = builder.AddRedis("redis")
                   .WithDataVolume()
                   .WithLifetime(ContainerLifetime.Persistent);

// The event backbone. Every cross-service state change travels through here.
var rabbitmq = builder.AddRabbitMQ("rabbitmq")
                      .WithDataVolume()
                      .WithManagementPlugin()
                      .WithLifetime(ContainerLifetime.Persistent);

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

var identityApi = builder.AddProject<Projects.SocietyHub_Identity_Api>("identity-api")
                         .WithReference(identityDb).WaitFor(identityDb)
                         .WithReference(redis).WaitFor(redis)
                         .WithReference(rabbitmq).WaitFor(rabbitmq)
                         .WithHttpHealthCheck("/health");

// The gateway is the only component with a publicly reachable endpoint. Everything
// else is reachable solely through service discovery inside the compose network.
builder.AddProject<Projects.SocietyHub_ApiGateway>("apigateway")
       .WithReference(identityApi).WaitFor(identityApi)
       .WithReference(redis)
       .WithExternalHttpEndpoints()
       .WithHttpHealthCheck("/health");

builder.Build().Run();
