using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace API.Entities;


[Owned]
public class Dimensions
{
    [JsonPropertyName("altura")]
    public int? Altura { get; set; }

    [JsonPropertyName("largura")]
    public int? Largura { get; set; }

    [JsonPropertyName("espessura")]
    public int? Espessura { get; set; }
}

public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    // General product description (used by the storefront + admin form)
    public string? Description { get; set; }

    public string? Subtitle { get; set; }

    public string? Author { get; set; }

    public List<string>? SecondaryAuthors { get; set; }

    public string? ISBN { get; set; }

    public string? Publisher { get; set; }

    public int? Edition { get; set; }

    [JsonPropertyName("anoPublicacao")]
    public int? PublicationYear { get; set; }

    // store genre as free-form text so admins can add genres without code changes
    public string? Genero { get; set; }

    public decimal Price { get; set; }

    public decimal? PromotionalPrice { get; set; }

    public int? DiscountPercentage { get; set; }

    public int SalesCount { get; set; } = 0;

    public int QuantityInStock { get; set; }

    public string? Synopsis { get; set; }

    public string? Index { get; set; }

    public int? PageCount { get; set; }

    public string? Language { get; set; }

    public string? Format { get; set; }

    public Dimensions? Dimensoes { get; set; }

    public int? Weight { get; set; }

    // clothing / toy specific attributes
    public string? Cor { get; set; }
    public string? Material { get; set; }
    public string? Tamanho { get; set; }
    public string? Marca { get; set; }

    // technology specific attributes
    public string? Tipo { get; set; }
    public string? Modelo { get; set; }
    public string? Capacidade { get; set; }

    // toy specific attributes
    public int? IdadeMinima { get; set; }
    public int? IdadeMaxima { get; set; }

    public string? PictureUrl { get; set; }

    public List<string>? SecondaryImages { get; set; }

    public List<string>? SecondaryImagePublicIds { get; set; }

    public List<string>? Tags { get; set; }

    public double? AverageRating { get; set; }

    public int? RatingsCount { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // Soft-delete: when set, product is hidden from the storefront/admin inventory by default.
    // Can be restored within a retention window (handled in controller).
    public DateTime? DeletedAt { get; set; }

    public bool Active { get; set; } = true;

    // Publishing gate: products start as drafts (IsPublished=false) until admin approves.
    public bool IsPublished { get; set; } = false;

    public string? PublicId { get; set; }

    // Arbitrary, admin-defined properties stored as JSON.
    // Expected shape: [{ categoryId?: number|null, name: string, value: string }]
    public string? CustomPropertiesJson { get; set; }

    // relations
    public List<Campaign>? Campaigns { get; set; }
    public List<Category>? Categories { get; set; }

    public List<ProductVariant> Variants { get; set; } = [];

    [NotMapped]
    public bool IsFavorite { get; set; } = false;
}