using Microsoft.AspNetCore.Identity;

namespace DocAgent.Core.Entities;

/// <summary>Application user. Guid keys; email/password and external logins via ASP.NET Core Identity.</summary>
public class AppUser : IdentityUser<Guid>;
