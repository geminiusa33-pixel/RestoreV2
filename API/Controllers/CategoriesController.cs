using API.Data;
using API.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Globalization;
using System.Text;

namespace API.Controllers;

public class CategoriesController(StoreContext context) : BaseApiController
{
    private static string NormalizeKey(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length == 0) return string.Empty;

        // Remove diacritics (e.g., "Vestuário" == "Vestuario") and normalize spaces.
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    [HttpGet]
    public async Task<ActionResult<List<Category>>> GetCategories([FromQuery] bool onlyWithProducts = false)
    {
        var query = context.Categories
            .Where(c => c.IsActive)
            .AsQueryable();

        if (onlyWithProducts)
        {
            // For admin UIs: hide categories that currently have no products assigned.
            // Soft-deletes are filtered via the global query filter on Product.
            query = query.Where(c => c.Products!.Any());
        }

        var items = await query.ToListAsync();

        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Category>> GetCategory(int id)
    {
        var item = await context.Categories.FindAsync(id);
        if (item == null) return NotFound();
        return item;
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<ActionResult<Category>> CreateCategory(Category c)
    {
        if (c == null) return BadRequest("Invalid category payload");
        c.Name = (c.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(c.Name)) return BadRequest("Category name is required");

        if (c.ParentCategoryId.HasValue)
        {
            // Parent must exist
            var parent = await context.Categories.FirstOrDefaultAsync(x => x.Id == c.ParentCategoryId.Value);
            if (parent == null) return BadRequest("Parent category not found");

            // Enforce max depth of 4 layers (root=1 ... leaf=4)
            var depth = 1;
            var currentParentId = parent.ParentCategoryId;
            while (currentParentId.HasValue)
            {
                depth++;
                if (depth >= 4) break;
                currentParentId = await context.Categories
                    .Where(x => x.Id == currentParentId.Value)
                    .Select(x => x.ParentCategoryId)
                    .FirstOrDefaultAsync();
            }

            // If parent is already at depth 4, child would exceed limit.
            if (depth >= 4) return BadRequest("Maximum category depth is 4 levels");
        }

        // Prevent duplicates for the same parent (case-insensitive).
        // If it already exists, return the existing category so the UI can re-use it.
        var normalizedName = NormalizeKey(c.Name);
        var existing = await context.Categories
            .Where(x => x.IsActive)
            .Where(x => (x.ParentCategoryId ?? null) == (c.ParentCategoryId ?? null))
            .ToListAsync();
        var match = existing.FirstOrDefault(x => NormalizeKey(x.Name) == normalizedName);
        if (match != null)
        {
            return Ok(match);
        }

        if (string.IsNullOrWhiteSpace(c.Slug) && !string.IsNullOrWhiteSpace(c.Name))
        {
            c.Slug = c.Name.ToLower().Replace(' ', '-');
        }

        // Only allow creating with a safe payload
        c.Products = null;

        context.Categories.Add(c);
        var res = await context.SaveChangesAsync() > 0;
        if (!res) return BadRequest("Problem creating category");
        return CreatedAtAction(nameof(GetCategory), new { id = c.Id }, c);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut]
    public async Task<ActionResult> UpdateCategory(Category c)
    {
        var existing = await context.Categories.FindAsync(c.Id);
        if (existing == null) return NotFound();

        existing.Name = c.Name;
        existing.Slug = c.Slug;
        existing.IsActive = c.IsActive;
        existing.Description = c.Description;

        var res = await context.SaveChangesAsync() > 0;
        if (!res) return BadRequest("Problem updating category");
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("cleanup-unused")]
    public async Task<ActionResult> CleanupUnusedCategories()
    {
        // "Live" = published products only. Soft-deleted products are excluded by the global query filter.
        var directLiveCategoryIds = await context.Products
            .Where(p => p.IsPublished)
            .SelectMany(p => p.Categories!.Select(c => c.Id))
            .Distinct()
            .ToListAsync();

        var parents = await context.Categories
            .Select(c => new { c.Id, c.ParentCategoryId })
            .ToListAsync();

        var parentById = parents.ToDictionary(x => x.Id, x => x.ParentCategoryId);
        var keep = new HashSet<int>(directLiveCategoryIds);

        // Keep ancestors too (so the tree stays intact for any live leaf category).
        foreach (var id in directLiveCategoryIds)
        {
            var current = id;
            for (var i = 0; i < 10; i++)
            {
                if (!parentById.TryGetValue(current, out var pid) || pid == null) break;
                keep.Add(pid.Value);
                current = pid.Value;
            }
        }

        var toDeactivate = await context.Categories
            .Where(c => c.IsActive)
            .Where(c => !keep.Contains(c.Id))
            .ToListAsync();

        foreach (var c in toDeactivate)
        {
            c.IsActive = false;
        }

        await context.SaveChangesAsync();

        return Ok(new { deactivated = toDeactivate.Count });
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> DeleteCategory(int id)
    {
        var existing = await context.Categories.FindAsync(id);
        if (existing == null) return NotFound();

        context.Categories.Remove(existing);
        try
        {
            var res = await context.SaveChangesAsync() > 0;
            if (!res) return BadRequest("Problem deleting category");
            return Ok();
        }
        catch (DbUpdateException)
        {
            return BadRequest("Cannot delete a category that has subcategories or is in use.");
        }
    }
}
