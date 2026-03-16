export type Product = {
    id: number
    name: string
    description?: string | null
    price: number
    pictureUrl: string
    // removed 'type' and 'brand' — replaced with book-specific fields
    genero?: string
    anoPublicacao?: number
    quantityInStock: number
    // optional fields returned by the API
    subtitle?: string | null
    author?: string | null
    secondaryAuthors?: string | null
    isbn?: string | null
    publisher?: string | null
    edition?: string | null
    promotionalPrice?: number | null
    discountPercentage?: number | null
    salesCount?: number | null
    synopsis?: string | null
    index?: string | null
    pageCount?: number | null
    language?: string | null
    format?: string | null
    dimensoes?: string | null
    weight?: number | null
    secondaryImages?: string[] | null
    secondaryImagePublicIds?: string[] | null
    // clothing / toy specific
    cor?: string | null
    material?: string | null
    tamanho?: string | null
    marca?: string | null
    // technology specific
    tipo?: string | null
    modelo?: string | null
    capacidade?: string | null
    // toy specific
    idadeMinima?: number | null
    idadeMaxima?: number | null
    // metadata
    averageRating?: number | null
    ratingsCount?: number | null
    createdAt?: string | null
    updatedAt?: string | null
    active?: boolean | null
    tags?: string[]
    publicId?: string | null
    categories?: { id: number; name: string }[] | null
    campaigns?: { id: number; name: string }[] | null
    isFavorite?: boolean | null
    // publishing gate (admin sees drafts)
    isPublished?: boolean | null
    // color variants (each can override stock/photo/description/price)
    variants?: ProductVariant[] | null

    // admin-defined free-form key/value properties (JSON string)
    customPropertiesJson?: string | null
}

export type ProductVariant = {
    id: number
    productId?: number
    color: string
    quantityInStock: number
    priceOverride?: number | null
    descriptionOverride?: string | null
    pictureUrl?: string | null
    publicId?: string | null
    createdAt?: string | null
    updatedAt?: string | null
}