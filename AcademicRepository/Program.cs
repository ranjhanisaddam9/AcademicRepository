using AcademicRepository.Data;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews(options => options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection is missing.")));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
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
});
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<StudentSubmissionService>();
builder.Services.AddScoped<StudentDepartmentService>();
builder.Services.AddScoped<SubmissionLock>();
builder.Services.AddScoped<ProjectFileService>();
builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();
builder.Services.AddOptions<FileStorageOptions>().BindConfiguration("FileStorage").ValidateDataAnnotations().ValidateOnStart();
var uploadLimit = builder.Configuration.GetValue<int?>("FileStorage:MaxFileSizeMB") ?? 50;
var requestLimit = uploadLimit * 1024L * 1024 + 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = requestLimit);
builder.Services.Configure<Microsoft.AspNetCore.Builder.IISServerOptions>(o => o.MaxRequestBodySize = requestLimit);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = requestLimit);
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Events.OnValidatePrincipal = async context =>
    {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = context.Principal is null ? null : await users.GetUserAsync(context.Principal);
        var stampType = context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        var roles = user is null ? Array.Empty<string>() : (await users.GetRolesAsync(user)).ToArray();
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
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
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
