using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MyCore.Api.Data;
using MyCore.Api.Models;
using System.Net.Http.Json;
using Testcontainers.PostgreSql;

namespace MyCore.IntegrationTests;

public class PetApiTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private HttpClient _client = null!;
    private readonly PostgreSqlContainer _postgreSqlContainer;
    private WebApplicationFactory<Program> _configuredFactory = null!;

    public PetApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _postgreSqlContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb")
            .WithUsername("testuser")
            .WithPassword("testpass")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgreSqlContainer.StartAsync();
        
        _configuredFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove all EF Core related services
                var descriptorsToRemove = services.Where(d => 
                    d.ServiceType == typeof(ApplicationDbContext) ||
                    d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>) ||
                    d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>) ||
                    d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true ||
                    d.ServiceType.FullName?.Contains("DbContext") == true)
                    .ToList();

                foreach (var descriptor in descriptorsToRemove)
                {
                    services.Remove(descriptor);
                }

                // Add a test container PostgreSQL DbContext for testing
                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    options.UseNpgsql(_postgreSqlContainer.GetConnectionString());
                }, ServiceLifetime.Scoped);
            });
        });
        
        _client = _configuredFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        
        using var scope = _configuredFactory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _configuredFactory?.Dispose();
        await _postgreSqlContainer.DisposeAsync();
    }

    [Fact]
    public async Task POST_Pet_Creates_New_Pet()
    {
        // Arrange
        var newPet = new
        {
            Name = "Integration Test Pet"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/pets", newPet);
        var createdPet = await response.Content.ReadFromJsonAsync<Pet>();

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.NotNull(createdPet);
        Assert.Equal(newPet.Name, createdPet.Name);
        Assert.True(createdPet.Id > 0);
    }

    [Fact]
    public async Task GET_Pet_By_Id_Returns_Correct_Pet()
    {
        // Arrange - Create a pet first
        var newPet = new
        {
            Name = "Test Pet for GET"
        };

        var createResponse = await _client.PostAsJsonAsync("/pets", newPet);
        var createdPet = await createResponse.Content.ReadFromJsonAsync<Pet>();

        // Act
        var getResponse = await _client.GetAsync($"/pets/{createdPet!.Id}");
        var retrievedPet = await getResponse.Content.ReadFromJsonAsync<Pet>();

        // Assert
        getResponse.EnsureSuccessStatusCode();
        Assert.NotNull(retrievedPet);
        Assert.Equal(createdPet.Id, retrievedPet!.Id);
        Assert.Equal(newPet.Name, retrievedPet.Name);
    }
}
