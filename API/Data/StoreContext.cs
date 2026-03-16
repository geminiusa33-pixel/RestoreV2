using System;
using API.Entities;
using API.Entities.OrderAggregate;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace API.Data;

public class StoreContext(DbContextOptions options) : IdentityDbContext<User>(options)
{
    public required DbSet<Product> Products { get; set; }
    public required DbSet<ProductVariant> ProductVariants { get; set; }
    public required DbSet<ProductClick> ProductClicks { get; set; }
    public required DbSet<Favorite> Favorites { get; set; }
    public required DbSet<Campaign> Campaigns { get; set; }
    public required DbSet<Category> Categories { get; set; }
    public required DbSet<Basket> Baskets { get; set; }
    public required DbSet<Order> Orders { get; set; }
    public required DbSet<Promo> Promos { get; set; }
    public required DbSet<Logo> Logos { get; set; }
    public required DbSet<Contact> Contacts { get; set; }
    public required DbSet<ShippingRate> ShippingRates { get; set; }
    public required DbSet<HeroBlock> HeroBlocks { get; set; }
    public required DbSet<HeroBlockImage> HeroBlockImages { get; set; }
    public required DbSet<Newsletter> Newsletters { get; set; }
    public required DbSet<NewsletterAttachment> NewsletterAttachments { get; set; }
    public required DbSet<ProductReview> ProductReviews { get; set; }
    public required DbSet<OrderIncident> OrderIncidents { get; set; }
    public required DbSet<OrderIncidentAttachment> OrderIncidentAttachments { get; set; }
    public required DbSet<UserNotification> UserNotifications { get; set; }
    public required DbSet<UiSettings> UiSettings { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Logo>(b =>
        {
            b.Property(x => x.Url).IsRequired().HasMaxLength(500);
            b.Property(x => x.PublicId).HasMaxLength(200);
            b.Property(x => x.Scale).HasPrecision(5, 2).HasDefaultValue(1.00m);
        });

        builder.Entity<ProductVariant>(b =>
        {
            b.HasIndex(x => x.ProductId);
            b.Property(x => x.Color).IsRequired().HasMaxLength(100);
            b.Property(x => x.PictureUrl).HasMaxLength(500);
            b.Property(x => x.PublicId).HasMaxLength(200);
            b.Property(x => x.PriceOverride).HasPrecision(18, 2);
            b.HasOne(x => x.Product)
                .WithMany(p => p.Variants)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserNotification>(b =>
        {
            b.HasIndex(x => x.UserId);
            b.HasIndex(x => new { x.UserId, x.IsRead });
            b.Property(x => x.UserId).IsRequired().HasMaxLength(450);
            b.Property(x => x.Title).IsRequired().HasMaxLength(200);
            b.Property(x => x.Message).IsRequired().HasMaxLength(2000);
            b.Property(x => x.Url).HasMaxLength(500);
        });

        builder.Entity<UiSettings>(b =>
        {
            b.Property(x => x.PrimaryColorLight).IsRequired().HasMaxLength(20);
            b.Property(x => x.SecondaryColorLight).IsRequired().HasMaxLength(20);
            b.Property(x => x.PrimaryColorDark).IsRequired().HasMaxLength(20);
            b.Property(x => x.SecondaryColorDark).IsRequired().HasMaxLength(20);
            b.Property(x => x.ButtonIconColor).IsRequired().HasMaxLength(50);
            b.Property(x => x.NotificationsBadgeColorLight).IsRequired().HasMaxLength(50);
            b.Property(x => x.NotificationsBadgeColorDark).IsRequired().HasMaxLength(50);
            b.HasIndex(x => x.UpdatedAt);
        });

        builder.Entity<OrderIncident>(b =>
        {
            b.HasIndex(x => x.OrderId).IsUnique();
            b.Property(x => x.BuyerEmail).IsRequired().HasMaxLength(320);
            b.Property(x => x.Description).IsRequired().HasMaxLength(2000);
            b.HasMany(x => x.Attachments)
                .WithOne(a => a.OrderIncident)
                .HasForeignKey(a => a.OrderIncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OrderIncidentAttachment>(b =>
        {
            b.Property(x => x.OriginalFileName).IsRequired().HasMaxLength(260);
            b.Property(x => x.StoredFileName).IsRequired().HasMaxLength(260);
            b.Property(x => x.ContentType).IsRequired().HasMaxLength(128);
            b.Property(x => x.RelativePath).IsRequired().HasMaxLength(500);
        });

        builder.Entity<Order>()
            .HasOne(o => o.Incident)
            .WithOne(i => i.Order)
            .HasForeignKey<OrderIncident>(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Order>(b =>
        {
            b.Property(x => x.RefundId).HasMaxLength(100);
            b.HasIndex(x => x.RefundId);

            b.HasIndex(x => x.RefundRequestStatus);
            b.Property(x => x.RefundRequestReason).HasMaxLength(1000);
            b.Property(x => x.RefundReviewNote).HasMaxLength(2000);
        });

        builder.Entity<ProductReview>(b =>
        {
            b.HasIndex(x => x.ProductId);
            b.HasIndex(x => new { x.ProductId, x.BuyerEmail, x.OrderId }).IsUnique();
            b.HasQueryFilter(x => !x.IsDeleted);
            b.Property(x => x.BuyerEmail).IsRequired().HasMaxLength(320);
            b.Property(x => x.Comment).IsRequired().HasMaxLength(1000);
            b.Property(x => x.AdminReply).HasMaxLength(2000);
            b.Property(x => x.DeletedByEmail).HasMaxLength(320);
            b.Property(x => x.DeletedReason).HasMaxLength(500);
            b.HasCheckConstraint("CK_ProductReviews_Rating", "[Rating] >= 1 AND [Rating] <= 5");
            b.HasOne<Product>()
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<User>()
            .Property(u => u.NewsletterOptIn)
            .HasDefaultValue(false);

        builder.Entity<Newsletter>()
            .HasMany(n => n.Attachments)
            .WithOne(a => a.Newsletter)
            .HasForeignKey(a => a.NewsletterId)
            .OnDelete(DeleteBehavior.Cascade);

        // Avoid implicit decimal precision/scale defaults on SQL Server (prevents truncation + removes EF warnings)
        builder.Entity<Product>()
            .Property(p => p.Price)
            .HasPrecision(18, 2);

        // Hide soft-deleted products by default across the app.
        builder.Entity<Product>()
            .HasQueryFilter(p => p.DeletedAt == null);

        // Keep required relationships consistent with Product's query filter.
        builder.Entity<BasketItem>()
            .HasQueryFilter(x => x.Product.DeletedAt == null);

        builder.Entity<ProductVariant>()
            .HasQueryFilter(x => x.Product.DeletedAt == null);

        builder.Entity<Favorite>()
            .HasQueryFilter(x => x.Product == null || x.Product.DeletedAt == null);

        builder.Entity<ProductClick>()
            .HasQueryFilter(x => x.Product == null || x.Product.DeletedAt == null);

        builder.Entity<Product>()
            .Property(p => p.PromotionalPrice)
            .HasPrecision(18, 2);

        builder.Entity<ShippingRate>()
            .Property(p => p.Rate)
            .HasPrecision(18, 2);

        builder.Entity<ShippingRate>()
            .Property(p => p.FreeShippingThreshold)
            .HasPrecision(18, 2);

        builder.Entity<IdentityRole>()
            .HasData(
                new IdentityRole {Id = "e069461a-10cf-4abf-9930-d070b2a7e40f", Name = "Member", NormalizedName = "MEMBER"},
                new IdentityRole {Id = "ed2e9149-fa53-484c-a93f-bd33f9e9fcf6", Name = "Admin", NormalizedName = "ADMIN"}
            );

        builder.Entity<Category>(b =>
        {
            b.HasIndex(x => x.ParentCategoryId);
            b.HasIndex(x => x.Slug);

            b.HasOne(x => x.ParentCategory)
                .WithMany(p => p.Children)
                .HasForeignKey(x => x.ParentCategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
