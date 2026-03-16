using API.Data;
using API.DTOs;
using API.Entities;
using API.Entities.OrderAggregate;
using API.Extensions;
using API.RequestHelpers;
using API.Services;
using AutoMapper;
using System;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace API.Controllers
{
    public record ProductClickDto(string SessionId);

    public class ProductsController(
        StoreContext context,
        IMapper mapper,
        ImageService imageService,
        UserManager<User> userManager,
        IEmailService emailService,
        IOptions<EmailSettings> emailOptions,
        ILogger<ProductsController> logger,
        INotificationService notificationService) : BaseApiController
    {
        private sealed class VariantUpsert
        {
            public int? Id { get; set; }
            public string? Key { get; set; }
            public string? Color { get; set; }
            public int QuantityInStock { get; set; }
            public decimal? PriceOverride { get; set; }
            public string? DescriptionOverride { get; set; }
        }

        private sealed class PropertyUpsert
        {
            public int? CategoryId { get; set; }
            public string? Name { get; set; }
            public string? Value { get; set; }
        }

        [HttpGet]
        public async Task<ActionResult<List<Product>>> GetProducts(
            [FromQuery] ProductParams productParams)
        {
            var isAdmin = User?.Identity?.IsAuthenticated == true && User.IsInRole("Admin");

            // Start with base query applying search and scalar filters.
            // Sorting is applied later because some sorts may depend on other tables (e.g. clicks).
            var query = context.Products
                .Search(productParams.SearchTerm)
                .Filter(
                    productParams.Generos,
                    productParams.Anos,
                    productParams.HasDiscount,
                    productParams.Marcas,
                    productParams.Modelos,
                    productParams.Tipos,
                    productParams.Capacidades,
                    productParams.Cores,
                    productParams.Materiais,
                    productParams.Tamanhos)
                .AsQueryable();

            // Customers should only see published products.
            // We keep Active for internal/admin usage, but avoid accidentally hiding the whole catalog.
            if (!isAdmin)
            {
                query = query.Where(p => p.IsPublished);
            }

            // Parse category and campaign id strings into lists
            var categoryList = new List<int>();
            if (!string.IsNullOrEmpty(productParams.CategoryIds))
            {
                categoryList.AddRange(productParams.CategoryIds.Split(",", StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => { if (int.TryParse(s.Trim(), out var v)) return v; return -1; })
                    .Where(v => v > 0));
            }

            var campaignList = new List<int>();
            if (!string.IsNullOrEmpty(productParams.CampaignIds))
            {
                campaignList.AddRange(productParams.CampaignIds.Split(",", StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => { if (int.TryParse(s.Trim(), out var v)) return v; return -1; })
                    .Where(v => v > 0));
            }

            // If category filters are present, fetch matching product ids via the relationship
            // This avoids EF trying to materialize collection navigations during translation.
            if (categoryList.Count > 0)
            {
                var productIds = await context.Categories
                    .Where(c => categoryList.Contains(c.Id))
                    .SelectMany(c => c.Products!.Select(p => p.Id))
                    .Distinct()
                    .ToListAsync();

                query = query.Where(p => productIds.Contains(p.Id));
            }

            if (campaignList.Count > 0)
            {
                var productIds = await context.Campaigns
                    .Where(c => campaignList.Contains(c.Id))
                    .SelectMany(c => c.Products!.Select(p => p.Id))
                    .Distinct()
                    .ToListAsync();

                query = query.Where(p => productIds.Contains(p.Id));
            }

            // Apply sorting after filters.
            // Support click-based sorting for "Mais procurados".
            if (!string.IsNullOrWhiteSpace(productParams.OrderBy) &&
                (productParams.OrderBy == "clicksDesc" || productParams.OrderBy == "clicks"))
            {
                var clickCounts = context.ProductClicks
                    .AsNoTracking()
                    .GroupBy(c => c.ProductId)
                    .Select(g => new { ProductId = g.Key, Clicks = g.LongCount() });

                var joined = query.GroupJoin(
                    clickCounts,
                    p => p.Id,
                    c => c.ProductId,
                    (p, cg) => new { Product = p, Clicks = cg.Select(x => x.Clicks).FirstOrDefault() });

                query = productParams.OrderBy == "clicksDesc"
                    ? joined.OrderByDescending(x => x.Clicks).ThenBy(x => x.Product.Name).Select(x => x.Product)
                    : joined.OrderBy(x => x.Clicks).ThenBy(x => x.Product.Name).Select(x => x.Product);
            }
            else
            {
                query = query.Sort(productParams.OrderBy);
            }

            // include navigation properties after filtering/sorting to keep EF translation simple
            query = query.Include(p => p.Campaigns).Include(p => p.Categories).Include(p => p.Variants);

            var products = await PagedList<Product>.ToPagedList(query,
                productParams.PageNumber, productParams.PageSize);

            Response.AddPaginationHeader(products.Metadata);

            // if user is authenticated, mark products that are in their favorites
            if (User?.Identity?.IsAuthenticated ?? false)
            {
                var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(userId))
                {
                    var favIds = await context.Favorites
                        .Where(f => f.UserId == userId)
                        .Select(f => f.ProductId)
                        .ToListAsync();

                    foreach (var p in products)
                    {
                        p.IsFavorite = favIds.Contains(p.Id);
                    }
                }
            }

            return products;
        }

        [HttpGet("{id}")] // api/products/2
        public async Task<ActionResult<Product>> GetProduct(int id)
        {
            var isAdmin = User?.Identity?.IsAuthenticated == true && User.IsInRole("Admin");

            // include campaigns and categories so the returned product contains related data
            var product = await context.Products
                .Include(p => p.Campaigns)
                .Include(p => p.Categories)
                .Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (product == null) return NotFound();

            if (!isAdmin)
            {
                if (!product.IsPublished) return NotFound();
            }

            if (User?.Identity?.IsAuthenticated ?? false)
            {
                var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(userId))
                {
                    var exists = await context.Favorites.AnyAsync(f => f.UserId == userId && f.ProductId == product.Id);
                    product.IsFavorite = exists;
                }
            }

            return product;
        }

        [HttpGet("{id:int}/reviews")]
        [AllowAnonymous]
        public async Task<ActionResult<List<ProductReviewDto>>> GetProductReviews(int id, CancellationToken ct)
        {
            var exists = await context.Products.AsNoTracking().AnyAsync(p => p.Id == id, ct);
            if (!exists) return NotFound();

            var reviews = await context.ProductReviews
                .AsNoTracking()
                .Where(r => r.ProductId == id)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new ProductReviewDto
                {
                    Id = r.Id,
                    ProductId = r.ProductId,
                    OrderId = r.OrderId,
                    BuyerEmail = r.BuyerEmail,
                    Rating = r.Rating,
                    Comment = r.Comment,
                    AdminReply = r.AdminReply,
                    AdminRepliedAt = r.AdminRepliedAt,
                    CreatedAt = r.CreatedAt
                })
                .ToListAsync(ct);

            return reviews;
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{id:int}/reviews/{reviewId:int}")]
        public async Task<ActionResult> DeleteProductReview(int id, int reviewId, CancellationToken ct)
        {
            var product = await context.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (product == null) return NotFound();

            var review = await context.ProductReviews
                .FirstOrDefaultAsync(r => r.Id == reviewId && r.ProductId == id, ct);

            if (review == null) return NotFound();

            review.IsDeleted = true;
            review.DeletedAt = DateTime.UtcNow;
            review.DeletedByEmail = User.GetEmail();
            review.DeletedReason = "Moderated";

            var saved = await context.SaveChangesAsync(ct) > 0;
            if (!saved) return BadRequest("Problem deleting review");

            var stats = await context.ProductReviews
                .AsNoTracking()
                .Where(r => r.ProductId == id)
                .GroupBy(_ => 1)
                .Select(g => new { Avg = g.Average(x => x.Rating), Count = g.Count() })
                .FirstOrDefaultAsync(ct);

            product.AverageRating = stats == null ? null : Math.Round(stats.Avg, 2);
            product.RatingsCount = stats?.Count ?? 0;
            await context.SaveChangesAsync(ct);

            return NoContent();
        }

        [HttpPost("{id:int}/reviews")]
        [Authorize]
        public async Task<ActionResult<ProductReviewDto>> CreateProductReview(int id, [FromBody] CreateProductReviewDto dto, CancellationToken ct)
        {
            if (dto == null) return BadRequest("Dados inválidos");

            var rating = dto.Rating;
            if (rating < 1 || rating > 5) return BadRequest("Classificação inválida");

            var comment = (dto.Comment ?? string.Empty).Trim();
            if (comment.Length < 3) return BadRequest("Comentário demasiado curto");
            if (comment.Length > 1000) return BadRequest("Comentário demasiado longo");

            var product = await context.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (product == null) return NotFound();

            var email = User.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Unauthorized();

            // Only allow reviews from real buyers, after delivery/review request.
            var eligibleOrder = await context.Orders
                .AsNoTracking()
                .Where(o => o.BuyerEmail == email)
                .Where(o => o.OrderStatus == OrderStatus.Delivered || o.OrderStatus == OrderStatus.ReviewRequested || o.OrderStatus == OrderStatus.Completed)
                .Where(o => o.OrderItems.Any(oi => oi.ItemOrdered.ProductId == id))
                .OrderByDescending(o => o.OrderDate)
                .FirstOrDefaultAsync(ct);

            if (eligibleOrder == null)
                return BadRequest("Só pode avaliar produtos que comprou e recebeu");

            var already = await context.ProductReviews
                .AsNoTracking()
                .AnyAsync(r => r.ProductId == id && r.BuyerEmail == email && r.OrderId == eligibleOrder.Id, ct);

            if (already) return BadRequest("Já avaliou este produto nesta encomenda");

            var review = new ProductReview
            {
                ProductId = id,
                OrderId = eligibleOrder.Id,
                BuyerEmail = email,
                Rating = rating,
                Comment = comment,
                CreatedAt = DateTime.UtcNow
            };

            context.ProductReviews.Add(review);
            await context.SaveChangesAsync(ct);

            // Update product cached rating stats (AverageRating/RatingsCount)
            var stats = await context.ProductReviews
                .AsNoTracking()
                .Where(r => r.ProductId == id)
                .GroupBy(_ => 1)
                .Select(g => new { Avg = g.Average(x => x.Rating), Count = g.Count() })
                .FirstOrDefaultAsync(ct);

            product.AverageRating = stats == null ? null : Math.Round(stats.Avg, 2);
            product.RatingsCount = stats?.Count ?? 0;
            await context.SaveChangesAsync(ct);

            await TryNotifyAdminsProductReview(product, review, ct);

            var dtoOut = new ProductReviewDto
            {
                Id = review.Id,
                ProductId = review.ProductId,
                OrderId = review.OrderId,
                BuyerEmail = review.BuyerEmail,
                Rating = review.Rating,
                Comment = review.Comment,
                AdminReply = review.AdminReply,
                AdminRepliedAt = review.AdminRepliedAt,
                CreatedAt = review.CreatedAt
            };

            return Ok(dtoOut);
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{id:int}/reviews/{reviewId:int}/reply")]
        public async Task<ActionResult<ProductReviewDto>> ReplyToProductReview(int id, int reviewId, [FromBody] ReplyTextDto dto, CancellationToken ct)
        {
            var reply = (dto?.Reply ?? string.Empty).Trim();
            if (reply.Length < 3) return BadRequest("Resposta demasiado curta");
            if (reply.Length > 2000) return BadRequest("Resposta demasiado longa");

            var review = await context.ProductReviews
                .FirstOrDefaultAsync(r => r.Id == reviewId && r.ProductId == id, ct);

            if (review == null) return NotFound();

            review.AdminReply = reply;
            review.AdminRepliedAt = DateTime.UtcNow;

            var saved = await context.SaveChangesAsync(ct) > 0;
            if (!saved) return BadRequest("Problem saving reply");

            await notificationService.TryCreateForEmailAsync(
                review.BuyerEmail,
                "Resposta à sua avaliação",
                $"Respondemos à sua avaliação do produto #{review.ProductId}.",
                $"/catalog/{review.ProductId}",
                ct);

            await TrySendProductReviewReplyEmailAsync(review, reply);

            return Ok(new ProductReviewDto
            {
                Id = review.Id,
                ProductId = review.ProductId,
                OrderId = review.OrderId,
                BuyerEmail = review.BuyerEmail,
                Rating = review.Rating,
                Comment = review.Comment,
                AdminReply = review.AdminReply,
                AdminRepliedAt = review.AdminRepliedAt,
                CreatedAt = review.CreatedAt
            });
        }

        private async Task TrySendProductReviewReplyEmailAsync(ProductReview review, string reply)
        {
            try
            {
                var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
                var productUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/catalog/{review.ProductId}";

                var product = await context.Products
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == review.ProductId);

                var productName = product?.Name ?? $"Produto #{review.ProductId}";
                var productDesc = (product?.Subtitle ?? product?.Description) ?? string.Empty;
                var productCard = EmailTemplate.RenderProductHighlight(
                    title: "Produto",
                    name: productName,
                    imageUrl: product?.PictureUrl,
                    description: productDesc,
                    productUrl: string.IsNullOrWhiteSpace(productUrl) ? null : productUrl,
                    ctaText: "Ver produto");

                var subject = $"Resposta à sua avaliação (Produto #{review.ProductId})";
                var html = $"""
                    <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                                            <h2>Resposta à sua avaliação</h2>
                                            {productCard}
                                            <p style='margin:0 0 8px'>Recebeu uma resposta da nossa equipa:</p>
                                            <div style='white-space: pre-wrap; border: 1px solid #e5e7eb; padding: 12px; border-radius: 12px; background: #f9fafb'>
                                                {System.Net.WebUtility.HtmlEncode(reply)}
                                            </div>
                    </div>
                    """;

                await emailService.SendEmailAsync(review.BuyerEmail, subject, html);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send product review reply email for review {ReviewId}", review.Id);
            }
        }

        private async Task TryNotifyAdminsProductReview(Product product, ProductReview review, CancellationToken ct)
        {
            try
            {
                var admins = await userManager.GetUsersInRoleAsync("Admin");
                var adminEmails = admins
                    .Select(a => (a.Email ?? string.Empty).Trim())
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (adminEmails.Count == 0) return;

                foreach (var to in adminEmails)
                {
                    await notificationService.TryCreateForEmailAsync(
                        to,
                        "Nova avaliação",
                        $"Nova avaliação no produto #{product.Id}.",
                        $"/catalog/{product.Id}",
                        ct);
                }

                var frontend = (emailOptions.Value.FrontendUrl ?? string.Empty).TrimEnd('/');
                var productUrl = string.IsNullOrWhiteSpace(frontend) ? string.Empty : $"{frontend}/catalog/{product.Id}";

                var productCard = EmailTemplate.RenderProductHighlight(
                    title: "Novo review",
                    name: product.Name,
                    imageUrl: product.PictureUrl,
                    description: product.Subtitle ?? product.Description,
                    productUrl: string.IsNullOrWhiteSpace(productUrl) ? null : productUrl,
                    ctaText: "Abrir produto");

                var subject = $"[Avaliação] Produto #{product.Id} - {product.Name}";
                var html = $"""
                    <div style='font-family: Arial, sans-serif; line-height: 1.5'>
                                            <h2>Nova avaliação</h2>
                                            {productCard}
                      <p><strong>Cliente:</strong> {System.Net.WebUtility.HtmlEncode(review.BuyerEmail)}</p>
                      <p><strong>Classificação:</strong> {review.Rating}/5</p>
                      <p><strong>Comentário:</strong></p>
                                            <div style='white-space: pre-wrap; border: 1px solid #e5e7eb; padding: 12px; border-radius: 12px; background: #f9fafb'>
                        {System.Net.WebUtility.HtmlEncode(review.Comment)}
                      </div>
                    </div>
                    """;

                foreach (var to in adminEmails)
                {
                    await emailService.SendEmailAsync(to, subject, html, replyToEmail: review.BuyerEmail);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to notify admins about product review for product {ProductId}", product.Id);
            }
        }

        // Track a view/click on the product details page.
        // Anonymous allowed; we dedupe by (ProductId, SessionId) within a short window.
        [HttpPost("{id}/click")]
        public async Task<IActionResult> TrackProductClick(int id, [FromBody] ProductClickDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.SessionId)) return BadRequest("SessionId is required");

            // Ensure product exists (avoid FK failures)
            var exists = await context.Products.AnyAsync(p => p.Id == id);
            if (!exists) return NotFound();

            var windowStart = DateTime.UtcNow.AddMinutes(-5);
            var alreadyCounted = await context.ProductClicks.AnyAsync(c =>
                c.ProductId == id &&
                c.SessionId == dto.SessionId &&
                c.CreatedAt >= windowStart);

            if (alreadyCounted) return Ok();

            var userId = User?.Identity?.IsAuthenticated ?? false
                ? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                : null;

            context.ProductClicks.Add(new ProductClick
            {
                ProductId = id,
                SessionId = dto.SessionId.Trim(),
                UserId = userId,
                UserAgent = Request.Headers.UserAgent.ToString(),
                CreatedAt = DateTime.UtcNow
            });

            await context.SaveChangesAsync();
            return Ok();
        }

        [HttpGet("filters")]
        public async Task<IActionResult> GetFilters()
        {
            var isAdmin = User?.Identity?.IsAuthenticated == true && User.IsInRole("Admin");

            // return distinct generos and publication years available for filters
            var generos = await context.Products
                .Where(p => p.Genero != null)
                .Select(p => p.Genero)
                .Distinct()
                .ToListAsync();

            var anos = await context.Products
                .Where(p => p.PublicationYear.HasValue)
                .Select(p => p.PublicationYear!.Value)
                .Distinct()
                .OrderByDescending(x => x)
                .ToListAsync();

            // include available categories and campaigns for filter UI
            var categories = await context.Categories
                .Where(c => c.IsActive)
                // Customers: only categories with published products.
                // Admin: categories with any products (drafts included).
                // Soft-deleted products are excluded by the Product global query filter.
                .Where(c => isAdmin ? c.Products!.Any() : c.Products!.Any(p => p.IsPublished))
                .Select(c => new { c.Id, c.Name, c.Slug, c.IsActive, c.ParentCategoryId })
                .ToListAsync();

            var campaigns = await context.Campaigns
                .Where(c => c.IsActive)
                .Where(c => isAdmin ? c.Products!.Any() : c.Products!.Any(p => p.IsPublished))
                .Select(c => new { c.Id, c.Name, c.Slug, c.IsActive })
                .ToListAsync();

            var marcas = await context.Products
                .Where(p => p.Marca != null)
                .Select(p => p.Marca)
                .Distinct()
                .ToListAsync();

            var modelos = await context.Products
                .Where(p => p.Modelo != null)
                .Select(p => p.Modelo)
                .Distinct()
                .ToListAsync();

            var tipos = await context.Products
                .Where(p => p.Tipo != null)
                .Select(p => p.Tipo)
                .Distinct()
                .ToListAsync();

            var capacidades = await context.Products
                .Where(p => p.Capacidade != null)
                .Select(p => p.Capacidade)
                .Distinct()
                .ToListAsync();

            var coresFromProducts = await context.Products
                .Where(p => p.Cor != null)
                .Select(p => p.Cor)
                .Distinct()
                .ToListAsync();

            var coresFromVariants = await context.ProductVariants
                .Select(v => v.Color)
                .Distinct()
                .ToListAsync();

            var cores = coresFromProducts
                .Concat(coresFromVariants)
                .Where(c => c != null)
                .Distinct()
                .ToList();

            var materiais = await context.Products
                .Where(p => p.Material != null)
                .Select(p => p.Material)
                .Distinct()
                .ToListAsync();

            var tamanhos = await context.Products
                .Where(p => p.Tamanho != null)
                .Select(p => p.Tamanho)
                .Distinct()
                .ToListAsync();

            return Ok(new { generos, anos, categories, campaigns, marcas, modelos, tipos, capacidades, cores, materiais, tamanhos });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<ActionResult<Product>> CreateProduct([FromForm] CreateProductDto productDto)
        {

            var product = mapper.Map<Product>(productDto);
            product.IsPublished = false;

            if (productDto.File != null)
            {
                var imageResult = await imageService.AddImageAsync(productDto.File);

                if (imageResult.Error != null)
                {
                    return BadRequest(imageResult.Error.Message);
                }

                product.PictureUrl = imageResult.SecureUrl.AbsoluteUri;
                product.PublicId = imageResult.PublicId;
            }

            // handle multiple secondary image uploads
            if (productDto.SecondaryFiles != null && productDto.SecondaryFiles.Any())
            {
                product.SecondaryImages ??= new List<string>();
                product.SecondaryImagePublicIds ??= new List<string>();
                foreach (var file in productDto.SecondaryFiles)
                {
                    var secResult = await imageService.AddImageAsync(file);
                    if (secResult.Error != null)
                        return BadRequest(secResult.Error.Message);

                    product.SecondaryImages.Add(secResult.SecureUrl.AbsoluteUri);
                    product.SecondaryImagePublicIds.Add(secResult.PublicId);
                }
            }

            // Ensure publication year from form ('anoPublicacao') is applied in case model binding missed it
            IFormCollection? formData = null;
            if (Request.HasFormContentType)
            {
                formData = await Request.ReadFormAsync();
                if (formData.TryGetValue("anoPublicacao", out var yearValues))
                {
                    if (int.TryParse(yearValues.FirstOrDefault(), out var year))
                    {
                        product.PublicationYear = year;
                    }
                }
            }

            // Handle variants (colors)
            var variantsJson = (productDto.VariantsJson ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(variantsJson))
            {
                List<VariantUpsert>? variants;
                try
                {
                    variants = JsonSerializer.Deserialize<List<VariantUpsert>>(variantsJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return BadRequest("Invalid variantsJson");
                }

                variants ??= [];

                var files = productDto.VariantFiles ?? [];
                var keys = productDto.VariantFileKeys ?? [];
                var fileByKey = new Dictionary<string, IFormFile>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < Math.Min(files.Count, keys.Count); i++)
                {
                    var k = (keys[i] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(k)) continue;
                    fileByKey[k] = files[i];
                }

                product.Variants = [];

                foreach (var v in variants)
                {
                    var color = (v.Color ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(color))
                        return BadRequest("Variant color is required");

                    var variant = new ProductVariant
                    {
                        Product = product,
                        Color = color,
                        QuantityInStock = Math.Max(0, v.QuantityInStock),
                        PriceOverride = v.PriceOverride,
                        DescriptionOverride = string.IsNullOrWhiteSpace(v.DescriptionOverride) ? null : v.DescriptionOverride.Trim(),
                        CreatedAt = DateTime.UtcNow
                    };

                    var key = (v.Key ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(key) && fileByKey.TryGetValue(key, out var file) && file != null)
                    {
                        var imageResult = await imageService.AddImageAsync(file);
                        if (imageResult.Error != null) return BadRequest(imageResult.Error.Message);

                        variant.PictureUrl = imageResult.SecureUrl.AbsoluteUri;
                        variant.PublicId = imageResult.PublicId;
                    }

                    product.Variants.Add(variant);
                }

                // Keep product-level stock as total for existing UIs
                product.QuantityInStock = product.Variants.Sum(x => x.QuantityInStock);
            }

            // assign campaigns and categories if provided
            if (productDto.CampaignIds != null && productDto.CampaignIds.Any())
            {
                product.Campaigns = await context.Campaigns
                    .Where(c => productDto.CampaignIds.Contains(c.Id))
                    .ToListAsync();
            }

            if (productDto.CategoryIds != null && productDto.CategoryIds.Any())
            {
                product.Categories = await context.Categories
                    .Where(c => productDto.CategoryIds.Contains(c.Id))
                    .ToListAsync();
            }

            // Custom properties (free-form key/value pairs)
            var propertiesJson = (productDto.PropertiesJson ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(propertiesJson))
            {
                List<PropertyUpsert>? props;
                try
                {
                    props = JsonSerializer.Deserialize<List<PropertyUpsert>>(propertiesJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return BadRequest("Invalid propertiesJson");
                }

                props ??= [];

                var normalized = props
                    .Select(p => new
                    {
                        categoryId = p.CategoryId,
                        name = (p.Name ?? string.Empty).Trim(),
                        value = (p.Value ?? string.Empty).Trim()
                    })
                    .Where(p => !string.IsNullOrWhiteSpace(p.name))
                    .ToList();

                product.CustomPropertiesJson = normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
            }

            context.Products.Add(product);

            var result = await context.SaveChangesAsync() > 0;

            if (result) return CreatedAtAction(nameof(GetProduct), new { Id = product.Id }, product);

            return BadRequest(new { message = "Problem creating new product" });
        }

        [Authorize(Roles = "Admin")]
        [HttpPut]
        public async Task<ActionResult> UpdateProduct([FromForm] UpdateProductDto updateProductDto)
        {
            var product = await context.Products
                .Include(p => p.Campaigns)
                .Include(p => p.Categories)
                .Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.Id == updateProductDto.Id);

            if (product == null) return NotFound();


            mapper.Map(updateProductDto, product);

            if (updateProductDto.File != null) 
            {
                var imageResult = await imageService.AddImageAsync(updateProductDto.File);

                if (imageResult.Error != null)
                    return BadRequest(imageResult.Error.Message);

                if (!string.IsNullOrEmpty(product.PublicId))
                    await imageService.DeleteImageAsync(product.PublicId);

                product.PictureUrl = imageResult.SecureUrl.AbsoluteUri;
                product.PublicId = imageResult.PublicId;
            }

            // handle newly uploaded secondary images
            if (updateProductDto.SecondaryFiles != null && updateProductDto.SecondaryFiles.Any())
            {
                product.SecondaryImages ??= new List<string>();
                product.SecondaryImagePublicIds ??= new List<string>();
                foreach (var file in updateProductDto.SecondaryFiles)
                {
                    var secResult = await imageService.AddImageAsync(file);
                    if (secResult.Error != null)
                        return BadRequest(secResult.Error.Message);

                    product.SecondaryImages.Add(secResult.SecureUrl.AbsoluteUri);
                    product.SecondaryImagePublicIds.Add(secResult.PublicId);
                }
            }

            // handle removal of existing secondary images (by publicId or URL)
            if (updateProductDto.RemovedSecondaryImages != null && updateProductDto.RemovedSecondaryImages.Any())
            {
                product.SecondaryImages = product.SecondaryImages ?? new List<string>();
                product.SecondaryImagePublicIds = product.SecondaryImagePublicIds ?? new List<string>();

                // copy to avoid mutation during iteration
                var toRemove = updateProductDto.RemovedSecondaryImages.ToList();

                foreach (var identifier in toRemove)
                {
                    // if identifier matches a publicId we have stored, delete via Cloudinary
                    if (product.SecondaryImagePublicIds != null && product.SecondaryImagePublicIds.Contains(identifier))
                    {
                        try
                        {
                            await imageService.DeleteImageAsync(identifier);
                        }
                        catch { /* swallow deletion errors, proceed to remove from lists */ }

                        var idx = product.SecondaryImagePublicIds.IndexOf(identifier);
                        if (idx >= 0 && product.SecondaryImages != null && idx < product.SecondaryImages.Count)
                        {
                            product.SecondaryImages.RemoveAt(idx);
                        }
                        product.SecondaryImagePublicIds.Remove(identifier);
                    }
                    else
                    {
                        // treat identifier as URL - remove URL and any matching publicId at same index
                        if (product.SecondaryImages != null && product.SecondaryImages.Contains(identifier))
                        {
                            var idx = product.SecondaryImages.IndexOf(identifier);
                            // if there's a publicId at same index, attempt deletion
                            if (product.SecondaryImagePublicIds != null && idx >= 0 && idx < product.SecondaryImagePublicIds.Count)
                            {
                                var pid = product.SecondaryImagePublicIds[idx];
                                if (!string.IsNullOrEmpty(pid))
                                {
                                    try { await imageService.DeleteImageAsync(pid); } catch { }
                                }
                                product.SecondaryImagePublicIds.RemoveAt(idx);
                            }
                            product.SecondaryImages.Remove(identifier);
                        }
                    }
                }
            }

            // Ensure publication year from form ('anoPublicacao') is applied in case model binding missed it
            IFormCollection? formData = null;
            if (Request.HasFormContentType)
            {
                formData = await Request.ReadFormAsync();
                if (formData.TryGetValue("anoPublicacao", out var yearValues))
                {
                    if (int.TryParse(yearValues.FirstOrDefault(), out var year))
                    {
                        product.PublicationYear = year;
                    }
                }
            }

            // Handle variants updates if provided
            var variantsJson = (updateProductDto.VariantsJson ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(variantsJson))
            {
                List<VariantUpsert>? variants;
                try
                {
                    variants = JsonSerializer.Deserialize<List<VariantUpsert>>(variantsJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return BadRequest("Invalid variantsJson");
                }

                variants ??= [];

                var files = updateProductDto.VariantFiles ?? [];
                var keys = updateProductDto.VariantFileKeys ?? [];
                var fileByKey = new Dictionary<string, IFormFile>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < Math.Min(files.Count, keys.Count); i++)
                {
                    var k = (keys[i] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(k)) continue;
                    fileByKey[k] = files[i];
                }

                product.Variants ??= [];

                var existingById = product.Variants
                    .Where(x => x.Id > 0)
                    .ToDictionary(x => x.Id, x => x);

                var keepIds = new HashSet<int>();

                foreach (var v in variants)
                {
                    var color = (v.Color ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(color))
                        return BadRequest("Variant color is required");

                    ProductVariant variant;
                    if (v.Id.HasValue && v.Id.Value > 0 && existingById.TryGetValue(v.Id.Value, out var existing))
                    {
                        variant = existing;
                        keepIds.Add(variant.Id);
                    }
                    else
                    {
                        variant = new ProductVariant
                        {
                            Product = product,
                            Color = color,
                            CreatedAt = DateTime.UtcNow
                        };
                        product.Variants.Add(variant);
                    }

                    variant.Color = color;
                    variant.QuantityInStock = Math.Max(0, v.QuantityInStock);
                    variant.PriceOverride = v.PriceOverride;
                    variant.DescriptionOverride = string.IsNullOrWhiteSpace(v.DescriptionOverride) ? null : v.DescriptionOverride.Trim();
                    variant.UpdatedAt = DateTime.UtcNow;

                    var key = (v.Key ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(key) && fileByKey.TryGetValue(key, out var file) && file != null)
                    {
                        var imageResult = await imageService.AddImageAsync(file);
                        if (imageResult.Error != null) return BadRequest(imageResult.Error.Message);

                        if (!string.IsNullOrEmpty(variant.PublicId))
                        {
                            try { await imageService.DeleteImageAsync(variant.PublicId); } catch { }
                        }

                        variant.PictureUrl = imageResult.SecureUrl.AbsoluteUri;
                        variant.PublicId = imageResult.PublicId;
                    }
                }

                // Remove variants not present in payload
                var toRemove = product.Variants
                    .Where(x => x.Id > 0)
                    .Where(x => !keepIds.Contains(x.Id))
                    .ToList();

                foreach (var r in toRemove)
                {
                    if (!string.IsNullOrEmpty(r.PublicId))
                    {
                        try { await imageService.DeleteImageAsync(r.PublicId); } catch { }
                    }
                    product.Variants.Remove(r);
                    context.ProductVariants.Remove(r);
                }

                // Keep product-level stock as total for existing UIs
                product.QuantityInStock = product.Variants.Sum(x => x.QuantityInStock);
            }

            // Handle custom properties update if provided.
            // NOTE: ProductForm submits `propertiesJson` even when empty to allow clearing.
            if (updateProductDto.PropertiesJson != null || (formData != null && formData.ContainsKey("propertiesJson_present")))
            {
                var propertiesJson = (updateProductDto.PropertiesJson ?? string.Empty).Trim();

                List<PropertyUpsert>? props;
                try
                {
                    props = JsonSerializer.Deserialize<List<PropertyUpsert>>(propertiesJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return BadRequest("Invalid propertiesJson");
                }

                props ??= [];

                var normalized = props
                    .Select(p => new
                    {
                        categoryId = p.CategoryId,
                        name = (p.Name ?? string.Empty).Trim(),
                        value = (p.Value ?? string.Empty).Trim()
                    })
                    .Where(p => !string.IsNullOrWhiteSpace(p.name))
                    .ToList();

                product.CustomPropertiesJson = normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
            }

            // update campaigns/categories if provided. Also treat the presence of the
            // keys in the multipart form as an explicit update (even when empty)
            if (updateProductDto.CampaignIds != null || (formData != null && formData.ContainsKey("campaignIds_present")))
            {
                var ids = updateProductDto.CampaignIds ?? new List<int>();
                var selectedCampaigns = await context.Campaigns
                    .Where(c => ids.Contains(c.Id))
                    .ToListAsync();

                product.Campaigns ??= new List<Campaign>();
                product.Campaigns.Clear();
                foreach (var c in selectedCampaigns)
                {
                    product.Campaigns.Add(c);
                }
            }

            if (updateProductDto.CategoryIds != null || (formData != null && formData.ContainsKey("categoryIds_present")))
            {
                var ids = updateProductDto.CategoryIds ?? new List<int>();
                var selectedCategories = await context.Categories
                    .Where(c => ids.Contains(c.Id))
                    .ToListAsync();

                product.Categories ??= new List<Category>();
                product.Categories.Clear();
                foreach (var c in selectedCategories)
                {
                    product.Categories.Add(c);
                }
            }

            try
            {
                var result = await context.SaveChangesAsync() > 0;
                if (result) return NoContent();
                return BadRequest(new { message = "Problem updating product" });
            }
            catch (DbUpdateException dbEx)
            {
                // surface DB error details to aid debugging
                var inner = dbEx.InnerException?.Message ?? dbEx.Message;
                return BadRequest(new { message = "Problem updating product", detail = inner });
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{id:int}/publish")]
        public async Task<ActionResult> PublishProduct(int id)
        {
            var product = await context.Products.FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            product.IsPublished = true;
            product.UpdatedAt = DateTime.UtcNow;

            var saved = await context.SaveChangesAsync() > 0;
            if (!saved) return BadRequest("Problem publishing product");

            return NoContent();
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{id:int}/unpublish")]
        public async Task<ActionResult> UnpublishProduct(int id)
        {
            var product = await context.Products
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            if (product.IsPublished == false) return NoContent();

            product.IsPublished = false;
            product.UpdatedAt = DateTime.UtcNow;

            var saved = await context.SaveChangesAsync() > 0;
            if (!saved) return BadRequest("Problem unpublishing product");

            return NoContent();
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{id:int}")]
        public async Task<ActionResult> DeleteProduct(int id)
        {
            var product = await context.Products
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == id);

            if (product == null) return NotFound();

            // Soft-delete: keep assets so we can restore within 15 days.
            if (product.DeletedAt.HasValue) return NoContent();

            var now = DateTime.UtcNow;
            product.DeletedAt = now;
            product.IsPublished = false;
            product.UpdatedAt = now;

            var saved = await context.SaveChangesAsync() > 0;
            if (!saved) return BadRequest("Problem deleting the product");

            return NoContent();
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("{id:int}/restore")]
        public async Task<ActionResult> RestoreProduct(int id)
        {
            var product = await context.Products
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == id);

            if (product == null) return NotFound();
            if (!product.DeletedAt.HasValue) return BadRequest("Product is not deleted");

            var deletedAt = product.DeletedAt.Value;
            if (DateTime.UtcNow > deletedAt.AddMonths(2))
            {
                return BadRequest("Restore window expired (2 months)");
            }

            product.DeletedAt = null;
            product.UpdatedAt = DateTime.UtcNow;

            var saved = await context.SaveChangesAsync() > 0;
            if (!saved) return BadRequest("Problem restoring the product");

            return NoContent();
        }

        [Authorize(Roles = "Admin")]
        [HttpGet("deleted")]
        public async Task<ActionResult> GetDeletedProducts([FromQuery] int days = 62)
        {
            var clamped = Math.Clamp(days, 1, 120);
            var cutoff = DateTime.UtcNow.AddDays(-clamped);

            var items = await context.Products
                .IgnoreQueryFilters()
                .Where(p => p.DeletedAt.HasValue && p.DeletedAt.Value >= cutoff)
                .OrderByDescending(p => p.DeletedAt)
                .Select(p => new { p.Id, p.Name, p.DeletedAt })
                .ToListAsync();

            return Ok(items);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("bulk-delete")]
        public async Task<ActionResult> BulkDeleteProducts([FromBody] API.DTOs.BulkDeleteProductsDto dto)
        {
            if (dto == null) return BadRequest("Invalid payload");

            var query = context.Products.AsQueryable();

            if (dto.DeleteAll)
            {
                // keep query as-is
            }
            else if (dto.CategoryId.HasValue)
            {
                var cid = dto.CategoryId.Value;
                query = query.Where(p => p.Categories!.Any(c => c.Id == cid));
            }
            else if (dto.ProductIds != null && dto.ProductIds.Count > 0)
            {
                var ids = dto.ProductIds.Distinct().ToList();
                query = query.Where(p => ids.Contains(p.Id));
            }
            else
            {
                return BadRequest("Specify deleteAll, categoryId, or productIds");
            }

            var now = DateTime.UtcNow;
            var products = await query.ToListAsync();
            foreach (var p in products)
            {
                if (p.DeletedAt.HasValue) continue;
                p.DeletedAt = now;
                p.IsPublished = false;
                p.UpdatedAt = now;
            }

            await context.SaveChangesAsync();
            return Ok(new { deleted = products.Count });
        }
    }
}