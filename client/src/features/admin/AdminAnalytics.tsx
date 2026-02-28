import { Box, FormControl, InputLabel, MenuItem, Select, SelectChangeEvent, Typography, Paper, Grid, Chip, Autocomplete, TextField, Table, TableBody, TableCell, TableContainer, TableHead, TableRow } from '@mui/material';
import { subDays, subMonths, subYears } from 'date-fns';
import { useLayoutEffect, useMemo, useRef, useState } from 'react';
import {
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  Tooltip,
  XAxis,
  YAxis,
  BarChart,
  Bar,
  ScatterChart,
  Scatter,
} from 'recharts';
import { useFetchFiltersQuery } from '../catalog/catalogApi';
import {
  useAllSalesQuery,
  useClicksTimeSeriesQuery,
  useCorrelationQuery,
  useSalesAmountTimeSeriesQuery,
  useSalesTimeSeriesQuery,
  useTopCommentsQuery,
  useTopClicksQuery,
  useTopRatedQuery,
  useTopSoldQuery,
  type AnalyticsQuery,
} from './analyticsApi';
import { currencyFormat } from '../../lib/util';
import PageTitle from '../../app/shared/components/PageTitle';

const toIso = (d: Date) => d.toISOString();

type Preset = '7d' | '30d' | '3m' | '1y';

type ChartSize = { width: number; height: number };

function MeasuredChart({
  minHeight,
  children,
}: {
  minHeight: number;
  children: (size: ChartSize) => React.ReactNode;
}) {
  const ref = useRef<HTMLDivElement | null>(null);
  const [size, setSize] = useState<ChartSize | null>(null);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;

    const update = () => {
      const rect = el.getBoundingClientRect();
      const width = Math.floor(rect.width);
      const height = Math.floor(rect.height);

      if (width > 0 && height > 0) setSize({ width, height });
      else setSize(null);
    };

    update();

    const ro = new ResizeObserver(() => update());
    ro.observe(el);
    window.addEventListener('resize', update);

    return () => {
      ro.disconnect();
      window.removeEventListener('resize', update);
    };
  }, []);

  return (
    <Box ref={ref} sx={{ flex: 1, minWidth: 0, minHeight }}>
      {size ? children(size) : <Box sx={{ width: '100%', height: minHeight }} />}
    </Box>
  );
}

