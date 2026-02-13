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
    public const string PUSHOVER_ENDPOINT = "https://api.pushover.net/1/messages.json";
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(12)  // Slightly longer than NotificationService guard timeout (10s) to ensure guard timeout triggers first
    };

    /// <summary>
    /// Result of a Pushover API call with full diagnostic details.
    /// </summary>
    public class SendResult
    {
        public bool Success { get; set; }
        public int? HttpStatusCode { get; set; }
        public string? ResponseBody { get; set; }
        public Exception? Exception { get; set; }
    }

    /// <summary>
    /// Send a push notification via Pushover API.
    /// Non-blocking: returns Task that should be fire-and-forget.
    /// </summary>
    /// <param name="userKey">Pushover user key</param>
    /// <param name="appToken">Pushover application token</param>
    /// <param name="title">Notification title</param>
    /// <param name="message">Notification message</param>
    /// <param name="priority">Priority (0 = normal, 1 = high, 2 = emergency)</param>
    /// <returns>SendResult with success status and full diagnostic details</returns>
    public static async Task<SendResult> SendAsync(string userKey, string appToken, string title, string message, int priority = 0)
    {
        var result = new SendResult();
        
        if (string.IsNullOrWhiteSpace(userKey) || string.IsNullOrWhiteSpace(appToken))
        {
            result.Success = false;
            result.Exception = new ArgumentException("User key or app token is null or empty");
            return result;
        }

        try
        {
            var formData = new StringBuilder();
            formData.Append($"token={Uri.EscapeDataString(appToken)}");
            formData.Append($"&user={Uri.EscapeDataString(userKey)}");
            formData.Append($"&title={Uri.EscapeDataString(title)}");
            formData.Append($"&message={Uri.EscapeDataString(message)}");
            formData.Append($"&priority={priority}");
            
            // Priority 2 (emergency) requires both expire and retry parameters
            // expire: maximum time in seconds that the notification will be retried (max 10800 = 3 hours)
            // retry: how often in seconds the notification will be retried (min 30 seconds)
            // Set expire to 3600 seconds (1 hour) and retry to 60 seconds (retry every minute)
            if (priority == 2)
            {
                formData.Append($"&expire=3600");
                formData.Append($"&retry=60");
            }

            var content = new StringContent(formData.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await _httpClient.PostAsync(PUSHOVER_ENDPOINT, content);
            
            result.HttpStatusCode = (int)response.StatusCode;
            result.ResponseBody = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                result.Success = false;
                result.Exception = new HttpRequestException($"Pushover API returned non-success status code: {response.StatusCode}");
            }
            else
            {
                result.Success = true;
            }
        }
        catch (HttpRequestException ex)
        {
            result.Success = false;
            result.Exception = ex;
        }
        catch (TaskCanceledException ex)
        {
            result.Success = false;
            result.Exception = ex;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Exception = ex;
        }
        
        return result;
    }
}
