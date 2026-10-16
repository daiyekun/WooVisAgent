using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WooVisAgent.EntityFrameworkCore.Repository;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Repository;


namespace WooVisAgent.EntityFrameworkCore;

public static class DependencyInjection
{
    public static void AddEfCore(this IHostApplicationBuilder builder)
    {
        builder.AddNpgsqlDbContext<AiCopilotDbContext>("ai-copilot");

        builder.Services.AddScoped(typeof(IReadRepository<>), typeof(EfReadRepository<>));
        builder.Services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
        builder.Services.AddScoped<IDataQueryService, DataQueryService>();

        builder.Services.AddIdentityCore<IdentityUser>(options =>
            {
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 8;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AiCopilotDbContext>();
    }
}