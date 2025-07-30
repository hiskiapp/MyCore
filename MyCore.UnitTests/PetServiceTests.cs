using Microsoft.EntityFrameworkCore;
using MyCore.Api.Data;
using MyCore.Api.Models;

namespace MyCore.UnitTests;

public class PetServiceTests
{
    private static ApplicationDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task Should_Add_Pet_Successfully()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var pet = new Pet
        {
            Name = "Test Pet",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        context.Pets.Add(pet);
        await context.SaveChangesAsync();

        // Assert
        var savedPet = await context.Pets.FirstOrDefaultAsync();
        Assert.NotNull(savedPet);
        Assert.Equal("Test Pet", savedPet.Name);
        Assert.True(savedPet.Id > 0);
    }

    [Fact]
    public async Task Should_Get_All_Pets()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        context.Pets.AddRange(
            new Pet { Name = "Pet 1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new Pet { Name = "Pet 2", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        );
        await context.SaveChangesAsync();

        // Act
        var pets = await context.Pets.ToListAsync();

        // Assert
        Assert.Equal(2, pets.Count);
    }
}
