using System;
using API.DTOs;
using API.Entities;
using Microsoft.AspNetCore.Authorization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using API.RequestHelpers;
using API.Services;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;

namespace API.Controllers;

public class AccountController(
    SignInManager<User> signInManager,
    IEmailService emailService,
    IOptions<EmailSettings> emailSettings,
    IWebHostEnvironment env,
    IConfiguration config,
    ILogger<AccountController> logger) : BaseApiController
{
    private readonly EmailSettings _emailSettings = emailSettings.Value;
    private readonly bool _isDevelopment = env.IsDevelopment();
    private readonly IConfiguration _config = config;
    private readonly IEmailService _emailService = emailService;
    private readonly ILogger<AccountController> _logger = logger;

    [HttpPost("register")]
    public async Task<ActionResult> RegisterUser(RegisterDto registerDto)
    {
        var user = new User{UserName = registerDto.Email, Email = registerDto.Email, NewsletterOptIn = registerDto.NewsletterOptIn};

        var result = await signInManager.UserManager.CreateAsync(user, registerDto.Password);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(error.Code, error.Description);
            }

            return ValidationProblem();
        }

        await signInManager.UserManager.AddToRoleAsync(user, "Member");

        await TrySendWelcomeEmailAsync(user);

        return Ok();
    }

    [HttpGet("user-info")]
    public async Task<ActionResult> GetUserInfo()
    {
        if (User.Identity?.IsAuthenticated == false) return NoContent();

        var user = await signInManager.UserManager.GetUserAsync(User);

        if (user == null) return Unauthorized();

        var roles = await signInManager.UserManager.GetRolesAsync(user);

        return Ok(new 
        {
            user.Email,
            user.UserName,
            user.NewsletterOptIn,
            Roles = roles
        });
    }

    [HttpPost("logout")]
    public async Task<ActionResult> Logout()
    {
        await signInManager.SignOutAsync();

        return NoContent();
    }

    [Authorize]
    [HttpPost("address")]
    public async Task<ActionResult<Address>> CreateOrUpdateAddress(Address address)
    {
        var user = await signInManager.UserManager.Users
            .Include(x => x.Address)
            .FirstOrDefaultAsync(x => x.UserName == User.Identity!.Name);

        if (user == null) return Unauthorized();

        user.Address = address;

        var result = await signInManager.UserManager.UpdateAsync(user);

        if (!result.Succeeded) return BadRequest("Problem updating user address");

        return Ok(user.Address);
    }

    [Authorize]
    [HttpGet("address")]
    public async Task<ActionResult<Address>> GetSavedAddress()
    {
        var address = await signInManager.UserManager.Users
            .Where(x => x.UserName == User.Identity!.Name)
            .Select(x => x.Address)
            .FirstOrDefaultAsync();

        if (address == null) return NoContent();

        return address;
    }

    [Authorize]
    [HttpPut("user-info")]
    public async Task<ActionResult> UpdateUserInfo(UpdateUserDto update)
    {
        var user = await signInManager.UserManager.GetUserAsync(User);

        if (user == null) return Unauthorized();

        // If email is changing, ensure it's not already taken and update via UserManager
        if (!string.IsNullOrWhiteSpace(update.Email) && update.Email != user.Email)
        {
            var existing = await signInManager.UserManager.FindByEmailAsync(update.Email);
            if (existing != null && existing.Id != user.Id)
            {
                return BadRequest("Email is already in use");
            }

            var setEmailResult = await signInManager.UserManager.SetEmailAsync(user, update.Email);
            if (!setEmailResult.Succeeded)
            {
                return BadRequest("Problem updating email");
            }
        }

        // If username is changing, ensure it's not already taken and update via UserManager
        if (!string.IsNullOrWhiteSpace(update.UserName) && update.UserName != user.UserName)
        {
            var existingUser = await signInManager.UserManager.FindByNameAsync(update.UserName);
            if (existingUser != null && existingUser.Id != user.Id)
            {
                return BadRequest("Username is already in use");
            }

            var setUserNameResult = await signInManager.UserManager.SetUserNameAsync(user, update.UserName);
            if (!setUserNameResult.Succeeded)
            {
                return BadRequest("Problem updating username");
            }
        }

        // Ensure other user fields are persisted
        if (update.NewsletterOptIn.HasValue)
        {
            user.NewsletterOptIn = update.NewsletterOptIn.Value;
        }

        var result = await signInManager.UserManager.UpdateAsync(user);

        if (!result.Succeeded) return BadRequest("Problem updating user");

        var roles = await signInManager.UserManager.GetRolesAsync(user);

        return Ok(new
        {
            user.Email,
            user.UserName,
            user.NewsletterOptIn,
            Roles = roles
        });
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<ActionResult> ChangePassword(ChangePasswordDto dto)
    {
        var user = await signInManager.UserManager.GetUserAsync(User);

        if (user == null) return Unauthorized();

        var result = await signInManager.UserManager.ChangePasswordAsync(user, dto.CurrentPassword, dto.NewPassword);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(error.Code, error.Description);
            }

            return ValidationProblem();
        }

        return Ok();
    }

    [Authorize]
    [HttpPost("delete-account")]
    public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountDto dto, [FromServices] AccountDeletionService deletionService, CancellationToken ct)
    {
        var user = await signInManager.UserManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        // Prevent deleting the last admin account.
        var roles = await signInManager.UserManager.GetRolesAsync(user);
        if (roles.Contains("Admin"))
        {
            var admins = await signInManager.UserManager.GetUsersInRoleAsync("Admin");
            if (admins.Count <= 1)
            {
                return BadRequest("Não é possível apagar o último admin");
            }
        }

        var (ok, error) = await deletionService.DeleteUserAsync(user, dto?.DeleteStoredData ?? false, ct);
        if (!ok) return BadRequest(error ?? "Problem deleting account");

        await signInManager.SignOutAsync();
        return NoContent();
    }

    [HttpPost("login")]
    public async Task<ActionResult> Login(LoginDto login)
    {
        if (login == null) return BadRequest("Invalid login request");

        string? userName = null;

        // try find by email first
        if (!string.IsNullOrWhiteSpace(login.Identifier) && login.Identifier.Contains('@'))
        {
            var byEmail = await signInManager.UserManager.FindByEmailAsync(login.Identifier);
            if (byEmail != null) userName = byEmail.UserName;
        }

        // if not found by email, try as username
        if (userName == null)
        {
            var byName = await signInManager.UserManager.FindByNameAsync(login.Identifier);
            if (byName != null) userName = byName.UserName;
        }

        if (userName == null)
            return Unauthorized();

        var result = await signInManager.PasswordSignInAsync(userName, login.Password, isPersistent: false, lockoutOnFailure: false);

        if (!result.Succeeded) return Unauthorized();

        return Ok();
    }

     [HttpPost("google-login")]
     public async Task<ActionResult> GoogleLogin([FromBody] GoogleLoginDto dto)
     {
         if (dto == null || string.IsNullOrWhiteSpace(dto.Token))
             return BadRequest("Invalid Google token");

         try
         {
             // Verify the Google ID token using Google Auth library
             var payload = await GoogleJsonWebSignature.ValidateAsync(dto.Token);
             
             if (payload == null)
                 return Unauthorized("Invalid Google token");

             var email = payload.Email;
             if (string.IsNullOrWhiteSpace(email))
                 return Unauthorized("Google token did not contain an email");

             // Find user
             var user = await signInManager.UserManager.FindByEmailAsync(email);
             if (user == null)
                 return NotFound(new { message = "UserNotFound" });

             // Sign in the user (cookie-based)
             await signInManager.SignInAsync(user, isPersistent: false);

             return Ok();
         }
         catch (Exception ex)
         {
             return Unauthorized($"Token validation failed: {ex.Message}");
         }
     }

     [HttpPost("google-register")]
     public async Task<ActionResult> GoogleRegister([FromBody] GoogleLoginDto dto)
     {
         if (dto == null || string.IsNullOrWhiteSpace(dto.Token))
             return BadRequest("Invalid Google token");

         try
         {
             // Verify the Google ID token using Google Auth library
             var payload = await GoogleJsonWebSignature.ValidateAsync(dto.Token);
             
             if (payload == null)
                 return Unauthorized("Invalid Google token");

             var email = payload.Email;
             if (string.IsNullOrWhiteSpace(email))
                 return Unauthorized("Google token did not contain an email");

             // Find or create user
             var user = await signInManager.UserManager.FindByEmailAsync(email);
             var created = false;
             if (user == null)
             {
                 user = new User { UserName = email, Email = email, NewsletterOptIn = false };
                 var createResult = await signInManager.UserManager.CreateAsync(user);
                 if (!createResult.Succeeded)
                 {
                     foreach (var error in createResult.Errors)
                         ModelState.AddModelError(error.Code, error.Description);
                     return ValidationProblem();
                 }

                 created = true;
             }

             // Ensure roles exist for Google users
             var roles = await signInManager.UserManager.GetRolesAsync(user);
             if (!roles.Contains("Member"))
             {
                 await signInManager.UserManager.AddToRoleAsync(user, "Member");
             }

             var adminEmail = _config["AdminSettings:Email"] ?? _config["ADMIN_EMAIL"];
             if (!string.IsNullOrWhiteSpace(adminEmail)
                 && string.Equals(adminEmail, user.Email, StringComparison.OrdinalIgnoreCase))
             {
                 if (!roles.Contains("Admin"))
                 {
                     await signInManager.UserManager.AddToRoleAsync(user, "Admin");
                 }
             }

             // Sign in the user (cookie-based)
             await signInManager.SignInAsync(user, isPersistent: false);

             if (created)
             {
                 await TrySendWelcomeEmailAsync(user);
             }

             return Ok();
         }
         catch (Exception ex)
         {
             return Unauthorized($"Token validation failed: {ex.Message}");
         }
     }

     private string? TryGetFrontendUrl()
     {
         var frontendUrl = (_emailSettings.FrontendUrl ?? string.Empty).TrimEnd('/');
         if (!string.IsNullOrWhiteSpace(frontendUrl)) return frontendUrl;

         var origin = Request.Headers.Origin.ToString().TrimEnd('/');
         if (!string.IsNullOrWhiteSpace(origin)) return origin;

         if (Request.Host.HasValue)
         {
             return $"{Request.Scheme}://{Request.Host}".TrimEnd('/');
         }

         return null;
     }

     private async Task TrySendWelcomeEmailAsync(User user)
     {
         try
         {
             if (string.IsNullOrWhiteSpace(user.Email)) return;

             var frontendUrl = TryGetFrontendUrl();
             if (string.IsNullOrWhiteSpace(frontendUrl))
             {
                 _logger.LogWarning("Welcome email skipped: FrontendUrl is not configured and could not be inferred from request.");
                 return;
             }

             var subject = "Bem-vindo(a)!";
             var html = $@"
<div style=""font-family: Arial, sans-serif; color:#111827;"">
    <h2 style=""margin:0 0 12px 0;"">Bem-vindo(a)!</h2>
    <p style=""margin:0 0 16px 0;"">A sua conta foi criada com sucesso.</p>
    <p style=""margin:0 0 16px 0;"">
        <a href=""{frontendUrl}"" style=""display:inline-block;background:#2563eb;color:#ffffff;text-decoration:none;padding:10px 14px;border-radius:8px;"">Visitar a loja</a>
  </p>
    <p style=""margin:0;font-size:12px;color:#6b7280;"">Se não reconhece este registo, por favor contacte o suporte.</p>
</div>";

             var sent = await _emailService.SendEmailAsync(user.Email, subject, html);
             if (!sent)
             {
                 _logger.LogWarning("Welcome email failed to send for {Email}. Check EmailSettings configuration.", user.Email);
             }
         }
         catch (Exception ex)
         {
             _logger.LogWarning(ex, "Welcome email failed unexpectedly.");
         }
     }

     [HttpPost("forgot-password")]
     public async Task<ActionResult> ForgotPassword(ForgotPasswordDto dto)
     {
         if (dto == null || string.IsNullOrWhiteSpace(dto.Email)) return BadRequest();

        var user = await signInManager.UserManager.FindByEmailAsync(dto.Email);

        // Do not reveal whether user exists
        if (user == null) return Ok(new { message = "Se existir uma conta com esse email, enviámos um link para repor a password." });

        // Prefer explicit config, but fall back to request Origin/Host so production can work even if FrontendUrl isn't set.
        var frontendUrl = (_emailSettings.FrontendUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(frontendUrl))
        {
            var origin = Request.Headers.Origin.ToString().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(origin))
            {
                frontendUrl = origin;
            }
            else if (Request.Host.HasValue)
            {
                frontendUrl = $"{Request.Scheme}://{Request.Host}".TrimEnd('/');
            }
        }

        if (string.IsNullOrWhiteSpace(frontendUrl))
        {
            _logger.LogWarning("ForgotPassword: FrontendUrl is not configured and could not be inferred from request.");
            return Ok(new { message = "Se existir uma conta com esse email, enviámos um link para repor a password." });
        }

        var token = await signInManager.UserManager.GeneratePasswordResetTokenAsync(user);
        var encodedToken = WebUtility.UrlEncode(token);

        // Build a full reset URL using configured frontend base URL
                var resetUrl = $"{frontendUrl}/reset-password?email={WebUtility.UrlEncode(user.Email)}&token={encodedToken}";

                var subject = "Repor password";
                var html = $@"
