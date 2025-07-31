var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgAdmin();

var database = postgres.AddDatabase("mycoredb");

builder.AddProject<Projects.MyCore_Api>("api")
    .WithReference(database)
    .WaitFor(database); // Ensure database is ready before starting API

builder.Build().Run();
