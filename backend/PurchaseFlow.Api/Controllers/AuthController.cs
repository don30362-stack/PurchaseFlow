using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PurchaseFlow.Api.Data;

namespace PurchaseFlow.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAntiforgery _antiforgery;

    public AuthController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAntiforgery antiforgery)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _antiforgery = antiforgery;
    }

    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult GetCsrf()
    {
        Response.Headers["Cache-Control"] = "no-store";

        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);

        return Ok(new
        {
            csrfToken = tokens.RequestToken
        });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request)
    {
        var result = await _signInManager.PasswordSignInAsync(
            request.UserName,
            request.Password,
            isPersistent: false,
            lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            return Unauthorized(new
            {
                message = "帳號或密碼錯誤，或帳號暫時無法登入。"
            });
        }

        return Ok(new
        {
            message = "登入成功。"
        });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();

        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Unauthorized();
        }

        var roles = await _userManager.GetRolesAsync(user);

        return Ok(new
        {
            id = user.Id,
            userName = user.UserName,
            role = roles.SingleOrDefault()
        });
    }
}

public sealed class LoginRequest
{
    [Required]
    public string UserName { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";
}