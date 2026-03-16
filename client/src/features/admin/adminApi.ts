import { createApi } from "@reduxjs/toolkit/query/react";
import { baseQueryWithErrorHandling } from "../../app/api/baseApi";
import { Product } from "../../app/models/product";
import { User } from "../../app/models/user";
import { Campaign } from "../../app/models/campaign";
import { Category } from "../../app/models/category";
import { catalogApi } from "../catalog/catalogApi";

export const adminApi = createApi({
    reducerPath: 'adminApi',
    baseQuery: baseQueryWithErrorHandling,
    tagTypes: ['Products','Filters','Users'],
    endpoints: (builder) => ({
        getUsers: builder.query<User[], void>({
            query: () => ({ url: 'admin/users' }),
            providesTags: ['Users']
        }),
        getPromo: builder.query<{ message: string; color: string }, void>({
            query: () => ({ url: 'admin/promo' }),
            providesTags: ['Users']
        }),
        updatePromo: builder.mutation<void, { message: string; color: string }>({
            query: (payload) => ({
                url: 'admin/promo',
                method: 'PUT',
                body: payload
            }),
            invalidatesTags: ['Users']
        }),
        updateUserRole: builder.mutation<void, { email: string; role: string }>({
            query: (payload) => ({
                url: 'admin/users/roles',
                method: 'PUT',
                body: payload
            }),
            invalidatesTags: ['Users']
        }),
        deleteUser: builder.mutation<void, { email: string; deleteStoredData: boolean }>({
            query: (payload) => ({
                url: 'admin/users/delete',
                method: 'POST',
                body: payload
            }),
            invalidatesTags: ['Users']
        }),
        createProduct: builder.mutation<Product, FormData>({
            query: (data: FormData) => {
                return {
                    url: 'products',
                    method: 'POST',
                    body: data
                }
            }
        ,
            invalidatesTags: ['Products','Filters'],
            async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
                try {
                    await queryFulfilled;
                    dispatch(catalogApi.util.invalidateTags(['Filters']));
                } catch { }
            }
        }),
        updateProduct: builder.mutation<void, {id: number, data: FormData}>({
            query: ({id, data}) => {
                data.append('id', id.toString())

                return {
                    url: 'products',
                    method: 'PUT',
                    body: data
                }
            }
        ,
            invalidatesTags: (_result, _error, { id }) => [{ type: 'Products', id }, { type: 'Products', id: 'LIST' }, 'Filters'],
            async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
                try {
                    await queryFulfilled;
                    dispatch(catalogApi.util.invalidateTags(['Filters']));
                } catch { }
            }
        }),
        deleteProduct: builder.mutation<void, number>({
            query: (id: number) => {
                return {
                    url: `products/${id}`,
                    method: 'DELETE'
                }
            }
        ,
            invalidatesTags: (_result, _error, id) => [{ type: 'Products', id }, { type: 'Products', id: 'LIST' }, 'Filters'],
            async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
                try {
                    await queryFulfilled;
                    dispatch(catalogApi.util.invalidateTags(['Filters']));
                } catch { }
            }
        }),
        bulkDeleteProducts: builder.mutation<{ deleted: number }, { deleteAll?: boolean; categoryId?: number; productIds?: number[] }>({
            query: (payload) => ({
                url: 'products/bulk-delete',
                method: 'POST',
                body: payload
            }),
            invalidatesTags: ['Products', 'Filters'],
            async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
                try {
                    await queryFulfilled;
                    dispatch(catalogApi.util.invalidateTags(['Filters']));
                } catch { }
            }
        }),
        publishProduct: builder.mutation<void, number>({
            query: (id: number) => ({
                url: `products/${id}/publish`,
                method: 'PUT'
            }),
            invalidatesTags: (_result, _error, id) => [{ type: 'Products', id }, { type: 'Products', id: 'LIST' }, 'Filters'],
            async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
                try {
                    await queryFulfilled;
                    dispatch(catalogApi.util.invalidateTags(['Filters']));
                } catch { }
            }
        }),
        unpublishProduct: builder.mutation<void, number>({
            query: (id: number) => ({
                url: `products/${id}/unpublish`,
                method: 'PUT'
            }),
            invalidatesTags: (_result, _error, id) => [{ type: 'Products', id }, { type: 'Products', id: 'LIST' }, 'Filters'],
            async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
                try {
                    await queryFulfilled;
                    dispatch(catalogApi.util.invalidateTags(['Filters']));
                } catch { }
            }
        }),
        getDeletedProducts: builder.query<Array<{ id: number; name: string; deletedAt: string }>, { days?: number } | void>({
            query: (arg) => {
                const days = (arg && 'days' in arg) ? arg.days : undefined;
                return ({ url: 'products/deleted', params: days ? { days } : undefined });
            },
            providesTags: ['Products']
        }),
        restoreProduct: builder.mutation<void, number>({
            query: (id: number) => ({
                url: `products/${id}/restore`,
                method: 'POST'
            }),
            invalidatesTags: (_result, _error, id) => [{ type: 'Products', id }, { type: 'Products', id: 'LIST' }, 'Filters'],
            async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
                try {
                    await queryFulfilled;
                    dispatch(catalogApi.util.invalidateTags(['Filters']));
                } catch { }
            }
        }),
        getCampaigns: builder.query<Campaign[], void>({
            query: () => ({ url: 'campaigns' }),
            providesTags: ['Filters']
        }),
        createCampaign: builder.mutation<Campaign, { name: string }>({
            query: (payload) => ({ url: 'campaigns', method: 'POST', body: payload }),
            invalidatesTags: ['Filters']
        }),
        deleteCampaign: builder.mutation<void, number>({
            query: (id: number) => ({ url: `campaigns/${id}`, method: 'DELETE' }),
            invalidatesTags: ['Filters']
        }),
        getCategories: builder.query<Category[], void>({
            query: () => ({ url: 'categories', params: { onlyWithProducts: true } }),
            providesTags: ['Filters']
        }),
        cleanupUnusedCategories: builder.mutation<{ deactivated: number }, void>({
            query: () => ({ url: 'categories/cleanup-unused', method: 'POST' }),
            invalidatesTags: ['Filters'],
            async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
                try {
                    await queryFulfilled;
                    dispatch(catalogApi.util.invalidateTags(['Filters']));
                } catch { }
            }
        }),
        getAllCategories: builder.query<Category[], void>({
            query: () => ({ url: 'categories' }),
            providesTags: ['Filters']
        }),
        createCategory: builder.mutation<Category, { name: string; parentCategoryId?: number | null }>({
            query: (payload) => ({ url: 'categories', method: 'POST', body: payload }),
            invalidatesTags: ['Filters']
        })
    })
});

export const {
    useCreateProductMutation,
    useUpdateProductMutation,
    usePublishProductMutation,
    useDeleteProductMutation,
    useBulkDeleteProductsMutation,
    useUnpublishProductMutation,
    useGetDeletedProductsQuery,
    useRestoreProductMutation,
    useGetCampaignsQuery,
    useDeleteCampaignMutation,
    useGetCategoriesQuery,
    useGetAllCategoriesQuery,
    useCleanupUnusedCategoriesMutation,
    useCreateCampaignMutation,
    useCreateCategoryMutation,
    useGetUsersQuery,
    useGetPromoQuery,
    useUpdatePromoMutation,
    useUpdateUserRoleMutation,
    useDeleteUserMutation
} = adminApi;