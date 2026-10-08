using System.Net;
using System.Text.Json;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static partial class IntegrationChecks
{
    static async Task Office365EmailChecks()
    {
        var settings = new EmailOptions
        {
            TenantId = Guid.NewGuid().ToString(), ClientId = Guid.NewGuid().ToString(),
            ClientSecret = "test-only-secret+&=", From = "sender@smiu.edu.pk"
        };
        Check(settings.HasValidGraphConfiguration(), "Microsoft Graph email configuration accepted");
        Check(!new EmailOptions().HasValidGraphConfiguration(), "Missing Microsoft Graph credentials rejected");
        foreach (var redirect in new[] { false, true })
        foreach (var purpose in new[] { CodePurpose.StudentLogin, CodePurpose.StaffRecovery })
        {
            settings.DevelopmentStudentRecipient = redirect ? "ranjhanisaddam@smiu.edu.pk" : null;
            var requests = 0;
            using var http = new HttpClient(new EmailTestHandler(async request =>
            {
                requests++;
                Check(request.Method == HttpMethod.Post, "Office 365 uses POST");
                if (requests == 1)
                {
                    Check(request.RequestUri!.AbsoluteUri == $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token", "Tenant-specific token endpoint");
                    var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync());
                    Check(fields["client_secret"] == settings.ClientSecret && fields["client_id"] == settings.ClientId
                        && fields["scope"] == "https://graph.microsoft.com/.default" && fields["grant_type"] == "client_credentials", "App credentials correctly form-encoded");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test-access-token\"}") };
                }
                Check(request.RequestUri!.AbsoluteUri == "https://graph.microsoft.com/v1.0/users/sender%40smiu.edu.pk/sendMail"
                    && request.Headers.Authorization?.ToString() == "Bearer test-access-token", "Graph uses configured sender and bearer token");
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var message = json.RootElement.GetProperty("message");
                Check(message.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString() == (redirect && purpose == CodePurpose.StudentLogin ? "ranjhanisaddam@smiu.edu.pk" : "CSC20F005@smiu.edu.pk")
                    && message.GetProperty("body").GetProperty("content").GetString()!.Contains("123456")
                    && message.GetProperty("subject").GetString() == (purpose == CodePurpose.StudentLogin ? "SMIU student sign-in code" : "SMIU staff password recovery code")
                    && !json.RootElement.GetProperty("saveToSentItems").GetBoolean(), "Graph OTP message recipient, purpose and content");
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }));
            await new AuthenticationEmailSender(http, Options.Create(settings), new EmailTestEnvironment { EnvironmentName = redirect ? "Development" : "Production" })
                .SendCodeAsync("CSC20F005@smiu.edu.pk", "123456", purpose, default);
            Check(requests == 2, "Office 365 token exchange and accepted send");
        }
        settings.DevelopmentStudentRecipient = null;
        foreach (var failToken in new[] { true, false })
        {
            var requests = 0;
            using var http = new HttpClient(new EmailTestHandler(_ => Task.FromResult(++requests == 1 && !failToken
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test-access-token\"}") }
                : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("sensitive-response-body") })));
            try
            {
                await new AuthenticationEmailSender(http, Options.Create(settings), new EmailTestEnvironment())
                    .SendCodeAsync("recipient@smiu.edu.pk", "123456", CodePurpose.StudentLogin, default);
                Check(false, "Failed Office 365 request must throw");
            }
            catch (HttpRequestException exception)
            {
                Check(exception.StatusCode == HttpStatusCode.Forbidden && !exception.Message.Contains("sensitive-response-body")
                    && requests == (failToken ? 1 : 2), "Office 365 failure stops delivery without exposing response secrets");
            }
        }
        using var noNetwork = new HttpClient(new EmailTestHandler(_ => throw new InvalidOperationException("Unexpected network request")));
        settings.DevelopmentStudentRecipient = "ranjhanisaddam@smiu.edu.pk";
        Check(settings.HasValidDevelopmentRecipient(true) && !settings.HasValidDevelopmentRecipient(false), "Student recipient override is Development-only");
        try
        {
            await new AuthenticationEmailSender(noNetwork, Options.Create(settings), new EmailTestEnvironment())
                .SendCodeAsync("CSC20F005@smiu.edu.pk", "123456", CodePurpose.StudentLogin, default);
            Check(false, "Production must reject recipient override");
        }
        catch (InvalidOperationException exception)
        {
            Check(exception.Message.Contains("Student recipient override requires Development"), "Production override rejected before network access");
        }
        settings.DevelopmentStudentRecipient = "outside@gmail.com";
        Check(!settings.HasValidDevelopmentRecipient(true), "Development recipient must be institutional");
        var development = new EmailOptions { DeliveryMode = "DevelopmentLog" };
        try
        {
            await new AuthenticationEmailSender(noNetwork, Options.Create(development), new EmailTestEnvironment { EnvironmentName = "Development" })
                .SendCodeAsync("recipient@smiu.edu.pk", "123456", CodePurpose.StudentLogin, default);
            Check(false, "Development OTPs must not be written to logs");
        }
        catch (InvalidOperationException exception)
        {
            Check(exception.Message.Contains("Development OTP logging is disabled"), "Development delivery never logs or reports an OTP");
        }
        try
        {
            await new AuthenticationEmailSender(noNetwork, Options.Create(development), new EmailTestEnvironment())
                .SendCodeAsync("recipient@smiu.edu.pk", "123456", CodePurpose.StudentLogin, default);
            Check(false, "Production must reject development delivery");
        }
        catch (InvalidOperationException exception)
        {
            Check(exception.Message.Contains("forbidden outside Development"), "Production forbids development OTP logging");
        }
    }
}

internal sealed class EmailTestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return respond(request);
    }
}

internal sealed class EmailTestEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "AcademicRepository.Tests";
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
