using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace QTSW2.Robot.Core.Notifications;

/// <summary>
/// Pushover API client for sending push notifications.
/// Non-blocking: returns Task, never awaited on hot paths.
/// </summary>
public static class PushoverClient
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    /// <summary>
    /// Send a push notification via Pushover API.
    /// Non-blocking: returns Task that should be fire-and-forget.
    /// </summary>
    /// <param name="userKey">Pushover user key</param>
    /// <param name="appToken">Pushover application token</param>
    /// <param name="title">Notification title</param>
    /// <param name="message">Notification message</param>
    /// <param name="priority">Priority (0 = normal, 1 = high, 2 = emergency)</param>
    /// <returns>True if sent successfully, false otherwise</returns>
    public static async Task<bool> SendAsync(string userKey, string appToken, string title, string message, int priority = 0)
    {
        if (string.IsNullOrWhiteSpace(userKey) || string.IsNullOrWhiteSpace(appToken))
            return false;

        try
        {
            var formData = new StringBuilder();
            formData.Append($"token={Uri.EscapeDataString(appToken)}");
            formData.Append($"&user={Uri.EscapeDataString(userKey)}");
            formData.Append($"&title={Uri.EscapeDataString(title)}");
            formData.Append($"&message={Uri.EscapeDataString(message)}");
            formData.Append($"&priority={priority}");

            var content = new StringContent(formData.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await _httpClient.PostAsync("https://api.pushover.net/1/messages.json", content);
            
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Swallow exceptions - notification failures should not crash the robot
            return false;
        }
    }
}
