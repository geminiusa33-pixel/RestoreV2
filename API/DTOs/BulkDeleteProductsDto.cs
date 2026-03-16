using System.Collections.Generic;

namespace API.DTOs;

public class BulkDeleteProductsDto
{
    public bool DeleteAll { get; set; } = false;
    public int? CategoryId { get; set; }
    public List<int> ProductIds { get; set; } = [];
}