<div style=""font-family: Arial, sans-serif; color:#111827;"">
    <h2 style=""margin:0 0 12px 0;"">Repor password</h2>
    <p style=""margin:0 0 16px 0;"">Recebemos um pedido para repor a sua password. Para continuar, clique no botão abaixo:</p>
    <p style=""margin:0 0 16px 0;"">
        <a href=""{resetUrl}"" style=""display:inline-block;background:#2563eb;color:#ffffff;text-decoration:none;padding:10px 14px;border-radius:8px;"">Repor password</a>
    </p>
    <p style=""margin:0 0 8px 0;font-size:12px;color:#6b7280;"">Se não pediu esta alteração, pode ignorar este email.</p>
    <p style=""margin:0;font-size:12px;color:#6b7280;"">Se o botão não funcionar, copie e cole este link no browser:<br/>
        <a href=""{resetUrl}"" style=""color:#2563eb;"">{resetUrl}</a>
    </p>
</div>";

                var sent = await _emailService.SendEmailAsync(user.Email!, subject, html);
                if (!sent)
                {
                    _logger.LogWarning("ForgotPassword email failed to send for {Email}. Check EmailSettings configuration.", user.Email);
                }

                // In development, returning the link can help local testing.
                if (_isDevelopment)
                {
                        return Ok(new { message = "Link enviado por email. Verificar Spam", resetUrl });
                }

                return Ok(new { message = "Se existir uma conta com esse email, enviámos um link para repor a password." });
    }
    

    [HttpPost("reset-password")]
    public async Task<ActionResult> ResetPassword(ResetPasswordDto dto)
    {
        if (dto == null) return BadRequest();

        var user = await signInManager.UserManager.FindByEmailAsync(dto.Email);
        if (user == null) return BadRequest("Invalid request");

        var token = WebUtility.UrlDecode(dto.Token);

        var result = await signInManager.UserManager.ResetPasswordAsync(user, token, dto.NewPassword);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(error.Code, error.Description);
            }

            return ValidationProblem();
        }

        return Ok();
    }
}