export default function AdminAnalytics() {
  const [preset, setPreset] = useState<Preset>('30d');
  const [interval, setInterval] = useState<'day' | 'week' | 'month'>('day');
  const [categoryIds, setCategoryIds] = useState<number[]>([]);

  const { data: filters } = useFetchFiltersQuery();

  const range = useMemo(() => {
    const end = new Date();
    const start =
      preset === '7d' ? subDays(end, 7) :
      preset === '30d' ? subDays(end, 30) :
      preset === '3m' ? subMonths(end, 3) :
      subYears(end, 1);

    return { from: toIso(start), to: toIso(end) };
  }, [preset]);

  const query: AnalyticsQuery = useMemo(() => ({
    ...range,
    interval,
    categoryIds,
    take: 30,
  }), [range, interval, categoryIds]);

  const { data: topSold = [] } = useTopSoldQuery(query);
  const { data: topClicks = [] } = useTopClicksQuery(query);
  const { data: topComments = [] } = useTopCommentsQuery(query);
  const { data: topRated = [] } = useTopRatedQuery(query);
  const { data: allSales = [] } = useAllSalesQuery({ ...query, take: 5000 });
  const { data: salesSeries = [] } = useSalesTimeSeriesQuery(query);
  const { data: salesAmountSeries = [] } = useSalesAmountTimeSeriesQuery(query);
  const { data: clicksSeries = [] } = useClicksTimeSeriesQuery(query);
  const { data: corr = [] } = useCorrelationQuery({ ...query, take: 80 });

  const categoryOptions = (filters?.categories ?? []).map(c => ({ id: c.id, name: c.name }));

  const onPresetChange = (e: SelectChangeEvent) => setPreset(e.target.value as Preset);
  type Interval = 'day' | 'week' | 'month';
  const isInterval = (v: string): v is Interval => v === 'day' || v === 'week' || v === 'month';
  const onIntervalChange = (e: SelectChangeEvent) => {
    const v = String(e.target.value);
    if (isInterval(v)) setInterval(v);
  };

  return (
    <Box sx={{ p: { xs: 2, md: 3 } }}>
      <PageTitle title="Analytics" variant="h4" />

      <Paper sx={{ p: 2, mb: 2 }}>
        <Grid container spacing={2} alignItems="center">
          <Grid item xs={12} md={3}>
            <FormControl fullWidth size="small">
              <InputLabel>Período</InputLabel>
              <Select label="Período" value={preset} onChange={onPresetChange}>
                <MenuItem value="7d">Últimos 7 dias</MenuItem>
                <MenuItem value="30d">Últimos 30 dias</MenuItem>
                <MenuItem value="3m">Últimos 3 meses</MenuItem>
                <MenuItem value="1y">Último ano</MenuItem>
              </Select>
            </FormControl>
          </Grid>

          <Grid item xs={12} md={3}>
            <FormControl fullWidth size="small">
              <InputLabel>Agregação</InputLabel>
              <Select label="Agregação" value={interval} onChange={onIntervalChange}>
                <MenuItem value="day">Dia</MenuItem>
                <MenuItem value="week">Semana</MenuItem>
                <MenuItem value="month">Mês</MenuItem>
              </Select>
            </FormControl>
          </Grid>

          <Grid item xs={12} md={6}>
            <Autocomplete
              multiple
              options={categoryOptions}
              getOptionLabel={(o) => o.name}
              onChange={(_e, v) => setCategoryIds(v.map(x => x.id))}
              renderTags={(value, getTagProps) =>
                value.map((option, index) => (
                  <Chip label={option.name} {...getTagProps({ index })} key={option.id} />
                ))
              }
              renderInput={(params) => <TextField {...params} label="Categorias" placeholder="Filtrar por categoria" size="small" />}
            />
          </Grid>
        </Grid>
      </Paper>

      <Grid container spacing={2}>
        <Grid item xs={12} md={6}>
          <Paper sx={{ p: 2, height: 360, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Mais vendidos (Top 30)</Typography>
            <MeasuredChart minHeight={260}>
              {({ width, height }) => (
                <BarChart width={width} height={height} data={topSold} layout="vertical" margin={{ left: 40, right: 20, top: 10, bottom: 10 }}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis type="number" />
                  <YAxis type="category" dataKey="name" width={140} />
                  <Tooltip />
                  <Bar dataKey="count" fill="#1976d2" />
                </BarChart>
              )}
            </MeasuredChart>
          </Paper>
        </Grid>

        <Grid item xs={12} md={6}>
          <Paper sx={{ p: 2, height: 360, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Mais clicados (Top 30)</Typography>
            <MeasuredChart minHeight={260}>
              {({ width, height }) => (
                <BarChart width={width} height={height} data={topClicks} layout="vertical" margin={{ left: 40, right: 20, top: 10, bottom: 10 }}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis type="number" />
                  <YAxis type="category" dataKey="name" width={140} />
                  <Tooltip />
                  <Bar dataKey="count" fill="#9c27b0" />
                </BarChart>
              )}
            </MeasuredChart>
          </Paper>
        </Grid>

        <Grid item xs={12} md={6}>
          <Paper sx={{ p: 2, height: 360, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Mais comentados (Top 30)</Typography>
            <MeasuredChart minHeight={260}>
              {({ width, height }) => (
                <BarChart width={width} height={height} data={topComments} layout="vertical" margin={{ left: 40, right: 20, top: 10, bottom: 10 }}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis type="number" />
                  <YAxis type="category" dataKey="name" width={140} />
                  <Tooltip />
                  <Bar dataKey="count" fill="#2e7d32" />
                </BarChart>
              )}
            </MeasuredChart>
          </Paper>
        </Grid>

        <Grid item xs={12} md={6}>
          <Paper sx={{ p: 2, height: 360, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Melhor classificados (Top 30)</Typography>
            <MeasuredChart minHeight={260}>
              {({ width, height }) => (
                <BarChart width={width} height={height} data={topRated} layout="vertical" margin={{ left: 40, right: 20, top: 10, bottom: 10 }}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis type="number" domain={[0, 5]} tickCount={6} />
                  <YAxis type="category" dataKey="name" width={140} />
                  <Tooltip
                    formatter={(v: any, name: any, props: any) => {
                      if (name === 'averageRating') {
                        const avg = typeof v === 'number' ? v : Number(v);
                        const cnt = props?.payload?.ratingsCount;
                        return [`${avg.toFixed(2)} (${cnt ?? 0} avaliações)`, 'Rating'];
                      }
                      return [v, name];
                    }}
                  />
                  <Bar dataKey="averageRating" fill="#ed6c02" />
                </BarChart>
              )}
            </MeasuredChart>
          </Paper>
        </Grid>

        <Grid item xs={12} md={6}>
          <Paper sx={{ p: 2, height: 320, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Vendas no tempo</Typography>
            <MeasuredChart minHeight={220}>
              {({ width, height }) => (
                <LineChart width={width} height={height} data={salesSeries} margin={{ left: 10, right: 20, top: 10, bottom: 10 }}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis dataKey="date" hide />
                  <YAxis />
                  <Tooltip />
                  <Legend />
                  <Line type="monotone" dataKey="value" name="Vendas (qty)" stroke="#2e7d32" dot={false} />
                </LineChart>
              )}
            </MeasuredChart>
          </Paper>
        </Grid>

        <Grid item xs={12} md={6}>
          <Paper sx={{ p: 2, height: 320, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Total de vendas (€)</Typography>
            <MeasuredChart minHeight={220}>
              {({ width, height }) => (
                <LineChart width={width} height={height} data={salesAmountSeries} margin={{ left: 10, right: 20, top: 10, bottom: 10 }}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis dataKey="date" hide />
                  <YAxis tickFormatter={(v) => currencyFormat(Number(v))} />
                  <Tooltip formatter={(v) => currencyFormat(Number(v))} />
                  <Legend />
                  <Line type="monotone" dataKey="value" name="Vendas (€)" stroke="#0288d1" dot={false} />
                </LineChart>
              )}
            </MeasuredChart>
          </Paper>
        </Grid>

        <Grid item xs={12} md={6}>
          <Paper sx={{ p: 2, height: 320, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Clicks no tempo</Typography>
            <MeasuredChart minHeight={220}>
              {({ width, height }) => (
                <LineChart width={width} height={height} data={clicksSeries} margin={{ left: 10, right: 20, top: 10, bottom: 10 }}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis dataKey="date" hide />
                  <YAxis />
                  <Tooltip />
                  <Legend />
                  <Line type="monotone" dataKey="value" name="Clicks" stroke="#ed6c02" dot={false} />
                </LineChart>
              )}
            </MeasuredChart>
          </Paper>
        </Grid>

        <Grid item xs={12}>
          <Paper sx={{ p: 2, height: 420, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Correlação: clicks vs vendas</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
              Cada ponto é um produto no período selecionado.
            </Typography>
            <MeasuredChart minHeight={300}>
              {({ width, height }) => (
                <ScatterChart width={width} height={height} margin={{ left: 10, right: 20, top: 10, bottom: 10 }}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis type="number" dataKey="clicks" name="Clicks" />
                  <YAxis type="number" dataKey="sales" name="Vendas" />
                  <Tooltip cursor={{ strokeDasharray: '3 3' }} />
                  <Scatter name="Produtos" data={corr} fill="#1976d2" />
                </ScatterChart>
              )}
            </MeasuredChart>
          </Paper>
        </Grid>

        <Grid item xs={12}>
          <Paper sx={{ p: 2 }}>
            <Typography variant="h6" sx={{ mb: 1 }}>Todos os produtos: nº de vendas</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
              Total histórico (SalesCount) por produto.
            </Typography>

            <TableContainer sx={{ maxHeight: 520 }}>
              <Table stickyHeader size="small" aria-label="Tabela de vendas por produto">
                <TableHead>
                  <TableRow>
                    <TableCell>Produto</TableCell>
                    <TableCell align="right">Vendas</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {allSales.map((p) => (
                    <TableRow key={p.productId} hover>
                      <TableCell>{p.name}</TableCell>
                      <TableCell align="right">{p.count}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </TableContainer>
          </Paper>
        </Grid>
      </Grid>
    </Box>
  );
}
