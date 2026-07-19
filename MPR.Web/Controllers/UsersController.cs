using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MPR.Domain.Entities;
using MPR.Domain.Enums;
using MPR.Infrastructure.Persistence;

namespace MPR.Web.Controllers;

public record CreateUserRequest(string FullName, string Email, string UserName, string TempPassword, AppRole Role, bool CanExportOrEmail);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record AdminResetPasswordRequest(int UserId, string NewTempPassword);

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher<AppUser> _hasher = new();

    public UsersController(AppDbContext db) => _db = db;

    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<IEnumerable<object>>> List()
    {
        var users = await _db.Users
            .Select(u => new { u.Id, u.FullName, u.Email, u.UserName, u.Role, u.CanExportOrEmail, u.IsActive, u.LastLoginAt })
            .ToListAsync();
        return Ok(users);
    }

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<int>> Create(CreateUserRequest req)
    {
        if (await _db.Users.AnyAsync(u => u.UserName == req.UserName || u.Email == req.Email))
            return Conflict("A user with that username or email already exists.");

        var user = new AppUser
        {
            FullName = req.FullName,
            Email = req.Email,
            UserName = req.UserName,
            Role = req.Role,
            CanExportOrEmail = req.CanExportOrEmail,
            MustChangePassword = true
        };
        user.PasswordHash = _hasher.HashPassword(user, req.TempPassword);

        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return Ok(user.Id);
    }

    [HttpPut("{id}/deactivate")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return NotFound();
        user.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id}/reactivate")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Reactivate(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return NotFound();
        user.IsActive = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/admin-reset-password")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AdminResetPassword(int id, AdminResetPasswordRequest req)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return NotFound();
        user.PasswordHash = _hasher.HashPassword(user, req.NewTempPassword);
        user.MustChangePassword = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Any authenticated user, including admins, can change their own password.
    [HttpPost("me/change-password")]
    public async Task<IActionResult> ChangeOwnPassword(ChangePasswordRequest req)
    {
        int userId = int.Parse(User.FindFirst("sub")?.Value ?? "0");
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return Unauthorized();

        var verify = _hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword);
        if (verify == PasswordVerificationResult.Failed) return BadRequest("Current password is incorrect.");

        if (req.NewPassword.Length < 8)
            return BadRequest("New password must be at least 8 characters.");

        user.PasswordHash = _hasher.HashPassword(user, req.NewPassword);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
