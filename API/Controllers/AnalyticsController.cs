using API.Data;
using API.Entities.OrderAggregate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[Authorize(Roles = "Admin")]
public class AnalyticsController(StoreContext context) : BaseApiController
{
    public record TimeSeriesPoint(DateTime Date, long Value);
    public record ProductCount(int ProductId, string Name, string? PictureUrl, long Count);
    public record ProductCorrelationPoint(int ProductId, string Name, string? PictureUrl, long Clicks, long Sales);
    public record ProductRating(int ProductId, string Name, string? PictureUrl, double AverageRating, long RatingsCount);

    [HttpGet("all-sales")]
    public async Task<ActionResult<List<ProductCount>>> GetAllSales(
        [FromQuery] string? categoryIds,
        [FromQuery] int take = 5000)
    {
        var catIds = ParseIds(categoryIds);

        var products = context.Products.AsNoTracking().AsQueryable();
        if (catIds.Count > 0)
        {
            var filteredProductIds = await FilterProductIdsByCategories(catIds);
            if (filteredProductIds == null || filteredProductIds.Count == 0)
                return new List<ProductCount>();

            products = products.Where(p => filteredProductIds.Contains(p.Id));
        }

        var res = await products
            .OrderByDescending(p => p.SalesCount)
            .ThenBy(p => p.Name)
            .Select(p => new ProductCount(p.Id, p.Name, p.PictureUrl, p.SalesCount))
            .Take(Math.Clamp(take, 1, 5000))
            .ToListAsync();

        return res;
    }

    [HttpGet("top-sold")]
    public async Task<ActionResult<List<ProductCount>>> GetTopSold(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? interval,
        [FromQuery] string? categoryIds,
        [FromQuery] int take = 30)
    {
        // For the ranking, we use paid orders in the period. If no range is given, fallback to all-time SalesCount.
        var catIds = ParseIds(categoryIds);

        if (from.HasValue || to.HasValue)
        {
            var (start, end) = NormalizeRange(from, to);
            var filteredProductIds = await FilterProductIdsByCategories(catIds);

            var query = context.Orders
                .AsNoTracking()
                .Where(o => o.OrderStatus != OrderStatus.Pending && o.OrderStatus != OrderStatus.PaymentFailed && o.OrderDate >= start && o.OrderDate <= end)
                .SelectMany(o => o.OrderItems)
                .Select(oi => new { oi.ItemOrdered.ProductId, oi.ItemOrdered.Name, oi.ItemOrdered.PictureUrl, oi.Quantity });

            if (filteredProductIds != null)
                query = query.Where(x => filteredProductIds.Contains(x.ProductId));

            // NOTE: OrderItem.ItemOrdered is an owned type. EF Core can struggle translating GroupBy over owned properties.
            // Pull the minimal rows to memory, then aggregate.
            var rows = await query.ToListAsync();

            var top = rows
                .GroupBy(x => new { x.ProductId, x.Name, x.PictureUrl })
                .Select(g => new ProductCount(g.Key.ProductId, g.Key.Name, g.Key.PictureUrl, g.Sum(x => (long)x.Quantity)))
                .OrderByDescending(x => x.Count)
                .Take(Math.Clamp(take, 1, 200))
                .ToList();

            return top;
        }

        // all-time: use Products.SalesCount
        var products = context.Products.AsNoTracking().AsQueryable();
        if (catIds.Count > 0)
        {
            var filteredProductIds = await FilterProductIdsByCategories(catIds);
            if (filteredProductIds == null || filteredProductIds.Count == 0)
                return new List<ProductCount>();

            products = products.Where(p => filteredProductIds.Contains(p.Id));
        }

        var res = await products
            .OrderByDescending(p => p.SalesCount)
            .ThenBy(p => p.Name)
            .Select(p => new ProductCount(p.Id, p.Name, p.PictureUrl, p.SalesCount))
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync();

        return res;
    }

