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
var noticeDb = sql.AddDatabase("noticedb");

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

var societyApi = builder.AddProject<Projects.SocietyHub_Society_Api>("society-api")
                        .WithReference(societyDb).WaitFor(societyDb)
                        .WithReference(redis).WaitFor(redis)
                        .WithReference(rabbitmq).WaitFor(rabbitmq)
                        .WithHttpHealthCheck("/health");

var gateApi = builder.AddProject<Projects.SocietyHub_Gate_Api>("gate-api")
                     .WithReference(gateDb).WaitFor(gateDb)
                     .WithReference(redis).WaitFor(redis)
                     .WithReference(rabbitmq).WaitFor(rabbitmq)
                     .WithHttpHealthCheck("/health");

var helpdeskApi = builder.AddProject<Projects.SocietyHub_Helpdesk_Api>("helpdesk-api")
                         .WithReference(helpdeskDb).WaitFor(helpdeskDb)
                         .WithReference(redis).WaitFor(redis)
                         .WithReference(rabbitmq).WaitFor(rabbitmq)
                         .WithHttpHealthCheck("/health");

var notificationApi = builder.AddProject<Projects.SocietyHub_Notification_Api>("notification-api")
                             .WithReference(notificationDb).WaitFor(notificationDb)
                             .WithReference(redis).WaitFor(redis)
                             .WithReference(rabbitmq).WaitFor(rabbitmq)
                             .WithHttpHealthCheck("/health");

var noticeApi = builder.AddProject<Projects.SocietyHub_Notice_Api>("notice-api")
                       .WithReference(noticeDb).WaitFor(noticeDb)
                       .WithReference(redis).WaitFor(redis)
                       .WithReference(rabbitmq).WaitFor(rabbitmq)
                       .WithHttpHealthCheck("/health");

// The gateway is the only component with a publicly reachable endpoint. Everything
// else is reachable solely through service discovery inside the compose network.
// The gateway's port is pinned rather than left to Aspire's dynamic assignment.
//
// Every other service can move freely — they are reached through service discovery, which
// resolves whatever port they landed on. The gateway cannot: it is the address the three
// client apps are configured against, and a Blazor WebAssembly build has no way to discover a
// port that changes on every run. Leaving it dynamic meant the admin console defaulted to its
// own origin, where every API call hit the SPA fallback and returned index.html with HTTP 200.
//
// 5280 rather than the obvious 8080: on Windows, 8080 is routinely held by an HTTP.sys
// reservation owned by the System process, and Kestrel then fails to bind with an error that
// never mentions what took the port.
builder.AddProject<Projects.SocietyHub_ApiGateway>("apigateway")
       .WithEndpoint("http", endpoint => endpoint.Port = 5280)
       .WithReference(identityApi).WaitFor(identityApi)
       .WithReference(societyApi).WaitFor(societyApi)
       .WithReference(gateApi).WaitFor(gateApi)
       .WithReference(helpdeskApi).WaitFor(helpdeskApi)
       .WithReference(notificationApi).WaitFor(notificationApi)
       .WithReference(noticeApi).WaitFor(noticeApi)
       .WithReference(redis)
       .WithExternalHttpEndpoints()
       .WithHttpHealthCheck("/health");

builder.Build().Run();
