using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Models;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}

public static class PageRequest
{
    private static readonly int[] AllowedPageSizes = [10, 20, 50, 100];
    public static bool IsAllowedSize(int size) => AllowedPageSizes.Contains(size);
    public static int NormalizeSize(int? requestedSize, int defaultSize = 20) =>
        AllowedPageSizes.Contains(requestedSize ?? defaultSize) ? requestedSize ?? defaultSize
        : AllowedPageSizes.Contains(defaultSize) ? defaultSize : 20;

    public static int NormalizePage(int page, int totalCount, int pageSize)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        return Math.Clamp(page, 1, pages);
    }

    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(this IQueryable<T> orderedQuery, int page,
        int? pageSize = null, int defaultPageSize = 20, CancellationToken cancellationToken = default)
    {
        var size = NormalizeSize(pageSize, defaultPageSize);
        var total = await orderedQuery.CountAsync(cancellationToken);
        var current = NormalizePage(page, total, size);
        var items = await orderedQuery.Skip((current - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new(items, current, size, total);
    }
}

public sealed class AllowedPageSizeAttribute : System.ComponentModel.DataAnnotations.ValidationAttribute
{
    public AllowedPageSizeAttribute() => ErrorMessage = "Choose 10, 20, 50 or 100 results per page.";
    public override bool IsValid(object? value) => value is int size && PageRequest.IsAllowedSize(size);
}

public sealed record PaginationViewModel(string Action, string? Controller, int PageNumber, int PageSize,
    int TotalCount, string Label, IReadOnlyDictionary<string, string> RouteValues)
{
    public string PageParameterName { get; init; } = "page";
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
    public IReadOnlyList<int> PageNumbers
    {
        get
        {
            if (TotalPages <= 7) return Enumerable.Range(1, TotalPages).ToArray();
            var pages = new SortedSet<int> { 1, TotalPages };
            for (var p = Math.Max(1, PageNumber - 2); p <= Math.Min(TotalPages, PageNumber + 2); p++) pages.Add(p);
            return pages.ToArray();
        }
    }
}

public sealed record SearchBoxViewModel(string Name, string Label, string? Value, string Placeholder = "Search", int MaxLength = 200);
public sealed record FilterOptionViewModel(string Value, string Text);
public sealed record FilterFieldViewModel(string Name, string Label, string? Value, string Type = "text",
    string Placeholder = "", int MaxLength = 200, IReadOnlyList<FilterOptionViewModel>? Options = null);
public sealed record FilterPanelViewModel(string Action, string? Controller, string ResetAction,
    IReadOnlyList<FilterFieldViewModel> Fields, int PageSize, string SubmitLabel = "Apply filters");
public sealed record DataTableColumnViewModel(string Title, string? SortKey = null);
public sealed record DataTableCellViewModel(string Text, string? BadgeClass = null);
public sealed record DataTableActionViewModel(string Label, string Url, string CssClass = "dropdown-item",
    string? Controller = null, string? Action = null, string? RouteId = null, bool IsPost = false,
    string? HiddenName = null, string? HiddenValue = null);
public sealed record DataTableRowViewModel(IReadOnlyList<DataTableCellViewModel> Cells,
    IReadOnlyList<DataTableActionViewModel> Actions);
public sealed record DataTableViewModel(string Action, string? Controller, IReadOnlyList<DataTableColumnViewModel> Columns,
    IReadOnlyList<DataTableRowViewModel> Rows, string Sort, string Direction,
    IReadOnlyDictionary<string, string> RouteValues, string EmptyTitle, string EmptyMessage,
    string? ResetUrl = null);
public sealed record EmptyStateViewModel(string Title, string Message, string? ResetUrl = null);
public sealed record StatusBadgeViewModel(string Text, string CssClass);
public sealed record ErrorPageViewModel(string CorrelationId, bool IsAuthenticated);
public sealed record StatusCodePageViewModel(int StatusCode, string Title, string Message, string CorrelationId, bool IsAuthenticated);
