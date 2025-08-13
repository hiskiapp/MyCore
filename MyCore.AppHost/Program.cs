var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin();

var database = postgres.AddDatabase("mycoredb");

builder.AddProject<Projects.MyCore_Api>("api")
    .WithReference(database)
    .WaitFor(database);

builder.AddProject<Projects.MyCore_AI>("ai");

builder.Build().Run();
