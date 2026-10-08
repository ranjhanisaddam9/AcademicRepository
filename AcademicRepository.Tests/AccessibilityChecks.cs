using System.Text.RegularExpressions;

internal static partial class IntegrationChecks
{
    public static void AccessibilityChecks()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "AcademicRepository");
        var views = Path.Combine(root, "Views");
        var layout = File.ReadAllText(Path.Combine(views, "Shared", "_Layout.cshtml"));
        Check(layout.Contains("<header", StringComparison.Ordinal) && layout.Contains("<nav", StringComparison.Ordinal)
            && layout.Contains("<aside", StringComparison.Ordinal) && layout.Contains("<main", StringComparison.Ordinal)
            && layout.Contains("<footer", StringComparison.Ordinal), "Shared layout contains semantic page landmarks");
        Check(Regex.Matches(layout, "<span class=\"nav-icon\"(?![^>]*aria-hidden=\"true\")[^>]*>").Count == 0
            && layout.Contains("class=\"sidebar-home-icon\" aria-hidden=\"true\"", StringComparison.Ordinal),
            "Decorative navigation icons are hidden from assistive technology");

        var viewFiles = Directory.GetFiles(views, "*.cshtml", SearchOption.AllDirectories);
        var unscopedHeaders = viewFiles.SelectMany(File.ReadAllLines)
            .SelectMany(line => Regex.Matches(line, "<th(?=[\\s>])[^>]*>").Select(match => match.Value))
            .Where(tag => !Regex.IsMatch(tag, "\\bscope=\"col\"", RegexOptions.IgnoreCase)).ToArray();
        Check(unscopedHeaders.Length == 0, "All rendered table column headers declare scope=col");
        var unlabelledImages = viewFiles.SelectMany(File.ReadAllLines)
            .SelectMany(line => Regex.Matches(line, "<img\\b[^>]*>").Select(match => match.Value))
            .Where(tag => !Regex.IsMatch(tag, "\\balt=", RegexOptions.IgnoreCase)).ToArray();
        Check(unlabelledImages.Length == 0, "Every image has an alt attribute (or there are no images)");

        var pagination = File.ReadAllText(Path.Combine(views, "Shared", "_Pagination.cshtml"));
        Check(pagination.Contains("aria-current=\"page\"", StringComparison.Ordinal)
            && pagination.Contains("aria-label=\"Previous page\"", StringComparison.Ordinal)
            && pagination.Contains("aria-label=\"Next page\"", StringComparison.Ordinal),
            "Pagination announces the current page and labels navigation links");
        var statuses = File.ReadAllText(Path.Combine(views, "Shared", "_SubmissionStatus.cshtml"))
            + File.ReadAllText(Path.Combine(views, "Shared", "_StatusBadge.cshtml"));
        Check(statuses.Contains("Under Review", StringComparison.Ordinal) && statuses.Contains("@Model.Text", StringComparison.Ordinal),
            "Submission status badges include readable text labels");

        var css = File.ReadAllText(Path.Combine(root, "wwwroot", "css", "site.css"));
        var script = File.ReadAllText(Path.Combine(root, "wwwroot", "js", "site.js"));
        Check(css.Contains(":focus-visible", StringComparison.Ordinal) && css.Contains("outline: 3px solid #176b63", StringComparison.Ordinal),
            "Keyboard focus remains visible with a high-contrast outline");
        Check(script.Contains("event.key === \"Escape\"", StringComparison.Ordinal)
            && script.Contains("showModal()", StringComparison.Ordinal) && script.Contains("cancelConfirmation?.focus()", StringComparison.Ordinal),
            "Dialogs and action disclosures support keyboard dismissal and focus handling");
        var actionMenu = File.ReadAllText(Path.Combine(views, "Shared", "_ActionMenu.cshtml"));
        Check(actionMenu.Contains("<details", StringComparison.Ordinal) && actionMenu.Contains("<summary", StringComparison.Ordinal)
            && actionMenu.Contains("aria-label=\"@rowActionsLabel\"", StringComparison.Ordinal),
            "Row action controls use a keyboard-operable disclosure with a contextual accessible name");
    }
}