    [HttpGet("sales-timeseries")]
    public async Task<ActionResult<List<TimeSeriesPoint>>> GetSalesTimeSeries(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string interval = "day",
        [FromQuery] string? categoryIds = null)
    {
        var (start, end) = NormalizeRange(from, to);
        var catIds = ParseIds(categoryIds);
        var filteredProductIds = await FilterProductIdsByCategories(catIds);

        var itemsQuery = context.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus != OrderStatus.Pending && o.OrderStatus != OrderStatus.PaymentFailed && o.OrderDate >= start && o.OrderDate <= end)
            .SelectMany(o => o.OrderItems.Select(oi => new { o.OrderDate, oi.ItemOrdered.ProductId, oi.Quantity }));

        if (filteredProductIds != null)
            itemsQuery = itemsQuery.Where(x => filteredProductIds.Contains(x.ProductId));

        var rows = await itemsQuery.ToListAsync();

        var points = rows
            .GroupBy(r => Bucket(r.OrderDate, interval))
            .Select(g => new TimeSeriesPoint(g.Key, g.Sum(x => (long)x.Quantity)))
            .OrderBy(x => x.Date)
            .ToList();

        return points;
    }

    [HttpGet("sales-amount-timeseries")]
    public async Task<ActionResult<List<TimeSeriesPoint>>> GetSalesAmountTimeSeries(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string interval = "day",
        [FromQuery] string? categoryIds = null)
    {
        var (start, end) = NormalizeRange(from, to);
        var catIds = ParseIds(categoryIds);
        var filteredProductIds = await FilterProductIdsByCategories(catIds);

        var itemsQuery = context.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus != OrderStatus.Pending && o.OrderStatus != OrderStatus.PaymentFailed && o.OrderDate >= start && o.OrderDate <= end)
            .SelectMany(o => o.OrderItems.Select(oi => new { o.OrderDate, oi.ItemOrdered.ProductId, oi.Quantity, oi.Price }));

        if (filteredProductIds != null)
            itemsQuery = itemsQuery.Where(x => filteredProductIds.Contains(x.ProductId));

        var rows = await itemsQuery.ToListAsync();

        var points = rows
            .GroupBy(r => Bucket(r.OrderDate, interval))
            .Select(g => new TimeSeriesPoint(g.Key, g.Sum(x => x.Price * (long)x.Quantity)))
            .OrderBy(x => x.Date)
            .ToList();

        return points;
    }

    [HttpGet("top-clicks")]
    public async Task<ActionResult<List<ProductCount>>> GetTopClicks(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? categoryIds,
        [FromQuery] int take = 30)
    {
        var (start, end) = NormalizeRange(from, to);
        var catIds = ParseIds(categoryIds);
        var filteredProductIds = await FilterProductIdsByCategories(catIds);

        var q = context.ProductClicks
            .AsNoTracking()
            .Where(c => c.CreatedAt >= start && c.CreatedAt <= end);

        if (filteredProductIds != null)
            q = q.Where(c => filteredProductIds.Contains(c.ProductId));

        var res = await q
            .GroupBy(c => c.ProductId)
            .Select(g => new { ProductId = g.Key, Count = g.LongCount() })
            .OrderByDescending(x => x.Count)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync();

        var productIds = res.Select(x => x.ProductId).ToList();
        var products = await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.PictureUrl })
            .ToListAsync();

        var map = products.ToDictionary(x => x.Id, x => x);

        return res
            .Select(x => map.TryGetValue(x.ProductId, out var p)
                ? new ProductCount(x.ProductId, p.Name, p.PictureUrl, x.Count)
                : new ProductCount(x.ProductId, $"#{x.ProductId}", null, x.Count))
            .ToList();
    }

    [HttpGet("clicks-timeseries")]
    public async Task<ActionResult<List<TimeSeriesPoint>>> GetClicksTimeSeries(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string interval = "day",
        [FromQuery] string? categoryIds = null)
    {
        var (start, end) = NormalizeRange(from, to);
        var catIds = ParseIds(categoryIds);
        var filteredProductIds = await FilterProductIdsByCategories(catIds);

        var q = context.ProductClicks
            .AsNoTracking()
            .Where(c => c.CreatedAt >= start && c.CreatedAt <= end);

        if (filteredProductIds != null)
            q = q.Where(c => filteredProductIds.Contains(c.ProductId));

        var rows = await q.Select(c => new { c.CreatedAt }).ToListAsync();

        var points = rows
            .GroupBy(r => Bucket(r.CreatedAt, interval))
            .Select(g => new TimeSeriesPoint(g.Key, g.LongCount()))
            .OrderBy(x => x.Date)
            .ToList();

        return points;
    }

    [HttpGet("correlation")]
    public async Task<ActionResult<List<ProductCorrelationPoint>>> GetClicksSalesCorrelation(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? categoryIds,
        [FromQuery] int take = 60)
    {
        var (start, end) = NormalizeRange(from, to);
        var catIds = ParseIds(categoryIds);
        var filteredProductIds = await FilterProductIdsByCategories(catIds);

        // Clicks per product
        var clicksQ = context.ProductClicks
            .AsNoTracking()
            .Where(c => c.CreatedAt >= start && c.CreatedAt <= end);

        if (filteredProductIds != null)
            clicksQ = clicksQ.Where(c => filteredProductIds.Contains(c.ProductId));

        var clicks = await clicksQ
            .GroupBy(c => c.ProductId)
            .Select(g => new { ProductId = g.Key, Clicks = g.LongCount() })
            .ToListAsync();

        // Sales per product
        var salesItemsQ = context.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus != OrderStatus.Pending && o.OrderStatus != OrderStatus.PaymentFailed && o.OrderDate >= start && o.OrderDate <= end)
            .SelectMany(o => o.OrderItems.Select(oi => new { oi.ItemOrdered.ProductId, oi.Quantity }));

        if (filteredProductIds != null)
            salesItemsQ = salesItemsQ.Where(x => filteredProductIds.Contains(x.ProductId));

        // Same translation limitation as GetTopSold: aggregate in memory.
        var salesRows = await salesItemsQ.ToListAsync();
        var sales = salesRows
            .GroupBy(x => x.ProductId)
            .Select(g => new { ProductId = g.Key, Sales = g.Sum(x => (long)x.Quantity) })
            .ToList();

        var clickMap = clicks.ToDictionary(x => x.ProductId, x => x.Clicks);
        var salesMap = sales.ToDictionary(x => x.ProductId, x => x.Sales);

        var productIds = clickMap.Keys.Union(salesMap.Keys).ToList();

        // For large ranges, keep it bounded
        var topProductIds = productIds
            .OrderByDescending(id => (clickMap.TryGetValue(id, out var c) ? c : 0) + (salesMap.TryGetValue(id, out var s) ? s : 0) * 5)
            .Take(Math.Clamp(take, 10, 400))
            .ToList();

        var products = await context.Products
            .AsNoTracking()
            .Where(p => topProductIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.PictureUrl })
            .ToListAsync();

        var pMap = products.ToDictionary(x => x.Id, x => x);

        var result = topProductIds.Select(id =>
        {
            pMap.TryGetValue(id, out var p);
            return new ProductCorrelationPoint(
                id,
                p?.Name ?? $"#{id}",
                p?.PictureUrl,
                clickMap.TryGetValue(id, out var c) ? c : 0,
                salesMap.TryGetValue(id, out var s) ? s : 0);
        }).ToList();

        return result;
    }

    [HttpGet("top-comments")]
    public async Task<ActionResult<List<ProductCount>>> GetTopComments(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? categoryIds,
        [FromQuery] int take = 30)
    {
        var (start, end) = NormalizeRange(from, to);
        var catIds = ParseIds(categoryIds);
        var filteredProductIds = await FilterProductIdsByCategories(catIds);

        var q = context.ProductReviews
            .AsNoTracking()
            .Where(r => r.CreatedAt >= start && r.CreatedAt <= end);

        if (filteredProductIds != null)
            q = q.Where(r => filteredProductIds.Contains(r.ProductId));

        var res = await q
            .GroupBy(r => r.ProductId)
            .Select(g => new { ProductId = g.Key, Count = g.LongCount() })
            .OrderByDescending(x => x.Count)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync();

        var productIds = res.Select(x => x.ProductId).ToList();
        var products = await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.PictureUrl })
            .ToListAsync();

        var map = products.ToDictionary(x => x.Id, x => x);

        return res
            .Select(x => map.TryGetValue(x.ProductId, out var p)
                ? new ProductCount(x.ProductId, p.Name, p.PictureUrl, x.Count)
                : new ProductCount(x.ProductId, $"#{x.ProductId}", null, x.Count))
            .ToList();
    }

    [HttpGet("top-rated")]
    public async Task<ActionResult<List<ProductRating>>> GetTopRated(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? categoryIds,
        [FromQuery] int take = 30)
    {
        var (start, end) = NormalizeRange(from, to);
        var catIds = ParseIds(categoryIds);
        var filteredProductIds = await FilterProductIdsByCategories(catIds);

        var q = context.ProductReviews
            .AsNoTracking()
            .Where(r => r.CreatedAt >= start && r.CreatedAt <= end);

        if (filteredProductIds != null)
            q = q.Where(r => filteredProductIds.Contains(r.ProductId));

        var res = await q
            .GroupBy(r => r.ProductId)
            .Select(g => new { ProductId = g.Key, AverageRating = g.Average(x => (double)x.Rating), RatingsCount = g.LongCount() })
            .OrderByDescending(x => x.AverageRating)
            .ThenByDescending(x => x.RatingsCount)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync();

        var productIds = res.Select(x => x.ProductId).ToList();
        var products = await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.PictureUrl })
            .ToListAsync();

        var map = products.ToDictionary(x => x.Id, x => x);

        return res
            .Select(x => map.TryGetValue(x.ProductId, out var p)
                ? new ProductRating(x.ProductId, p.Name, p.PictureUrl, x.AverageRating, x.RatingsCount)
                : new ProductRating(x.ProductId, $"#{x.ProductId}", null, x.AverageRating, x.RatingsCount))
            .ToList();
    }

    private static (DateTime start, DateTime end) NormalizeRange(DateTime? from, DateTime? to)
    {
        // Default: last 30 days
        var end = (to ?? DateTime.UtcNow).ToUniversalTime();
        var start = (from ?? end.AddDays(-30)).ToUniversalTime();
        if (start > end) (start, end) = (end, start);
        return (start, end);
    }

    private static List<int> ParseIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new List<int>();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
            .Where(v => v > 0)
            .Distinct()
            .ToList();
    }

    private async Task<List<int>?> FilterProductIdsByCategories(List<int> categoryIds)
    {
        if (categoryIds == null || categoryIds.Count == 0) return null;

        // Avoid navigation-based filtering here: some EF Core versions/providers fail to translate
        // skip-navigation queries like `p.Categories.Any(...)` and produce "could not be translated".
        // Query the implicit many-to-many join table directly instead.
        return await context.Set<Dictionary<string, object>>("CategoryProduct")
            .AsNoTracking()
            .Where(cp => categoryIds.Contains(EF.Property<int>(cp, "CategoriesId")))
            .Select(cp => EF.Property<int>(cp, "ProductsId"))
            .Distinct()
            .ToListAsync();
    }

    private static DateTime Bucket(DateTime dt, string interval)
    {
        var d = dt.ToUniversalTime();
        interval = (interval ?? "day").Trim().ToLowerInvariant();

        return interval switch
        {
            "year" => new DateTime(d.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "month" => new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            "week" => StartOfWeekUtc(d.Date),
            _ => d.Date
        };
    }

    private static DateTime StartOfWeekUtc(DateTime date)
    {
        // Monday-start ISO-ish week
        var day = date.DayOfWeek;
        var diff = (7 + (int)day - (int)DayOfWeek.Monday) % 7;
        var monday = date.AddDays(-diff);
        return DateTime.SpecifyKind(monday, DateTimeKind.Utc);
    }
}