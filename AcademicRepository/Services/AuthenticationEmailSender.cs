using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text.Json;
using AcademicRepository.Models;
using Microsoft.Extensions.Options;

namespace AcademicRepository.Services;

public sealed class AuthenticationCodeOptions
{
    [Range(1, 30)] public int ExpiryMinutes { get; set; } = 10;
    [Range(1, 10)] public int MaxAttempts { get; set; } = 5;
    [Range(1, 600)] public int ResendCooldownSeconds { get; set; } = 60;
    [Range(1, 100)] public int MaxRequestsPerHour { get; set; } = 5;
    [Range(1, 30)] public int RecoveryGrantExpiryMinutes { get; set; } = 10;
    [Range(1, 10000)] public int IpPermitLimit { get; set; } = 20;
    public string? HmacKey { get; set; }
}

public sealed class EmailOptions
{
    public string DeliveryMode { get; set; } = "MicrosoftGraph";
    public string From { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string? DevelopmentStudentRecipient { get; set; }

    public bool HasValidDevelopmentRecipient(bool isDevelopment) => string.IsNullOrEmpty(DevelopmentStudentRecipient)
        || (isDevelopment && InstitutionalEmail.IsValid(DevelopmentStudentRecipient));

    public bool HasValidGraphConfiguration() => DeliveryMode == "MicrosoftGraph"
        && Guid.TryParse(TenantId, out _) && Guid.TryParse(ClientId, out _)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && MailAddress.TryCreate(From, out var address)
        && string.Equals(address.Address, From, StringComparison.OrdinalIgnoreCase);
}

public interface IAuthenticationEmailSender
{
    Task SendCodeAsync(string email, string code, CodePurpose purpose, CancellationToken cancellationToken);
}

public sealed class AuthenticationEmailSender(HttpClient httpClient, IOptions<EmailOptions> options, IWebHostEnvironment environment,
    ILogger<AuthenticationEmailSender> logger) : IAuthenticationEmailSender
{
    public async Task SendCodeAsync(string email, string code, CodePurpose purpose, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.HasValidDevelopmentRecipient(environment.IsDevelopment()))
            throw new InvalidOperationException("Student recipient override requires Development and a valid institutional email.");
        var recipient = purpose == CodePurpose.StudentLogin && !string.IsNullOrEmpty(settings.DevelopmentStudentRecipient)
            ? settings.DevelopmentStudentRecipient.Trim() : email;
        if (settings.DeliveryMode == "DevelopmentLog")
        {
            if (!environment.IsDevelopment()) throw new InvalidOperationException("Development OTP delivery is forbidden outside Development.");
            logger.LogInformation("DEVELOPMENT ONLY: {Purpose} code for {Email}: {Code}", purpose, email, code);
            return;
        }
        if (!settings.HasValidGraphConfiguration())
            throw new InvalidOperationException("Configure Microsoft Graph TenantId, ClientId, ClientSecret and From for authentication email.");

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post,
            $"https://login.microsoftonline.com/{Guid.Parse(settings.TenantId):D}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            })
        };
        using var tokenResponse = await httpClient.SendAsync(tokenRequest, cancellationToken);
        // Never include token responses, credentials or mail payloads in exceptions/logs.
        if (!tokenResponse.IsSuccessStatusCode)
            throw new HttpRequestException("Microsoft 365 authentication failed.", null, tokenResponse.StatusCode);
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!tokenJson.RootElement.TryGetProperty("access_token", out var tokenValue)
            || tokenValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tokenValue.GetString()))
            throw new InvalidOperationException("Microsoft 365 returned no access token.");

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(settings.From)}/sendMail");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenValue.GetString());
        request.Content = JsonContent.Create(new
        {
            message = new
            {
                subject = purpose == CodePurpose.StudentLogin ? "SMIU student sign-in code" : "SMIU staff password recovery code",
                body = new { contentType = "Text", content = $"Your verification code is {code}. It expires shortly and can be used once. If you did not request this code, ignore this message." },
                toRecipients = new[] { new { emailAddress = new { address = recipient } } }
            },
            saveToSentItems = false
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
            throw new HttpRequestException("Microsoft Graph did not accept the authentication email.", null, response.StatusCode);
    }
}
