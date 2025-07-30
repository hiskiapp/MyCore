
using MyCore.Api.Models;

namespace MyCore.Api.Data
{
    public static class DataSeeder
    {
        public static void Seed(ApplicationDbContext context)
        {
            if (!context.Pets.Any())
            {
                context.Pets.AddRange(
                    new Pet { Name = "Doggo" },
                    new Pet { Name = "Catto" },
                    new Pet { Name = "Birb" }
                );
                context.SaveChanges();
            }
        }
    }
}
