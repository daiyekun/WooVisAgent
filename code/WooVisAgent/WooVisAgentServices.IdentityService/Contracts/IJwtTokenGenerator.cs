using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;

namespace WooVisAgentServices.IdentityService.Contracts;

public interface IJwtTokenGenerator
{
    Task<string> GenerateTokenAsync(IdentityUser user);
}