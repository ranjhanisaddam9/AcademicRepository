internal static class LoginUiChecks
{
    public static void Run()
    {
        var root = Directory.GetCurrentDirectory();
        var login = File.ReadAllText(Path.Combine(root, "AcademicRepository", "Views", "Account", "Login.cshtml"));
        var start = File.ReadAllText(Path.Combine(root, "AcademicRepository", "Views", "_ViewStart.cshtml"));
        var layout = File.ReadAllText(Path.Combine(root, "AcademicRepository", "Views", "Shared", "_AuthLayout.cshtml"));
        var css = File.ReadAllText(Path.Combine(root, "AcademicRepository", "wwwroot", "css", "site.css"));
        Check(login.Contains("id=\"signin-student\"") && login.Contains("id=\"signin-staff\"")
            && login.Contains("for=\"signin-student\"") && login.Contains("for=\"signin-staff\""), "Student/Staff switch uses labeled native radio controls");
        Check(login.Contains("asp-action=\"StudentSignIn\"") && login.Contains("asp-action=\"Login\""), "Switch retains the separate passwordless Student and password Staff endpoints");
        Check(login.Contains("id=\"studentEmail\" name=\"Email\"") && login.Contains("asp-for=\"Email\"") && login.Contains("asp-for=\"Password\""), "Both sign-in panels retain labeled inputs for their current model bindings");
        Check(start.Contains("_AuthLayout") && layout.Contains("<main id=\"mainContent\"") && layout.Contains("smiu.edu.pk"), "Account sign-in screens use the dedicated semantic university layout");
        Check(css.Contains("backdrop-filter: blur") && css.Contains("auth-card:has(#signin-student:checked)")
            && css.Contains("auth-card:has(#signin-staff:checked)"), "Centered glass panel reveals the selected sign-in mode");
        Check(css.Contains("@media (max-width: 575.98px)") && css.Contains(".auth-landing { gap:"), "Auth landing has a compact mobile layout");
    }

    private static void Check(bool condition, string label)
    {
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {label}");
        if (!condition) throw new InvalidOperationException(label);
    }
}
