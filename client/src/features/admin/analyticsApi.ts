import { createApi } from '@reduxjs/toolkit/query/react';
import { baseQueryWithErrorHandling } from '../../app/api/baseApi';

export type TimeSeriesPoint = { date: string; value: number };
export type ProductCount = { productId: number; name: string; pictureUrl?: string | null; count: number };
export type ProductCorrelationPoint = { productId: number; name: string; pictureUrl?: string | null; clicks: number; sales: number };
export type ProductRating = { productId: number; name: string; pictureUrl?: string | null; averageRating: number; ratingsCount: number };

export type AnalyticsQuery = {
  from?: string;
  to?: string;
  interval?: 'day' | 'week' | 'month' | 'year';
  categoryIds?: number[];
  take?: number;
};

const buildParams = (q: AnalyticsQuery) => {
  const params: Record<string, string> = {};
  if (q.from) params.from = q.from;
  if (q.to) params.to = q.to;
  if (q.interval) params.interval = q.interval;
  if (q.categoryIds && q.categoryIds.length) params.categoryIds = q.categoryIds.join(',');
  if (q.take != null) params.take = String(q.take);
  return params;
};

export const analyticsApi = createApi({
  reducerPath: 'analyticsApi',
  baseQuery: baseQueryWithErrorHandling,
  endpoints: (builder) => ({
    topSold: builder.query<ProductCount[], AnalyticsQuery>({
      query: (q) => ({ url: 'analytics/top-sold', params: buildParams(q) }),
    }),
    salesTimeSeries: builder.query<TimeSeriesPoint[], AnalyticsQuery>({
      query: (q) => ({ url: 'analytics/sales-timeseries', params: buildParams(q) }),
    }),
    salesAmountTimeSeries: builder.query<TimeSeriesPoint[], AnalyticsQuery>({
      query: (q) => ({ url: 'analytics/sales-amount-timeseries', params: buildParams(q) }),
    }),
    topClicks: builder.query<ProductCount[], AnalyticsQuery>({
      query: (q) => ({ url: 'analytics/top-clicks', params: buildParams(q) }),
    }),
    clicksTimeSeries: builder.query<TimeSeriesPoint[], AnalyticsQuery>({
      query: (q) => ({ url: 'analytics/clicks-timeseries', params: buildParams(q) }),
    }),
    correlation: builder.query<ProductCorrelationPoint[], AnalyticsQuery>({
      query: (q) => ({ url: 'analytics/correlation', params: buildParams(q) }),
    }),
    topComments: builder.query<ProductCount[], AnalyticsQuery>({
      query: (q) => ({ url: 'analytics/top-comments', params: buildParams(q) }),
    }),
    topRated: builder.query<ProductRating[], AnalyticsQuery>({
      query: (q) => ({ url: 'analytics/top-rated', params: buildParams(q) }),
    }),
    allSales: builder.query<ProductCount[], AnalyticsQuery>({
      query: (q) => ({ url: 'analytics/all-sales', params: buildParams(q) }),
    }),
  }),
});

export const {
  useTopSoldQuery,
  useSalesTimeSeriesQuery,
  useSalesAmountTimeSeriesQuery,
  useTopClicksQuery,
  useClicksTimeSeriesQuery,
  useCorrelationQuery,
  useTopCommentsQuery,
  useTopRatedQuery,
  useAllSalesQuery,
} = analyticsApi;
