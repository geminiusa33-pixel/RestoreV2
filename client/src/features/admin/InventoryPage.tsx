import { Box, Button, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography, Grid, Collapse, Paper, Autocomplete, TextField, Switch } from "@mui/material";
import { Link } from "react-router-dom";
import { useAppDispatch, useAppSelector } from "../../app/store/store"
import { useFetchProductsQuery, useFetchFiltersQuery, useFetchProductDetailsQuery } from "../catalog/catalogApi";
import Filters from "../catalog/Filters";
import Search from "../catalog/Search";
import { currencyFormat, computeFinalPrice } from "../../lib/util";
import { Delete, Edit } from "@mui/icons-material";
import AppPagination from "../../app/shared/components/AppPagination";
import { setPageNumber } from "../catalog/catalogSlice";
import { useState } from "react";
import ProductForm from "./ProductForm";
import { Product } from "../../app/models/product";
import { useBulkDeleteProductsMutation, useCleanupUnusedCategoriesMutation, useDeleteProductMutation, useGetDeletedProductsQuery, usePublishProductMutation, useRestoreProductMutation, useUnpublishProductMutation } from "./adminApi";
import { skipToken } from "@reduxjs/toolkit/query";
import PageTitle from "../../app/shared/components/PageTitle";

export default function InventoryPage() {
    const productParams = useAppSelector(state => state.catalog);
    const {data, refetch} = useFetchProductsQuery(productParams);
    const {data: filtersData, isLoading: filtersLoading} = useFetchFiltersQuery();
    const dispatch = useAppDispatch();
    const [editMode, setEditMode] = useState(false);
    const [selectedProductId, setSelectedProductId] = useState<number | null>(null);
    const [deleteProduct] = useDeleteProductMutation();
    const [publishProduct] = usePublishProductMutation();
    const [unpublishProduct] = useUnpublishProductMutation();
    const [bulkDelete] = useBulkDeleteProductsMutation();
    const [cleanupUnusedCategories, { isLoading: isCleaningCategories }] = useCleanupUnusedCategoriesMutation();
    const [restoreProduct] = useRestoreProductMutation();
    const { data: deletedProducts = [], refetch: refetchDeleted } = useGetDeletedProductsQuery();

    const [openRecycle, setOpenRecycle] = useState(false);
    const [bulkCategory, setBulkCategory] = useState<{ id: number; name: string } | null>(null);

    const { data: selectedProductDetails, isLoading: isLoadingSelectedProduct } = useFetchProductDetailsQuery(
        selectedProductId ?? skipToken
    );

    const handleSelectProduct = (product: Product) => {
        setSelectedProductId(product.id);
        setEditMode(true);
    }

    const handleDeleteProduct = async (id: number) => {
        try {
            await deleteProduct(id);
            refetch();
        } catch (error) {
            console.log(error);
        }
    }

    const handlePublishProduct = async (id: number) => {
        try {
            await publishProduct(id).unwrap();
            refetch();
        } catch (error) {
            console.log(error);
        }
    }

    const handleUnpublishProduct = async (id: number) => {
        try {
            await unpublishProduct(id).unwrap();
            refetch();
        } catch (error) {
            console.log(error);
        }
    }

    const handleBulkDeleteAll = async () => {
        try {
            await bulkDelete({ deleteAll: true }).unwrap();
            refetch();
            refetchDeleted();
        } catch (error) {
            console.log(error);
        }
    }

    const handleBulkDeleteCategory = async () => {
        if (!bulkCategory?.id) return;
        try {
            await bulkDelete({ categoryId: bulkCategory.id }).unwrap();
            refetch();
            refetchDeleted();
        } catch (error) {
            console.log(error);
        }
    }

    const handleCleanupCategories = async () => {
        try {
            await cleanupUnusedCategories().unwrap();
        } catch (error) {
            console.log(error);
        }
    }

    const handleRestore = async (id: number) => {
        try {
            await restoreProduct(id).unwrap();
            refetch();
            refetchDeleted();
        } catch (error) {
            console.log(error);
        }
    }

    if (editMode && selectedProductId && (isLoadingSelectedProduct || !selectedProductDetails)) return <div>Loading...</div>

    if (editMode) return <ProductForm 
        setEditMode={setEditMode} 
        product={selectedProductId ? (selectedProductDetails ?? null) : null} 
        refetch={refetch}
        setSelectedProduct={(value) => setSelectedProductId(value ? value.id : null)}
    />
    if (!filtersData && !filtersLoading && !data) return <div>Loading...</div>

    const items = data?.items ?? [];

    const getCategoryLabel = (product: Product) => {
        const cats = (product.categories ?? []).filter(Boolean) as Array<{ id: number; name?: string; parentCategoryId?: number | null }>;
        if (!cats.length) return '—';

        const ids = new Set(cats.map(c => c.id));
        const root = cats.find(c => !c.parentCategoryId || !ids.has(c.parentCategoryId)) ?? cats[0];

        const parts: string[] = [];
        let current = root;
        for (let i = 0; i < 10 && current; i++) {
            parts.push((current.name ?? '').trim());
            const child = cats.find(c => (c.parentCategoryId ?? null) === current.id);
            if (!child) break;
            current = child;
        }

        const label = parts.filter(Boolean).join(' → ');
        return label || '—';
    }

    return (
        <>
            <Box sx={{ px: 2, pt: 2 }}>
                <PageTitle
                    title="Inventário"
                    variant="h4"
                    actions={(
                        <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                            <Button onClick={() => setOpenRecycle(s => !s)} size='large' variant='outlined'>
                                Reciclagem
                            </Button>
                            <Button onClick={() => setEditMode(true)} size='large' variant='contained'>
                                Criar Produto
                            </Button>
                        </Box>
                    )}
                />
            </Box>

            {/* Bulk actions + recycle bin */}
            <Box sx={{ px: 2, pb: 2 }}>
                <Paper sx={{ p: 2, borderRadius: 2 }}>
                    <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', alignItems: 'center' }}>
                        <Button variant="outlined" color="error" onClick={handleBulkDeleteAll}>
                            Apagar todos os produtos
                        </Button>

                        <Button variant="outlined" onClick={handleCleanupCategories} disabled={isCleaningCategories}>
                            Limpar categorias sem produtos à venda
                        </Button>

                        <Autocomplete
                            sx={{ minWidth: 280 }}
                            options={(filtersData?.categories ?? []).map(c => ({ id: c.id, name: c.name }))}
                            getOptionLabel={(o) => o.name}
                            value={bulkCategory}
                            onChange={(_e, v) => setBulkCategory(v)}
                            renderInput={(params) => <TextField {...params} label="Apagar por categoria" size="small" />}
                        />
                        <Button variant="outlined" color="error" disabled={!bulkCategory?.id} onClick={handleBulkDeleteCategory}>
                            Apagar categoria
                        </Button>
                    </Box>

                    <Collapse in={openRecycle} timeout={160}>
                        <Box sx={{ mt: 2 }}>
                            <Typography variant="h6" sx={{ mb: 1 }}>Reciclagem (restauro até 2 meses)</Typography>
                            {deletedProducts.length === 0 ? (
                                <Typography color="text.secondary">Sem produtos apagados recentemente.</Typography>
                            ) : (
                                <TableContainer component={Box}>
                                    <Table size="small">
                                        <TableHead>
                                            <TableRow>
                                                <TableCell>Produto</TableCell>
                                                <TableCell>Apagado em</TableCell>
                                                <TableCell align="right"></TableCell>
                                            </TableRow>
                                        </TableHead>
                                        <TableBody>
                                            {deletedProducts.map(p => (
                                                <TableRow key={p.id}>
                                                    <TableCell>{p.name}</TableCell>
                                                    <TableCell>{new Date(p.deletedAt).toLocaleString()}</TableCell>
                                                    <TableCell align="right">
                                                        <Button variant="outlined" onClick={() => handleRestore(p.id)}>
                                                            Restaurar
                                                        </Button>
                                                    </TableCell>
                                                </TableRow>
                                            ))}
                                        </TableBody>
                                    </Table>
                                </TableContainer>
                            )}
                        </Box>
                    </Collapse>
                </Paper>
            </Box>

            <Grid container spacing={4}>
                <Grid item xs={12} md={3} sx={{ display: { xs: 'none', md: 'block' } }}>
                    <Box sx={{ px: 2, pb: 2 }}>
                        <Search navigateTo={false} placeholder="Pesquisar no inventário" />
                    </Box>
                    {filtersData && <Filters filtersData={filtersData} />}
                </Grid>
                <Grid item xs={12} md={9}>
                    <Box sx={{ display: { xs: 'block', md: 'none' }, mb: 2 }}>
                        <Search navigateTo={false} placeholder="Pesquisar no inventário" />
                    </Box>
                    {/* Desktop/tablet: show table */}
                    <TableContainer component={Box} sx={{ display: { xs: 'none', md: 'block' } }}>
                <Table sx={{minWidth: 650, tableLayout: 'auto'}}>
                    <TableHead>
                        <TableRow>
                            <TableCell align="left">Produto</TableCell>
                            <TableCell align="left">Categoria</TableCell>
                            <TableCell align="right">Preço</TableCell>
                            <TableCell align="center">Desconto</TableCell>
                            <TableCell align="center">Stock</TableCell>
                            <TableCell align="center">Visível</TableCell>
                            <TableCell align="right"></TableCell>
                        </TableRow>
                    </TableHead>
                    <TableBody>
                        {items.map(product => {
                            const hasDiscount = !!product.discountPercentage && product.discountPercentage > 0;
                            const finalPrice = hasDiscount ? computeFinalPrice(product.price, product.discountPercentage) : product.price;

                            return (
                                <TableRow
                                    key={product.id}
                                    sx={{ '&:last-child td, &:last-child th': {border: 0} }}
                                >
                                    <TableCell align="left" sx={{ width: 420 }}>
                                        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
                                            <Box sx={{ flex: '0 0 auto' }}>
                                                <img
                                                    src={product.pictureUrl}
                                                    alt={product.name}
                                                    style={{ height: 50, width: 50, objectFit: 'cover', borderRadius: 8 }}
                                                />
                                            </Box>
                                            <Box sx={{ flex: 1, minWidth: 0 }}>
                                                <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 2, p: 1 }}>
                                                    <Typography sx={{ whiteSpace: 'normal', wordBreak: 'break-word' }}>
                                                        {product.name}
                                                    </Typography>
                                                    {product.isPublished === false && (
                                                        <Typography variant="caption" color="text.secondary">
                                                            Rascunho (por aprovar)
                                                        </Typography>
                                                    )}
                                                </Box>
                                            </Box>
                                        </Box>
                                    </TableCell>

                                    <TableCell align="left" sx={{ width: 220 }}>
                                        <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 2, p: 1 }}>
                                            <Typography sx={{ whiteSpace: 'normal', wordBreak: 'break-word' }}>
                                                {getCategoryLabel(product)}
                                            </Typography>
                                        </Box>
                                    </TableCell>

                                    <TableCell align="right">
                                        <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 2, p: 1 }}>
                                            {hasDiscount ? (
                                                <Box sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'flex-end', gap: 1 }}>
                                                    <span style={{ textDecoration: 'line-through', color: 'gray' }}>{currencyFormat(product.price)}</span>
                                                    <span style={{ color: 'crimson', fontWeight: 700 }}>{currencyFormat(finalPrice)}</span>
                                                </Box>
                                            ) : (
                                                <Box sx={{ textAlign: 'right', whiteSpace: 'normal', wordBreak: 'break-word' }}>{currencyFormat(product.price)}</Box>
                                            )}
                                        </Box>
                                    </TableCell>

                                    <TableCell align="center">
                                        <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 2, p: 1, display: 'inline-block', whiteSpace: 'normal' }}>
                                            {hasDiscount ? `-${product.discountPercentage}%` : '—'}
                                        </Box>
                                    </TableCell>

                                    <TableCell align="center">
                                        <Switch
                                            checked={product.isPublished === true}
                                            onChange={(_e, checked) => {
                                                if (checked) handlePublishProduct(product.id);
                                                else handleUnpublishProduct(product.id);
                                            }}
                                        />
                                    </TableCell>

                                    <TableCell align="center">
                                        <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 2, p: 1, display: 'inline-block', whiteSpace: 'normal' }}>
                                            {product.quantityInStock}
                                        </Box>
                                    </TableCell>

                                    <TableCell align="right" sx={{ width: 280 }}>
                                        <Box sx={{ display: 'flex', justifyContent: 'flex-end', flexWrap: 'wrap', gap: 1 }}>
                                            <Button
                                                component={Link}
                                                to={`/catalog/${product.id}?preview=1`}
                                                target="_blank"
                                                rel="noopener noreferrer"
                                                variant='outlined'
                                                size='small'
                                                sx={{ whiteSpace: 'nowrap' }}
                                            >
                                                Pré-visualizar
                                            </Button>

                                            {product.isPublished === false && (
                                                <Button
                                                    onClick={() => handlePublishProduct(product.id)}
                                                    variant='contained'
                                                    color='success'
                                                    size='small'
                                                    sx={{ whiteSpace: 'nowrap' }}
                                                >
                                                    Aprovar
                                                </Button>
                                            )}

                                            <Button
                                                onClick={() => handleSelectProduct(product)}
                                                startIcon={<Edit />}
                                                variant='contained'
                                                color='primary'
                                                size='small'
                                                sx={{ minWidth: 0, px: 1.25, whiteSpace: 'nowrap' }}
                                            >
                                                Editar
                                            </Button>

                                            <Button
                                                onClick={() => handleDeleteProduct(product.id)}
                                                startIcon={<Delete />}
                                                variant='contained'
                                                color="error"
                                                size='small'
                                                sx={{ minWidth: 0, px: 1.25, whiteSpace: 'nowrap' }}
                                            >
                                                Remover
                                            </Button>
                                        </Box>
                                    </TableCell>
                                </TableRow>
                            )
                        })}
                    </TableBody>
                </Table>
                <Box sx={{p: 3}}>
                    {data?.pagination && data.items.length > 0 && (
                        <AppPagination 
                            metadata={data.pagination}
                            onPageChange={(page: number) => dispatch(setPageNumber(page))}
                        />
                    )}
                </Box>
            </TableContainer>

            {/* Mobile: show cards */}
            <Box sx={{ display: { xs: 'block', md: 'none' }, mt: 2 }}>
                <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
                    <Grid container spacing={2}>
                        {items.map(product => {
                            const hasDiscount = !!product.discountPercentage && product.discountPercentage > 0;
                            const finalPrice = hasDiscount ? computeFinalPrice(product.price, product.discountPercentage) : product.price;
                            return (
                                <Grid item xs={6} sm={4} key={product.id}>
                                    <Box sx={{p: 1}}>
                                        <Box display='flex' flexDirection='column' alignItems='center'>
                                            <img src={product.pictureUrl} alt={product.name} style={{width: '100%', height: 100, objectFit: 'cover', borderRadius: 8}} />
                                            <Box sx={{ mt: 1, width: '100%' }}>
                                                <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 2, p: 1 }}>
                                                    <Typography
                                                        variant='subtitle2'
                                                        sx={{ textAlign: 'center', width: '100%', whiteSpace: 'normal', wordBreak: 'break-word' }}
                                                    >
                                                        {product.name}
                                                    </Typography>
                                                    {product.isPublished === false && (
                                                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', textAlign: 'center', mt: 0.5 }}>
                                                            Rascunho (por aprovar)
                                                        </Typography>
                                                    )}
                                                </Box>

                                                <Typography variant='caption' color='text.secondary' sx={{ display: 'block', textAlign: 'center', mt: 0.75 }}>
                                                    {getCategoryLabel(product)}
                                                </Typography>

                                                <Box sx={{ mt: 1, textAlign: 'center' }}>
                                                    {hasDiscount ? (
                                                        <>
                                                            <Typography variant='body2' sx={{ textDecoration: 'line-through', color: 'text.secondary' }}>{currencyFormat(product.price)}</Typography>
                                                            <Typography variant='subtitle2' sx={{color: 'crimson', fontWeight: 700}}>{currencyFormat(finalPrice)}</Typography>
                                                            <Typography variant='caption' color='text.secondary'>{`-${product.discountPercentage}%`}</Typography>
                                                        </>
                                                    ) : (
                                                        <Typography variant='subtitle2' sx={{color: 'secondary.main'}}>{currencyFormat(product.price)}</Typography>
                                                    )}
                                                </Box>
                                                <Box sx={{ mt: 1, textAlign: 'center' }}>
                                                    <Typography variant='caption' color='text.secondary'>Stock: {product.quantityInStock}</Typography>
                                                </Box>
                                                <Box sx={{display: 'flex', gap:1, mt:1, justifyContent: 'center', flexWrap: 'wrap'}}>
                                                    <Button
                                                        size='small'
                                                        component={Link}
                                                        to={`/catalog/${product.id}?preview=1`}
                                                        target="_blank"
                                                        rel="noopener noreferrer"
                                                        variant='outlined'
                                                    >
                                                        Pré-visualizar
                                                    </Button>
                                                    {product.isPublished === false && (
                                                        <Button
                                                            size='small'
                                                            color='success'
                                                            variant='contained'
                                                            onClick={() => handlePublishProduct(product.id)}
                                                        >
                                                            Aprovar
                                                        </Button>
                                                    )}
                                                    <Button size='small' onClick={() => handleSelectProduct(product)} startIcon={<Edit />} variant='contained' color='primary'>Editar</Button>
                                                    <Button size='small' color='error' onClick={() => handleDeleteProduct(product.id)} startIcon={<Delete />}>Excluir</Button>
                                                </Box>
                                            </Box>
                                        </Box>
                                    </Box>
                                </Grid>
                            )
                        })}
                    </Grid>
                </Box>

                <Box sx={{p: 2}}>
                    {data?.pagination && data.items.length > 0 && (
                        <AppPagination 
                            metadata={data.pagination}
                            onPageChange={(page: number) => dispatch(setPageNumber(page))}
                        />
                    )}
                </Box>
            </Box>
                </Grid>
            </Grid>
        </>
    )
}