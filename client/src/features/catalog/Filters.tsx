import { Box, Button, Paper, Typography, Collapse, Checkbox } from "@mui/material";
import { useTheme } from "@mui/material/styles";
import { useEffect, useState } from 'react';
// Search moved to NavBar for global access
import RadioButtonGroup from "../../app/shared/components/RadioButtonGroup";
import { useAppDispatch, useAppSelector } from "../../app/store/store";
import { resetParams, setOrderBy, setAnos, setGeneros, setCategories, setCampaigns, setMarcas, setModelos, setTipos, setCapacidades, setCores, setMateriais, setTamanhos } from "./catalogSlice";
import CheckboxButtons from "../../app/shared/components/CheckboxButtons";
import genres from "../../lib/genres";

const sortOptions = [
    { value: 'name', label: 'Alfabeticamente' },
    { value: 'clicksDesc', label: 'Mais procurados' },
    { value: 'salesDesc', label: 'Mais vendidos' },
    { value: 'priceDesc', label: 'Preço: Maior para menor' },
    { value: 'price', label: 'Preço: Menor para maior' },
    { value: 'discountDesc', label: 'Desconto: Maior para menor' },
    { value: 'discount', label: 'Desconto: Menor para maior' },
    { value: 'yearDesc', label: 'Ano: Mais recente' },
    { value: 'year', label: 'Ano: Mais antigo' }
]

type Props = {
    filtersData?: {
        generos: string[];
        anos: number[];
        categories?: { id: number; name: string; parentCategoryId?: number | null }[];
        campaigns?: { id: number; name: string }[];
        marcas?: string[];
        modelos?: string[];
        tipos?: string[];
        capacidades?: string[];
        cores?: string[];
        materiais?: string[];
        tamanhos?: string[];
    }
    onChangeComplete?: () => void
}

