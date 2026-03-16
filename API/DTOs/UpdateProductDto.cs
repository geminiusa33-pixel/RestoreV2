using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace API.DTOs;

public class UpdateProductDto
{
    public int Id { get; set; }

    [JsonPropertyName("titulo")]
    [Required]
    public string Name { get; set; } = string.Empty;

    // Bound from multipart/form-data field `description` sent by the admin form
    public string? Description { get; set; }

    [JsonPropertyName("subtitulo")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("autor")]
    public string? Author { get; set; }

    [JsonPropertyName("isbn")]
    public string? ISBN { get; set; }

    [JsonPropertyName("editora")]
    public string? Publisher { get; set; }

    [JsonPropertyName("edicao")]
    public int? Edition { get; set; }

    [JsonPropertyName("anoPublicacao")]
    [Range(0, 3000)]
    public int? PublicationYear { get; set; }

    [JsonIgnore]
    // bind form field 'anoPublicacao' to PublicationYear when multipart/form-data is posted
    // provide getter so controller or other code can read the bound value
    public int? anoPublicacao { get => PublicationYear; set => PublicationYear = value; }

    [JsonPropertyName("genero")]
    public string? Genero { get; set; }

    [JsonPropertyName("preco")]
    [Required]
    public decimal Price { get; set; }

    [JsonPropertyName("precoPromocional")]
    public decimal? PromotionalPrice { get; set; }

    [JsonPropertyName("descontoPercentagem")]
    public int? DiscountPercentage { get; set; }

    [JsonPropertyName("stock")]
    [Required]
    public int QuantityInStock { get; set; }

    [JsonPropertyName("sinopse")]
    public string? Synopsis { get; set; }

    [JsonPropertyName("numeroPaginas")]
    public int? PageCount { get; set; }

    [JsonPropertyName("idioma")]
    public string? Language { get; set; }

    [JsonPropertyName("formato")]
    public string? Format { get; set; }

    [JsonPropertyName("dimensoes")]
    public DimensionsDto? Dimensoes { get; set; }

    [JsonPropertyName("peso")]
    public int? Weight { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("cor")]
    public string? Cor { get; set; }

    [JsonPropertyName("material")]
    public string? Material { get; set; }

    [JsonPropertyName("tamanho")]
    public string? Tamanho { get; set; }

    [JsonPropertyName("marca")]
    public string? Marca { get; set; }

    [JsonPropertyName("tipo")]
    public string? Tipo { get; set; }

    [JsonPropertyName("modelo")]
    public string? Modelo { get; set; }

    [JsonPropertyName("capacidade")]
    public string? Capacidade { get; set; }

    [JsonPropertyName("idadeMinima")]
    public int? IdadeMinima { get; set; }

    [JsonPropertyName("idadeMaxima")]
    public int? IdadeMaxima { get; set; }

    [JsonPropertyName("activo")]
    public bool Active { get; set; } = true;

    [JsonPropertyName("file")]
    public IFormFile? File { get; set; }

    [JsonPropertyName("secondaryFiles")]
    public List<IFormFile>? SecondaryFiles { get; set; }

    [JsonPropertyName("removedSecondaryImages")]
    public List<string>? RemovedSecondaryImages { get; set; }

    [JsonPropertyName("campaignIds")]
    public List<int>? CampaignIds { get; set; }

    [JsonPropertyName("categoryIds")]
    public List<int>? CategoryIds { get; set; }

    public string? VariantsJson { get; set; }
    public List<IFormFile>? VariantFiles { get; set; }
    public List<string>? VariantFileKeys { get; set; }

    // Custom properties (free-form key/value pairs) submitted as JSON in the same multipart request.
    // Expected shape: [{ categoryId?: number|null, name: string, value: string }]
    public string? PropertiesJson { get; set; }
}