using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Asclepius_Aide_Memoire.DTOs;

namespace Asclepius_Aide_Memoire.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IConfiguration _config;

        public AuthController(
            UserManager<IdentityUser> userManager,
            RoleManager<IdentityRole> roleManager,
            IConfiguration config)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _config = config;
        }

        // 1. REGISTER
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDto dto)
        {
            var userExists = await _userManager.FindByEmailAsync(dto.Email)
                             ?? await _userManager.FindByNameAsync(dto.Username);

            if (userExists != null)
                return BadRequest(new AuthResponseDto { IsSuccess = false, Message = "Username or Email already in use." });

            var user = new IdentityUser
            {
                UserName = dto.Username,
                Email = dto.Email,
                EmailConfirmed = true
            };

            var result = await _userManager.CreateAsync(user, dto.Password);
            if (!result.Succeeded)
                return BadRequest(new AuthResponseDto { IsSuccess = false, Message = string.Join("; ", result.Errors.Select(e => e.Description)) });

            if (!await _roleManager.RoleExistsAsync(dto.Role))
                await _roleManager.CreateAsync(new IdentityRole(dto.Role));

            await _userManager.AddToRoleAsync(user, dto.Role);

            return Ok(new AuthResponseDto { IsSuccess = true, Message = "User registered successfully!" });
        }

        // 2. LOGIN (Email OR Username)
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.UsernameOrEmail)
                       ?? await _userManager.FindByNameAsync(dto.UsernameOrEmail);

            if (user == null || !await _userManager.CheckPasswordAsync(user, dto.Password))
                return Unauthorized(new AuthResponseDto { IsSuccess = false, Message = "Invalid username/email or password." });

            var roles = await _userManager.GetRolesAsync(user);
            var userRole = roles.FirstOrDefault() ?? "Patient";

            var token = GenerateJwtToken(user, userRole);

            return Ok(new AuthResponseDto
            {
                IsSuccess = true,
                Token = token,
                Username = user.UserName,
                Role = userRole,
                Message = "Login successful."
            });
        }

        // 3. FORGOT PASSWORD
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
                return Ok(new AuthResponseDto { IsSuccess = true, Message = "If the email is registered, a reset code was generated." });

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);

            return Ok(new
            {
                IsSuccess = true,
                Message = "Reset token generated successfully.",
                Token = resetToken
            });
        }

        // 4. RESET PASSWORD
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
                return BadRequest(new AuthResponseDto { IsSuccess = false, Message = "Invalid request." });

            var result = await _userManager.ResetPasswordAsync(user, dto.Token, dto.NewPassword);
            if (!result.Succeeded)
                return BadRequest(new AuthResponseDto { IsSuccess = false, Message = string.Join("; ", result.Errors.Select(e => e.Description)) });

            return Ok(new AuthResponseDto { IsSuccess = true, Message = "Password has been reset successfully. You can now login." });
        }

        // Helper: Generate Token
        private string GenerateJwtToken(IdentityUser user, string role)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.UserName ?? ""),
                new Claim(ClaimTypes.Email, user.Email ?? ""),
                new Claim(ClaimTypes.Role, role)
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"] ?? "SUPER_SECRET_KEY_AT_LEAST_32_CHARS_LONG_12345!"));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"] ?? "AsclepiusServer",
                audience: _config["Jwt:Audience"] ?? "AsclepiusClient",
                claims: claims,
                expires: DateTime.UtcNow.AddDays(7),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
