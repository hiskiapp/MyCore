using MyCore.Api.Data;
using MyCore.Api.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var defaultConnection = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrEmpty(defaultConnection))
{
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
}
else
{
    builder.AddNpgsqlDbContext<ApplicationDbContext>("mycoredb");
}

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "MyCore API V1");
        c.RoutePrefix = "swagger"; // Set Swagger UI at /swagger
    });
}

app.UseHttpsRedirection();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    context.Database.EnsureCreated();
    
    if (builder.Configuration["CI"] == "true")
    {
        DataSeeder.Seed(context);
    }
}

// Minimal API endpoints for Pet
app.MapGet("/pets", async (ApplicationDbContext db) =>
    await db.Pets.ToListAsync());

app.MapGet("/pets/{id}", async (int id, ApplicationDbContext db) =>
    await db.Pets.FindAsync(id) is Pet pet
        ? Results.Ok(pet)
        : Results.NotFound());

app.MapPost("/pets", async (Pet pet, ApplicationDbContext db) =>
{
    pet.CreatedAt = DateTime.UtcNow;
    pet.UpdatedAt = DateTime.UtcNow;
    db.Pets.Add(pet);
    await db.SaveChangesAsync();
    return Results.Created($"/pets/{pet.Id}", pet);
});

app.MapPut("/pets/{id}", async (int id, Pet inputPet, ApplicationDbContext db) =>
{
    var pet = await db.Pets.FindAsync(id);
    if (pet is null) return Results.NotFound();

    pet.Name = inputPet.Name;
    pet.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapDelete("/pets/{id}", async (int id, ApplicationDbContext db) =>
{
    if (await db.Pets.FindAsync(id) is Pet pet)
    {
        db.Pets.Remove(pet);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
    return Results.NotFound();
});

app.MapControllers();

app.Run();

// Make Program class accessible for testing
public partial class Program { }
