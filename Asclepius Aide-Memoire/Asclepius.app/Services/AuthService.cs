using System.Net.Http.Json;
using Asclepius.App.Models;

namespace Asclepius.App.Services
{
    public class AuthService
    {
        private readonly HttpClient _http;
        private const string TokenKey = "asclepius_jwt_token";

        public AuthService(HttpClient http)
        {
            _http = http;
        }

        // Send registration to API
        public async Task<AuthResponseDto> RegisterAsync(RegisterDto dto)
        {
            try
            {
                var response = await _http.PostAsJsonAsync("api/Auth/register", dto);
                var result = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
                return result ?? new AuthResponseDto { IsSuccess = false, Message = "Server returned empty response." };
            }
            catch (Exception ex)
            {
                return new AuthResponseDto { IsSuccess = false, Message = $"Connection error: {ex.Message}" };
            }
        }

        // Send login to API and save token
        public async Task<AuthResponseDto> LoginAsync(LoginDto dto)
        {
            try
            {
                var response = await _http.PostAsJsonAsync("api/Auth/login", dto);
                var result = await response.Content.ReadFromJsonAsync<AuthResponseDto>();

                if (result != null && result.IsSuccess && !string.IsNullOrEmpty(result.Token))
                {
                    // Securely saves token inside the device's encrypted hardware storage!
                    await SecureStorage.Default.SetAsync(TokenKey, result.Token);
                }

                return result ?? new AuthResponseDto { IsSuccess = false, Message = "Invalid credentials." };
            }
            catch (Exception ex)
            {
                return new AuthResponseDto { IsSuccess = false, Message = $"Connection error: {ex.Message}" };
            }
        }

        // Logout by wiping the token from hardware storage
        public void Logout()
        {
            SecureStorage.Default.Remove(TokenKey);
        }

        // Check if user has an active token
        public async Task<string?> GetTokenAsync()
        {
            return await SecureStorage.Default.GetAsync(TokenKey);
        }

        public async Task<AuthResponseDto> ResetPasswordAsync(ResetPasswordDto dto)
        {
            try
            {
                var response = await _http.PostAsJsonAsync("api/Auth/reset-password", dto);
                return await response.Content.ReadFromJsonAsync<AuthResponseDto>()
                       ?? new AuthResponseDto { IsSuccess = false, Message = "Reset failed." };
            }
            catch (Exception ex)
            {
                return new AuthResponseDto { IsSuccess = false, Message = $"Connection error: {ex.Message}" };
            }
        }
    }
}