export default function Filters({filtersData: data, onChangeComplete}: Props) {
    const { orderBy, anos, generos, categoryIds, campaignIds, marcas, modelos, tipos, capacidades, cores, materiais, tamanhos } = useAppSelector(state => state.catalog);
    const dispatch = useAppDispatch();
    const theme = useTheme();

    const [openSort, setOpenSort] = useState(false);
    const [openGenres, setOpenGenres] = useState(false);
    const [openTypes, setOpenTypes] = useState(false);
    const [openCategories, setOpenCategories] = useState(false);
    const [openCampaigns, setOpenCampaigns] = useState(false);
    const [openMarcas, setOpenMarcas] = useState(false);
    const [openModelos, setOpenModelos] = useState(false);
    const [openTipos, setOpenTipos] = useState(false);
    const [openCapacidades, setOpenCapacidades] = useState(false);
    const [openCores, setOpenCores] = useState(false);
    const [openMateriais, setOpenMateriais] = useState(false);
    const [openTamanhos, setOpenTamanhos] = useState(false);

    const selectedCount = (arr?: Array<string | number>) => (arr && arr.length) ? arr.length : 0;

    const mapGeneroLabel = (g: string) => {
        if (!g) return g;
        // if numeric, try to map to shared genres list
        const asNum = parseInt(g, 10);
        if (!Number.isNaN(asNum)) {
            // try both zero-based and one-based indexes
            if (genres[asNum]) return genres[asNum];
            if (genres[asNum - 1]) return genres[asNum - 1];
        }
        return g;
    }

    const generoItems = (data?.generos ?? []).map(g => ({ value: g, label: mapGeneroLabel(g) }));
    const categories = (data?.categories ?? []);
    const categoryItems = categories.map(c => ({ value: String(c.id), label: c.name }));
    const campaignItems = (data?.campaigns ?? []).map(c => ({ value: String(c.id), label: c.name }));
    const marcaItems = (data?.marcas ?? []).map(m => ({ value: m, label: m }));
    const modeloItems = (data?.modelos ?? []).map(m => ({ value: m, label: m }));
    const tipoItems = (data?.tipos ?? []).map(t => ({ value: t, label: t }));
    const capacidadeItems = (data?.capacidades ?? []).map(c => ({ value: c, label: c }));
    const corItems = (data?.cores ?? []).map(c => ({ value: c, label: c }));
    const materialItems = (data?.materiais ?? []).map(m => ({ value: m, label: m }));
    const tamanhoItems = (data?.tamanhos ?? []).map(t => ({ value: t, label: t }));
    // Show the genero (genre) filter only when the user has selected a category
    // that indicates books (contains 'livro' in its name). This keeps the UI
    // simpler for non-book categories.
    const selectedCategoryNames = (data?.categories ?? []).filter(c => (categoryIds ?? []).includes(c.id)).map(c => c.name.toLowerCase());
    const showGenero = selectedCategoryNames.some(n => n.includes('livro'));
    const showTech = selectedCategoryNames.some(n => n.includes('tecn'));
    const showClothing = selectedCategoryNames.some(n => n.includes('vest') || n.includes('roup'));
    const showToy = selectedCategoryNames.some(n => n.includes('brinqu'));

    const showMarca = (showTech || showClothing || showToy) && (data?.marcas?.length ?? 0) > 0;
    const showModelo = showTech && (data?.modelos?.length ?? 0) > 0;
    const showTipo = showTech && (data?.tipos?.length ?? 0) > 0;
    const showCapacidade = showTech && (data?.capacidades?.length ?? 0) > 0;
    const showCor = showClothing && (data?.cores?.length ?? 0) > 0;
    const showMaterial = (showClothing || showToy) && (data?.materiais?.length ?? 0) > 0;
    const showTamanho = showClothing && (data?.tamanhos?.length ?? 0) > 0;

    // Prevent hidden "book-only" filters from staying applied when user leaves Livros
    useEffect(() => {
        if (!showGenero) {
            if ((generos?.length ?? 0) > 0) dispatch(setGeneros([]));
            if ((anos?.length ?? 0) > 0) dispatch(setAnos([]));
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [showGenero]);

    // --- Category tree (expandable) ---
    const categoryById = new Map<number, { id: number; name: string; parentCategoryId?: number | null }>();
    for (const c of categories) categoryById.set(c.id, c);

    const childrenByParent = new Map<number | null, Array<{ id: number; name: string; parentCategoryId?: number | null }>>();
    for (const c of categories) {
        const pid = (c.parentCategoryId ?? null);
        const arr = childrenByParent.get(pid) ?? [];
        arr.push(c);
        childrenByParent.set(pid, arr);
    }
    // stable order
    for (const [, arr] of childrenByParent) {
        arr.sort((a, b) => (a.name ?? '').localeCompare((b.name ?? ''), 'pt', { sensitivity: 'base' }));
    }

    const [expandedCategories, setExpandedCategories] = useState<Record<number, boolean>>({});

    // auto-expand ancestors of selected categories so the user can see what is selected
    useEffect(() => {
        const next: Record<number, boolean> = { ...expandedCategories };
        for (const selectedId of (categoryIds ?? [])) {
            let current = categoryById.get(selectedId);
            for (let i = 0; i < 10 && current; i++) {
                const pid = current.parentCategoryId ?? null;
                if (!pid) break;
                next[pid] = true;
                current = categoryById.get(pid);
            }
        }
        setExpandedCategories(next);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [categories.length, (categoryIds ?? []).join(',')]);

    const toggleExpanded = (id: number) => setExpandedCategories(prev => ({ ...prev, [id]: !prev[id] }));

    const toggleCategoryChecked = (id: number) => {
        const set = new Set<number>(categoryIds ?? []);
        if (set.has(id)) set.delete(id); else set.add(id);
        dispatch(setCategories(Array.from(set)));
        onChangeComplete?.();
    };

    const renderCategoryNode = (node: { id: number; name: string }, level: number) => {
        const kids = childrenByParent.get(node.id) ?? [];
        const hasKids = kids.length > 0;
        const expanded = !!expandedCategories[node.id];
        const checked = (categoryIds ?? []).includes(node.id);

        return (
            <Box key={node.id}>
                <Box
                    sx={{
                        display: 'flex',
                        alignItems: 'center',
                        gap: 1,
                        pl: Math.min(level, 6) * 2,
                        py: 0.25,
                        borderRadius: 1,
                        cursor: hasKids ? 'pointer' : 'default'
                    }}
                    onClick={() => { if (hasKids) toggleExpanded(node.id); }}
                >
                    {hasKids ? (
                        <Box component='span' sx={{ width: 16, transform: expanded ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms', userSelect: 'none' }}>›</Box>
                    ) : (
                        <Box component='span' sx={{ width: 16 }} />
                    )}

                    <Checkbox
                        size="small"
                        checked={checked}
                        onClick={(e) => e.stopPropagation()}
                        onChange={() => toggleCategoryChecked(node.id)}
                    />

                    <Typography
                        sx={{
                            whiteSpace: 'nowrap',
                            overflow: 'hidden',
                            textOverflow: 'ellipsis'
                        }}
                        onClick={(e) => {
                            // If no kids, clicking label toggles the checkbox
                            if (!hasKids) {
                                e.stopPropagation();
                                toggleCategoryChecked(node.id);
                            }
                        }}
                    >
                        {node.name}
                    </Typography>
                </Box>

                {hasKids && (
                    <Collapse in={expanded} timeout={160}>
                        <Box>
                            {kids.map(k => renderCategoryNode(k, level + 1))}
                        </Box>
                    </Collapse>
                )}
            </Box>
        );
    };

    return (
        <Box sx={{ width: '100%' }}>
            {/* Desktop: list of pill rows that expand */}
            <Box sx={{ display: { xs: 'none', md: 'block' } }}>
                <Paper sx={{ p: 1, borderRadius: 2, boxShadow: 1, bgcolor: 'background.paper' }}>
                    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
                        {/* Sort */}
                        <Box
                            onClick={() => setOpenSort(s => !s)}
                            sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                        >
                            <Typography sx={{ fontWeight: 700 }}>Ordenar</Typography>
                            <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                <Typography color='text.secondary' sx={{ whiteSpace: 'nowrap' }}>
                                    {orderBy === 'name' ? 'Alfabético'
                                        : orderBy === 'clicksDesc' ? 'Mais procurados'
                                        : orderBy === 'salesDesc' ? 'Mais vendidos'
                                        : orderBy === 'price' ? 'Preço: Menor para maior'
                                        : orderBy === 'priceDesc' ? 'Preço: Maior para menor'
                                        : orderBy === 'discountDesc' ? 'Desconto: Maior para menor'
                                        : orderBy === 'discount' ? 'Desconto: Menor para maior'
                                        : orderBy === 'yearDesc' ? 'Ano: Mais recente'
                                        : orderBy === 'year' ? 'Ano: Mais antigo' : 'Todos'}
                                </Typography>
                                <Box component='span' sx={{ transform: openSort ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                            </Box>
                        </Box>
                        <Collapse in={openSort} timeout={160}>
                            <Box sx={{ p: 2 }}>
                                <RadioButtonGroup selectedValue={orderBy} options={sortOptions} onChange={e => { dispatch(setOrderBy(e.target.value)); onChangeComplete?.(); }} />
                            </Box>
                        </Collapse>

                        {/* Categories */}
                        <Box
                            onClick={() => setOpenCategories(s => !s)}
                            sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                        >
                            <Typography sx={{ fontWeight: 700 }}>Categorias</Typography>
                            <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                <Typography color='text.secondary'>{selectedCount(categoryIds) > 0 ? `${selectedCount(categoryIds)} selecionadas` : 'Todas'}</Typography>
                                <Box component='span' sx={{ transform: openCategories ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                            </Box>
                        </Box>
                        <Collapse in={openCategories} timeout={160}>
                            <Box sx={{ p: 2 }}>
                                {categories.length === 0 ? (
                                    <Typography color='text.secondary'>Sem categorias.</Typography>
                                ) : (
                                    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.25 }}>
                                        {(childrenByParent.get(null) ?? [])
                                            .filter(c => !c.parentCategoryId || !categoryById.has(c.parentCategoryId))
                                            .map(c => renderCategoryNode(c, 0))}
                                    </Box>
                                )}
                            </Box>
                        </Collapse>

                        {/* Generos (only shown when user selects a Books category) */}
                        {showGenero && (
                            <>
                                <Box
                                    onClick={() => setOpenGenres(s => !s)}
                                    sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                                >
                                    <Typography sx={{ fontWeight: 700 }}>Género</Typography>
                                    <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                        <Typography color='text.secondary'>{selectedCount(generos) > 0 ? `${selectedCount(generos)} selecionados` : 'Todos'}</Typography>
                                        <Box component='span' sx={{ transform: openGenres ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                                    </Box>
                                </Box>
                                <Collapse in={openGenres} timeout={160}>
                                    <Box sx={{ p: 2 }}>
                                        <CheckboxButtons items={generoItems} checked={(generos ?? [])} onChange={(items: string[]) => { dispatch(setGeneros(items)); onChangeComplete?.(); }} />
                                    </Box>
                                </Collapse>
                            </>
                        )}

                        {/* Marca */}
                        {showMarca && (
                            <>
                                <Box
                                    onClick={() => setOpenMarcas(s => !s)}
                                    sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                                >
                                    <Typography sx={{ fontWeight: 700 }}>Marca</Typography>
                                    <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                        <Typography color='text.secondary'>{selectedCount(marcas) > 0 ? `${selectedCount(marcas)} selecionadas` : 'Todas'}</Typography>
                                        <Box component='span' sx={{ transform: openMarcas ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                                    </Box>
                                </Box>
                                <Collapse in={openMarcas} timeout={160}>
                                    <Box sx={{ p: 2 }}>
                                        <CheckboxButtons items={marcaItems} checked={(marcas ?? [])} onChange={(items: string[]) => { dispatch(setMarcas(items)); onChangeComplete?.(); }} />
                                    </Box>
                                </Collapse>
                            </>
                        )}

                        {/* Tecnologia */}
                        {showTipo && (
                            <>
                                <Box
                                    onClick={() => setOpenTipos(s => !s)}
                                    sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                                >
                                    <Typography sx={{ fontWeight: 700 }}>Tipo</Typography>
                                    <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                        <Typography color='text.secondary'>{selectedCount(tipos) > 0 ? `${selectedCount(tipos)} selecionados` : 'Todos'}</Typography>
                                        <Box component='span' sx={{ transform: openTipos ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                                    </Box>
                                </Box>
                                <Collapse in={openTipos} timeout={160}>
                                    <Box sx={{ p: 2 }}>
                                        <CheckboxButtons items={tipoItems} checked={(tipos ?? [])} onChange={(items: string[]) => { dispatch(setTipos(items)); onChangeComplete?.(); }} />
                                    </Box>
                                </Collapse>
                            </>
                        )}

                        {showModelo && (
                            <>
                                <Box
                                    onClick={() => setOpenModelos(s => !s)}
                                    sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                                >
                                    <Typography sx={{ fontWeight: 700 }}>Modelo</Typography>
                                    <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                        <Typography color='text.secondary'>{selectedCount(modelos) > 0 ? `${selectedCount(modelos)} selecionados` : 'Todos'}</Typography>
                                        <Box component='span' sx={{ transform: openModelos ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                                    </Box>
                                </Box>
                                <Collapse in={openModelos} timeout={160}>
                                    <Box sx={{ p: 2 }}>
                                        <CheckboxButtons items={modeloItems} checked={(modelos ?? [])} onChange={(items: string[]) => { dispatch(setModelos(items)); onChangeComplete?.(); }} />
                                    </Box>
                                </Collapse>
                            </>
                        )}

                        {showCapacidade && (
                            <>
                                <Box
                                    onClick={() => setOpenCapacidades(s => !s)}
                                    sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                                >
                                    <Typography sx={{ fontWeight: 700 }}>Capacidade</Typography>
                                    <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                        <Typography color='text.secondary'>{selectedCount(capacidades) > 0 ? `${selectedCount(capacidades)} selecionados` : 'Todas'}</Typography>
                                        <Box component='span' sx={{ transform: openCapacidades ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                                    </Box>
                                </Box>
                                <Collapse in={openCapacidades} timeout={160}>
                                    <Box sx={{ p: 2 }}>
                                        <CheckboxButtons items={capacidadeItems} checked={(capacidades ?? [])} onChange={(items: string[]) => { dispatch(setCapacidades(items)); onChangeComplete?.(); }} />
                                    </Box>
                                </Collapse>
                            </>
                        )}

                        {/* Vestuário */}
                        {showCor && (
                            <>
                                <Box
                                    onClick={() => setOpenCores(s => !s)}
                                    sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                                >
                                    <Typography sx={{ fontWeight: 700 }}>Cor</Typography>
                                    <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                        <Typography color='text.secondary'>{selectedCount(cores) > 0 ? `${selectedCount(cores)} selecionadas` : 'Todas'}</Typography>
                                        <Box component='span' sx={{ transform: openCores ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                                    </Box>
                                </Box>
                                <Collapse in={openCores} timeout={160}>
                                    <Box sx={{ p: 2 }}>
                                        <CheckboxButtons items={corItems} checked={(cores ?? [])} onChange={(items: string[]) => { dispatch(setCores(items)); onChangeComplete?.(); }} />
                                    </Box>
                                </Collapse>
                            </>
                        )}

                        {showMaterial && (
                            <>
                                <Box
                                    onClick={() => setOpenMateriais(s => !s)}
                                    sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                                >
                                    <Typography sx={{ fontWeight: 700 }}>Material</Typography>
                                    <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                        <Typography color='text.secondary'>{selectedCount(materiais) > 0 ? `${selectedCount(materiais)} selecionados` : 'Todos'}</Typography>
                                        <Box component='span' sx={{ transform: openMateriais ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                                    </Box>
                                </Box>
                                <Collapse in={openMateriais} timeout={160}>
                                    <Box sx={{ p: 2 }}>
                                        <CheckboxButtons items={materialItems} checked={(materiais ?? [])} onChange={(items: string[]) => { dispatch(setMateriais(items)); onChangeComplete?.(); }} />
                                    </Box>
                                </Collapse>
                            </>
                        )}

                        {showTamanho && (
                            <>
                                <Box
                                    onClick={() => setOpenTamanhos(s => !s)}
                                    sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                                >
                                    <Typography sx={{ fontWeight: 700 }}>Tamanho</Typography>
                                    <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                        <Typography color='text.secondary'>{selectedCount(tamanhos) > 0 ? `${selectedCount(tamanhos)} selecionados` : 'Todos'}</Typography>
                                        <Box component='span' sx={{ transform: openTamanhos ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                                    </Box>
                                </Box>
                                <Collapse in={openTamanhos} timeout={160}>
                                    <Box sx={{ p: 2 }}>
                                        <CheckboxButtons items={tamanhoItems} checked={(tamanhos ?? [])} onChange={(items: string[]) => { dispatch(setTamanhos(items)); onChangeComplete?.(); }} />
                                    </Box>
                                </Collapse>
                            </>
                        )}

                        

                        {/* Campaigns */}
                        <Box
                            onClick={() => setOpenCampaigns(s => !s)}
                            sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                        >
                            <Typography sx={{ fontWeight: 700 }}>Campanhas</Typography>
                            <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                <Typography color='text.secondary'>{selectedCount(campaignIds) > 0 ? `${selectedCount(campaignIds)} selecionadas` : 'Todas'}</Typography>
                                <Box component='span' sx={{ transform: openCampaigns ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                            </Box>
                        </Box>
                        <Collapse in={openCampaigns} timeout={160}>
                            <Box sx={{ p: 2 }}>
                                <CheckboxButtons items={campaignItems} checked={(campaignIds ?? []).map(String)} onChange={(items: string[]) => { dispatch(setCampaigns(items.map(Number))); onChangeComplete?.(); }} />
                            </Box>
                        </Collapse>

                        {/* Ano de Lançamento (only shown when user selects a Books category) */}
                        {showGenero && (
                            <>
                                <Box
                                    onClick={() => setOpenTypes(s => !s)}
                                    sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', px: 2, py: 1.25, borderRadius: 2, cursor: 'pointer', bgcolor: theme.palette.action.hover }}
                                >
                                    <Typography sx={{ fontWeight: 700 }}>Ano de Lançamento</Typography>
                                    <Box sx={{ display: 'flex', gap: 2, alignItems: 'center' }}>
                                        <Typography color='text.secondary'>{selectedCount(anos) > 0 ? `${selectedCount(anos)} selecionados` : 'Todos'}</Typography>
                                        <Box component='span' sx={{ transform: openTypes ? 'rotate(90deg)' : 'rotate(0deg)', transition: 'transform 160ms' }}>›</Box>
                                    </Box>
                                </Box>
                                <Collapse in={openTypes} timeout={160}>
                                    <Box sx={{ p: 2 }}>
                                        <CheckboxButtons items={(data?.anos ?? []).map(String)} checked={(anos ?? []).map(String)} onChange={(items: string[]) => { dispatch(setAnos(items.map(Number))); onChangeComplete?.(); }} />
                                    </Box>
                                </Collapse>
                            </>
                        )}
                    </Box>
                </Paper>
                    <Box sx={{ mt: 1 }}>
                    <Button onClick={() => { dispatch(resetParams()); onChangeComplete?.(); }} variant='outlined'>Repor filtros</Button>
                </Box>
            </Box>

            {/* Mobile: unchanged stacked papers */}
                <Box sx={{ display: { xs: 'block', md: 'none' } }}>
                <Box display='flex' flexDirection='column' gap={2}>
                    <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                        <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Ordenar</Typography>
                            <RadioButtonGroup selectedValue={orderBy} options={sortOptions} onChange={e => { dispatch(setOrderBy(e.target.value)); onChangeComplete?.(); }} />
                    </Paper>

                                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                                        <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Campaigns</Typography>
                                        <CheckboxButtons items={campaignItems} checked={(campaignIds ?? []).map(String)} onChange={(items: string[]) => { dispatch(setCampaigns(items.map(Number))); onChangeComplete?.(); }} />
                                    </Paper>

                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                        <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Categorias</Typography>
                        <CheckboxButtons items={categoryItems} checked={(categoryIds ?? []).map(String)} onChange={(items: string[]) => { dispatch(setCategories(items.map(Number))); onChangeComplete?.(); }} />
                    </Paper>

                    {showGenero && (
                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                            <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Género</Typography>
                            <CheckboxButtons items={generoItems} checked={(generos ?? [])} onChange={(items: string[]) => { dispatch(setGeneros(items)); onChangeComplete?.(); }} />
                        </Paper>
                    )}

                    {showMarca && (
                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                            <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Marca</Typography>
                            <CheckboxButtons items={marcaItems} checked={(marcas ?? [])} onChange={(items: string[]) => { dispatch(setMarcas(items)); onChangeComplete?.(); }} />
                        </Paper>
                    )}

                    {showTipo && (
                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                            <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Tipo</Typography>
                            <CheckboxButtons items={tipoItems} checked={(tipos ?? [])} onChange={(items: string[]) => { dispatch(setTipos(items)); onChangeComplete?.(); }} />
                        </Paper>
                    )}

                    {showModelo && (
                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                            <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Modelo</Typography>
                            <CheckboxButtons items={modeloItems} checked={(modelos ?? [])} onChange={(items: string[]) => { dispatch(setModelos(items)); onChangeComplete?.(); }} />
                        </Paper>
                    )}

                    {showCapacidade && (
                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                            <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Capacidade</Typography>
                            <CheckboxButtons items={capacidadeItems} checked={(capacidades ?? [])} onChange={(items: string[]) => { dispatch(setCapacidades(items)); onChangeComplete?.(); }} />
                        </Paper>
                    )}

                    {showCor && (
                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                            <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Cor</Typography>
                            <CheckboxButtons items={corItems} checked={(cores ?? [])} onChange={(items: string[]) => { dispatch(setCores(items)); onChangeComplete?.(); }} />
                        </Paper>
                    )}

                    {showMaterial && (
                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                            <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Material</Typography>
                            <CheckboxButtons items={materialItems} checked={(materiais ?? [])} onChange={(items: string[]) => { dispatch(setMateriais(items)); onChangeComplete?.(); }} />
                        </Paper>
                    )}

                    {showTamanho && (
                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                            <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Tamanho</Typography>
                            <CheckboxButtons items={tamanhoItems} checked={(tamanhos ?? [])} onChange={(items: string[]) => { dispatch(setTamanhos(items)); onChangeComplete?.(); }} />
                        </Paper>
                    )}

                    {showGenero && (
                        <Paper sx={{ p: 2, bgcolor: 'background.paper' }}>
                            <Typography variant='subtitle2' sx={{ mb: 1, fontWeight: 700 }}>Ano de Lançamento</Typography>
                            <CheckboxButtons items={(data?.anos ?? []).map(String)} checked={(anos ?? []).map(String)} onChange={(items: string[]) => { dispatch(setAnos(items.map(Number))); onChangeComplete?.(); }} />
                        </Paper>
                    )}

                    <Box>
                        <Button onClick={() => { dispatch(resetParams()); onChangeComplete?.(); }} variant='outlined'>Repor filtros</Button>
                    </Box>
                </Box>
            </Box>
        </Box>
    )
}