using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace API.Entities;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }

    // Hierarchy (up to 4 levels enforced by API/UI)
    public int? ParentCategoryId { get; set; }

    [JsonIgnore]
    public Category? ParentCategory { get; set; }

    [JsonIgnore]
    public List<Category> Children { get; set; } = [];

    [JsonIgnore]
    public List<Product>? Products { get; set; }
}
