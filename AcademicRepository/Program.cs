using AcademicRepository.Data;
using AcademicRepository.Models;
using AcademicRepository.Services;
using AcademicRepository.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews(options => options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddExceptionHandler<SafeExceptionHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, StructuredAuthorizationResultHandler>();
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection is missing.")));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Password.RequiredLength = 12;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
}).AddEntityFrameworkStores<ApplicationDbContext>().AddDefaultTokenProviders().AddSignInManager<ActiveUserSignInManager>()
    .AddUserValidator<InstitutionalUserValidator>();
builder.Services.AddOptions<AuthenticationCodeOptions>().BindConfiguration("AuthenticationCodes").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<EmailOptions>().BindConfiguration("Email")
    .Validate(o => o.HasValidDevelopmentRecipient(builder.Environment.IsDevelopment()),
        "Email:DevelopmentStudentRecipient requires Development and a valid @smiu.edu.pk recipient.")
    .Validate(o => o.DeliveryMode == "DevelopmentLog" ? builder.Environment.IsDevelopment()
        : o.HasValidGraphConfiguration(),
        "Use DevelopmentLog only in Development, or configure Email:DeliveryMode=MicrosoftGraph, TenantId, ClientId, ClientSecret and From.").ValidateOnStart();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IOperationalSettingsService, OperationalSettingsService>();
builder.Services.AddScoped<IAdminSystemService, AdminSystemService>();
builder.Services.AddSingleton<AuthenticationCodeHasher>();
builder.Services.AddScoped<AuthenticationCodeService>();
builder.Services.AddHttpClient<IAuthenticationEmailSender, AuthenticationEmailSender>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthenticationCodeOptions>>().Value.IpPermitLimit,
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
    options.AddPolicy("uploads", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20, Window = TimeSpan.FromHours(1), QueueLimit = 0, AutoReplenishment = true
        }));
    options.AddPolicy("search", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
});
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<StudentSubmissionService>();
builder.Services.AddScoped<StudentDepartmentService>();
builder.Services.AddScoped<SubmissionLock>();
builder.Services.AddScoped<ProjectFileService>();
builder.Services.AddScoped<IReviewService, ReviewService>();
builder.Services.AddScoped<SubmissionWorkflowService>();
builder.Services.AddScoped<SubmissionVersionService>();
builder.Services.AddScoped<RepositoryService>();
builder.Services.AddScoped<IRepositoryService>(services => services.GetRequiredService<RepositoryService>());
builder.Services.AddScoped<IRepositoryReportService, RepositoryReportService>();
builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();
builder.Services.AddOptions<FileStorageOptions>().BindConfiguration("FileStorage").ValidateDataAnnotations().ValidateOnStart();
var requestLimit = 101L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = requestLimit);
builder.Services.Configure<Microsoft.AspNetCore.Builder.IISServerOptions>(o => o.MaxRequestBodySize = requestLimit);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = requestLimit);
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnValidatePrincipal = async context =>
    {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = context.Principal is null ? null : await users.GetUserAsync(context.Principal);
        var stampType = context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        var roles = user is null ? Array.Empty<string>() : (await users.GetRolesAsync(user)).ToArray();
        if (user is not null && roles.Contains("Coordinator") && user.DepartmentId is null)
        {
            var tempData = context.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>().GetTempData(context.HttpContext);
            tempData["Error"] = "Your Coordinator account has no department assigned. Contact your administrator before reviewing submissions.";
            tempData.Save();
        }
        else if (user is not null && roles.Contains("DepartmentHead") && user.DepartmentId is null)
        {
            var tempData = context.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>().GetTempData(context.HttpContext);
            tempData["Error"] = "Your DepartmentHead account has no department assigned. Contact your administrator before accessing the repository.";
            tempData.Save();
        }
        if (user is null || !ApplicationRoles.CanAuthenticate(user, roles) || context.Principal!.FindFirstValue(stampType) != user.SecurityStamp
            || (roles.Contains("Student") && !await context.HttpContext.RequestServices.GetRequiredService<StudentDepartmentService>().MatchesAsync(user)))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }
        await SecurityStampValidator.ValidatePrincipalAsync(context);
    };
});
var app = builder.Build();
_ = app.Services.GetRequiredService<IFileStorageService>();
_ = app.Services.GetRequiredService<AuthenticationCodeHasher>();
app.UseMiddleware<CorrelationIdMiddleware>();
if (app.Environment.IsDevelopment()) app.UseDeveloperExceptionPage();
else app.UseExceptionHandler("/Error");
app.UseStatusCodePagesWithReExecute("/Error/Status", "?statusCode={0}");
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "script-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
try
{
    await using var scope = app.Services.CreateAsyncScope();
    await IdentitySeeder.SeedAsync(scope.ServiceProvider, app.Configuration, app.Environment.IsDevelopment());
}
catch (Exception exception)
{
    app.Logger.LogCritical(exception, "Database initialization failed. Apply EF migrations and verify DefaultConnection before starting.");
    throw;
}
app.Run();

public partial class Program { }
