using Microsoft.AspNetCore.Identity;

namespace OnyxFilter.Models;

public class ApplicationUser : IdentityUser
{
    public string Role { get; set; } = "User";
}
