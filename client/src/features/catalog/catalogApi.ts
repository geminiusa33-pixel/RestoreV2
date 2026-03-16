import { createApi } from "@reduxjs/toolkit/query/react";
import { Product } from "../../app/models/product";
import { baseQueryWithErrorHandling } from "../../app/api/baseApi";
import { ProductParams } from "../../app/models/productParams";
import { filterEmptyValues } from "../../lib/util";
import { Pagination } from "../../app/models/pagination";
import { ProductReview } from "../../app/models/productReview";

export const catalogApi = createApi({
    reducerPath: 'catalogApi',
    baseQuery: baseQueryWithErrorHandling,
    tagTypes: ['Products','Filters','ProductReviews'],
    endpoints: (builder) => ({
        fetchProducts: builder.query<{items: Product[], pagination: Pagination}, ProductParams>({
            query: (productParams) => {
                // backend expects comma-separated strings for generos and anos
                const params: Record<string, string | number | undefined> = {
                    orderBy: productParams.orderBy,
                    pageNumber: productParams.pageNumber,
                    pageSize: productParams.pageSize,
                    searchTerm: productParams.searchTerm
                };

                if (productParams.generos && productParams.generos.length > 0) {
                    params.generos = productParams.generos.map(g => g.toLowerCase()).join(',');
                }

                if (productParams.anos && productParams.anos.length > 0) {
                    params.anos = productParams.anos.join(',');
                }

                if (productParams.categoryIds && productParams.categoryIds.length > 0) {
                    params.categoryIds = productParams.categoryIds.join(',');
                }

                if (productParams.campaignIds && productParams.campaignIds.length > 0) {
                    params.campaignIds = productParams.campaignIds.join(',');
                }

                if (productParams.marcas && productParams.marcas.length > 0) {
                    params.marcas = productParams.marcas.map(x => x.toLowerCase()).join(',');
                }

                if (productParams.modelos && productParams.modelos.length > 0) {
                    params.modelos = productParams.modelos.map(x => x.toLowerCase()).join(',');
                }

                if (productParams.tipos && productParams.tipos.length > 0) {
                    params.tipos = productParams.tipos.map(x => x.toLowerCase()).join(',');
                }

                if (productParams.capacidades && productParams.capacidades.length > 0) {
                    params.capacidades = productParams.capacidades.map(x => x.toLowerCase()).join(',');
                }

                if (productParams.cores && productParams.cores.length > 0) {
                    params.cores = productParams.cores.map(x => x.toLowerCase()).join(',');
                }

                if (productParams.materiais && productParams.materiais.length > 0) {
                    params.materiais = productParams.materiais.map(x => x.toLowerCase()).join(',');
                }

                if (productParams.tamanhos && productParams.tamanhos.length > 0) {
                    params.tamanhos = productParams.tamanhos.map(x => x.toLowerCase()).join(',');
                }

                if (productParams.hasDiscount !== undefined) {
                    params.hasDiscount = productParams.hasDiscount ? 'true' : 'false';
                }

                return {
                    url: 'products',
                    params: filterEmptyValues(params)
                }
                },
            providesTags: (result) =>
            {
                // provide a Products list tag so mutations can invalidate
                return result?.items ? [
                    ...result.items.map(({ id }) => ({ type: 'Products' as const, id })),
                    { type: 'Products', id: 'LIST' }
                ] : [{ type: 'Products', id: 'LIST' }]
            },
            transformResponse: (items: Product[], meta) => {
                const paginationHeader = meta?.response?.headers.get('Pagination');
                const pagination = paginationHeader ? JSON.parse(paginationHeader) : null;
                return {items, pagination}
            }
        }),
        fetchProductDetails: builder.query<Product, number>({
            query: (productId) => `products/${productId}`
        ,
            providesTags: (_result, _error, id) => [{ type: 'Products', id }]
        }),
        fetchProductReviews: builder.query<ProductReview[], number>({
            query: (productId) => `products/${productId}/reviews`,
            providesTags: (_result, _error, id) => [{ type: 'ProductReviews', id }]
        }),
        createProductReview: builder.mutation<ProductReview, { productId: number; rating: number; comment: string }>({
            query: ({ productId, rating, comment }) => ({
                url: `products/${productId}/reviews`,
                method: 'POST',
                body: { rating, comment }
            }),
            invalidatesTags: (_result, _error, { productId }) => [
                { type: 'ProductReviews', id: productId },
                { type: 'Products', id: productId }
            ]
        }),
        replyProductReview: builder.mutation<ProductReview, { productId: number; reviewId: number; reply: string }>({
            query: ({ productId, reviewId, reply }) => ({
                url: `products/${productId}/reviews/${reviewId}/reply`,
                method: 'PUT',
                body: { reply }
            }),
            invalidatesTags: (_result, _error, { productId }) => [
                { type: 'ProductReviews', id: productId },
                { type: 'Products', id: productId }
            ]
        }),
        deleteProductReview: builder.mutation<void, { productId: number; reviewId: number }>({
            query: ({ productId, reviewId }) => ({
                url: `products/${productId}/reviews/${reviewId}`,
                method: 'DELETE'
            }),
            invalidatesTags: (_result, _error, { productId }) => [
                { type: 'ProductReviews', id: productId },
                { type: 'Products', id: productId }
            ]
        }),
        recordProductClick: builder.mutation<void, { productId: number; sessionId: string }>({
            query: ({ productId, sessionId }) => ({
                url: `products/${productId}/click`,
                method: 'POST',
                body: { sessionId }
            })
        }),
        fetchFilters: builder.query<{
            generos: string[];
            anos: number[];
            categories?: { id: number; name: string; slug?: string; isActive?: boolean; parentCategoryId?: number | null }[];
            campaigns?: { id: number; name: string; slug?: string; isActive?: boolean }[];
            marcas?: string[];
            modelos?: string[];
            tipos?: string[];
            capacidades?: string[];
            cores?: string[];
            materiais?: string[];
            tamanhos?: string[];
        }, void>({
            query: () => 'products/filters'
        ,
            providesTags: ['Filters']
        })
    })
});

export const {
    useFetchProductDetailsQuery,
    useFetchProductsQuery,
    useFetchFiltersQuery,
    useRecordProductClickMutation,
    useFetchProductReviewsQuery,
    useCreateProductReviewMutation,
    useReplyProductReviewMutation,
    useDeleteProductReviewMutation
} 
    = catalogApi;