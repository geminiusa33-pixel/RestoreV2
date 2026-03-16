import { FieldValues, useForm } from "react-hook-form";
import { createProductSchema, CreateProductSchema } from "../../lib/schemas/createProductSchema";
import { zodResolver } from "@hookform/resolvers/zod";
import { Box, Button, Grid, Paper, Typography, Autocomplete, TextField, Chip, IconButton } from "@mui/material";
import AppTextInput from "../../app/shared/components/AppTextInput";
import AppDropzone from "../../app/shared/components/AppDropzone";
import PageTitle from "../../app/shared/components/PageTitle";
import { Product } from "../../app/models/product";
import { Category } from "../../app/models/category";
import { Campaign } from "../../app/models/campaign";
import { useEffect, useMemo, useRef, useState } from "react";
import { LoadingButton } from "@mui/lab";
import { computeFinalPrice, currencyFormat } from '../../lib/util';
import { handleApiError } from "../../lib/util";
import { useCreateProductMutation, useUpdateProductMutation, useGetCampaignsQuery, useGetAllCategoriesQuery, useCreateCategoryMutation } from "./adminApi";
import AddIcon from '@mui/icons-material/Add';
import DeleteIcon from '@mui/icons-material/Delete';

// --- Tipagem segura para ficheiros com preview ---
type PreviewFile = File & { preview: string };
type SecondaryPreviewFile = File & { preview: string };

type VariantDraft = {
    id?: number;
    key: string;
    color: string;
    quantityInStock: number;
    priceOverride?: number | null;
    descriptionOverride?: string | null;
    file?: File | null;
    preview?: string | null;
    existingPictureUrl?: string | null;
};

type PropertyDraft = {
    key: string;
    categoryId: number | null;
    name: string;
    value: string;
};

type Props = {
    setEditMode: (value: boolean) => void;
    product: Product | null;
    refetch: () => void;
    setSelectedProduct: (value: Product | null) => void;
};

export default function ProductForm({ setEditMode, product, refetch, setSelectedProduct }: Props) {
    const { control, handleSubmit, watch, reset, setError, formState: { isSubmitting }, setValue } =
        useForm<CreateProductSchema>({
            mode: "onTouched",
            resolver: zodResolver(createProductSchema)
        });

    const [removedSecondaryImages, setRemovedSecondaryImages] = useState<string[]>([]);
    const [removedSecondaryUploads, setRemovedSecondaryUploads] = useState<string[]>([]);

    const [variantsDraft, setVariantsDraft] = useState<VariantDraft[]>([]);
    const [variantsError, setVariantsError] = useState<string | null>(null);
    const variantPrevRef = useRef<string[]>([]);

    const [propertiesDraft, setPropertiesDraft] = useState<PropertyDraft[]>([]);

    const watchFile = watch("file");
    const watchSecondaryFiles = watch('secondaryFiles') as unknown as SecondaryPreviewFile[] | undefined;
    const secondaryPrevRef = useRef<SecondaryPreviewFile[]>([]);
    const [createProduct] = useCreateProductMutation();
    const [updateProduct] = useUpdateProductMutation();
    const [createCategory] = useCreateCategoryMutation();
    const { data: campaigns } = useGetCampaignsQuery();
    const { data: categories } = useGetAllCategoriesQuery();

    const [selectedCategoryPath, setSelectedCategoryPath] = useState<Category[]>(() => (product?.categories ?? []) as Category[]);
    const [categoryInputs, setCategoryInputs] = useState<string[]>(['', '', '', '']);
    const [selectedCampaigns, setSelectedCampaigns] = useState<{ id: number; name: string }[]>(product?.campaigns ?? []);

    const selectedCategoryPathRef = useRef<Category[]>(selectedCategoryPath);
    const categoryInputsRef = useRef<string[]>(categoryInputs);
    const creatingCategoryLevelsRef = useRef<Set<number>>(new Set());

    useEffect(() => {
        selectedCategoryPathRef.current = selectedCategoryPath;
    }, [selectedCategoryPath]);

    useEffect(() => {
        categoryInputsRef.current = categoryInputs;
    }, [categoryInputs]);

    const categoriesById = useMemo(() => {
        const map = new Map<number, Category>();
        for (const c of (categories ?? [])) map.set(c.id, c);
        return map;
    }, [categories]);

    const activeCategories = useMemo(() => {
        return (categories ?? []).filter(c => c.isActive !== false);
    }, [categories]);

    const newVariantKey = () => {
        const id = (typeof crypto !== 'undefined' && 'randomUUID' in crypto)
            ? crypto.randomUUID()
            : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
        return `v-${id}`;
    };

    // Product-level stock is independent from variant stock.

    useEffect(() => {
        // On unmount, revoke any variant previews
        return () => {
            for (const url of variantPrevRef.current) {
                try { URL.revokeObjectURL(url); } catch { }
            }
            variantPrevRef.current = [];
        };
    }, []);
    
    const newPropertyKey = () => {
        const id = (typeof crypto !== 'undefined' && 'randomUUID' in crypto)
            ? crypto.randomUUID()
            : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
        return `p-${id}`;
    };

    // 1) Atualizar valores quando "product" muda (evitar reset do file)
    useEffect(() => {
        if (product) {
            // map backend fields to form fields (only the fields the form needs)
            const mapped: Partial<CreateProductSchema> = {
                name: product.name,
                description: product.description ?? '',
                author: product.author ?? undefined,
                secondaryAuthors: product.secondaryAuthors ?? undefined,
                price: product.price,
                subtitle: product.subtitle ?? undefined,
                genero: product.genero ?? undefined,
                anoPublicacao: product.anoPublicacao ?? undefined,
                isbn: product.isbn ?? undefined,
                publisher: product.publisher ?? undefined,
                edition: product.edition != null ? String(product.edition) : undefined,
                precoPromocional: product.promotionalPrice ?? undefined,
                synopsis: product.synopsis ?? undefined,
                index: product.index ?? undefined,
                pageCount: product.pageCount ?? undefined,
                language: product.language ?? undefined,
                format: product.format ?? undefined,
                dimensoes: product.dimensoes ?? undefined,
                weight: product.weight ?? undefined,
                quantityInStock: product.quantityInStock,
                pictureUrl: product.pictureUrl ?? undefined,
                descontoPercentagem: product.discountPercentage ?? undefined,
                cor: product.cor ?? undefined,
                material: product.material ?? undefined,
                tamanho: product.tamanho ?? undefined,
                marca: product.marca ?? undefined,
                tipo: product.tipo ?? undefined,
                modelo: product.modelo ?? undefined,
                capacidade: product.capacidade ?? undefined,
                idadeMinima: product.idadeMinima ?? undefined,
                idadeMaxima: product.idadeMaxima ?? undefined,
            };
            reset(mapped as Partial<CreateProductSchema>);
            setRemovedSecondaryImages([]);
            // initialise selected categories/campaigns in form
            if (product.categories && product.categories.length) {
                // Try to reconstruct a single best path from the product's assigned categories.
                const assigned = (product.categories as Category[]).filter(Boolean);
                const assignedIds = new Set(assigned.map(c => c.id));
                const parentIdsInSet = new Set<number>();
                for (const c of assigned) {
                    const pid = (c.parentCategoryId ?? categoriesById.get(c.id)?.parentCategoryId) ?? null;
                    if (pid && assignedIds.has(pid)) parentIdsInSet.add(pid);
                }
                const leaves = assigned.filter(c => !parentIdsInSet.has(c.id));
                const leaf = leaves[0] ?? assigned[0];

                const path: Category[] = [];
                let current: Category | undefined = categoriesById.get(leaf.id) ?? leaf;
                for (let i = 0; i < 10 && current; i++) {
                    path.unshift(current);
                    if (!current.parentCategoryId) break;
                    current = categoriesById.get(current.parentCategoryId);
                }
                const finalPath = path.slice(0, 4);
                setSelectedCategoryPath(finalPath);
                selectedCategoryPathRef.current = finalPath;
                setValue('categoryIds', finalPath.map(c => c.id));
                setCategoryInputs(['', '', '', '']);
            }
            if (product.campaigns && product.campaigns.length) {
                setSelectedCampaigns(product.campaigns as { id: number; name: string }[]);
                setValue('campaignIds', product.campaigns.map(c => c.id));
            }

            const drafts: VariantDraft[] = (product.variants ?? []).map((v) => ({
                id: v.id,
                key: `v-${v.id}`,
                color: v.color,
                quantityInStock: v.quantityInStock,
                priceOverride: (v.priceOverride ?? null) as unknown as number | null,
                descriptionOverride: (v.descriptionOverride ?? null) as string | null,
                existingPictureUrl: (v.pictureUrl ?? null) as string | null,
                file: null,
                preview: null
            }));
            setVariantsDraft(drafts);
            setVariantsError(null);

            // Custom properties
            try {
                const raw = product.customPropertiesJson;
                const parsed = raw ? JSON.parse(raw) : [];
                const next: PropertyDraft[] = Array.isArray(parsed) ? parsed.map((p: any) => ({
                    key: newPropertyKey(),
                    categoryId: (typeof p?.categoryId === 'number' ? p.categoryId : null),
                    name: String(p?.name ?? '').trim(),
                    value: String(p?.value ?? '').trim(),
                })) : [];
                setPropertiesDraft(next);
            } catch {
                setPropertiesDraft([]);
            }
        }
        else {
            setVariantsDraft([]);
            setVariantsError(null);
            setPropertiesDraft([]);
        }
    }, [product, reset, setValue, categoriesById]);

    const setPathAtLevel = (level: number, value: Category | null) => {
        setSelectedCategoryPath(prev => {
            const next = prev.slice(0, level);
            if (value) next[level] = value;
            const final = next.slice(0, 4);
            selectedCategoryPathRef.current = final;
            setValue('categoryIds', final.map(c => c.id));
            return final;
        });

        // Clear deeper level inputs when changing the path
        setCategoryInputs(prev => {
            const next = prev.map((v, i) => (i > level ? '' : v));
            categoryInputsRef.current = next;
            return next;
        });
    };

    const optionsForParent = (parentId: number | null) => {
        const pid = parentId ?? null;
        return activeCategories.filter(c => (c.parentCategoryId ?? null) === pid);
    };

    const breadcrumbForLevel = (level: number) => {
        if (level <= 0) return '';
        const parts = selectedCategoryPathRef.current.slice(0, level).map(c => (c.name ?? '').trim()).filter(Boolean);
        return parts.join(' → ');
    };

    const breadcrumbForCategoryId = (categoryId: number | null) => {
        if (!categoryId) return '';
        const idsInPath = new Set(selectedCategoryPathRef.current.map(c => c.id));
        if (!idsInPath.has(categoryId)) return '';
        const parts: string[] = [];
        for (const c of selectedCategoryPathRef.current) {
            parts.push((c.name ?? '').trim());
            if (c.id === categoryId) break;
        }
        return parts.filter(Boolean).join(' → ');
    };

    const ensureCategoryAtLevel = async (level: number, raw: string) => {
        const name = (raw ?? '').trim();
        if (!name) return;

        const parentId = level === 0 ? null : (selectedCategoryPathRef.current[level - 1]?.id ?? null);
        if (level > 0 && !parentId) return;

        const options = optionsForParent(parentId);
        const existing = options.find(o => (o.name ?? '').trim().toLowerCase() === name.toLowerCase());
        if (existing) {
            setPathAtLevel(level, existing);
            setCategoryInputs(prev => {
                const next = prev.map((v, i) => (i === level ? '' : v));
                categoryInputsRef.current = next;
                return next;
            });
            return;
        }

        const created = await createCategory({ name, parentCategoryId: parentId }).unwrap();
        setPathAtLevel(level, created);
        setCategoryInputs(prev => {
            const next = prev.map((v, i) => (i === level ? '' : v));
            categoryInputsRef.current = next;
            return next;
        });
    };

    const commitTypedCategoryAtLevel = async (level: number) => {
        const typed = (categoryInputsRef.current[level] ?? '').trim();
        if (!typed) return;

        if (selectedCategoryPathRef.current[level]) return;

        if (creatingCategoryLevelsRef.current.has(level)) return;
        creatingCategoryLevelsRef.current.add(level);

        try {
            await ensureCategoryAtLevel(level, typed);
        } finally {
            creatingCategoryLevelsRef.current.delete(level);
        }
    };

    // 2) Revogar preview anterior quando watchFile muda
    useEffect(() => {
        const file = watchFile as PreviewFile | undefined;

        return () => {
            if (file?.preview) {
                URL.revokeObjectURL(file.preview);
            }
        };
    }, [watchFile]);

    const secondaryFileKey = (f: File) => `${f.name}_${f.size}_${f.lastModified}`;

    // Revoke object URLs for secondary files when removed/replaced
    useEffect(() => {
        const prev = secondaryPrevRef.current;
        const current = watchSecondaryFiles ?? [];
        const currentKeys = new Set(current.map(secondaryFileKey));

        prev.forEach((f) => {
            if (f.preview && !currentKeys.has(secondaryFileKey(f))) {
                URL.revokeObjectURL(f.preview);
            }
        });

        secondaryPrevRef.current = current;
        setRemovedSecondaryUploads((old) => old.filter((k) => currentKeys.has(k)));
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [watchSecondaryFiles]);

    // On unmount, revoke any remaining previews
    useEffect(() => {
        return () => {
            secondaryPrevRef.current.forEach((f) => {
                if (f.preview) URL.revokeObjectURL(f.preview);
            });
            secondaryPrevRef.current = [];
        };
    }, []);

    const secondaryCounts = useMemo(() => {
        const all = watchSecondaryFiles ?? [];
        const removed = new Set(removedSecondaryUploads);
        const active = all.filter((f) => !removed.has(secondaryFileKey(f)));
        return { total: all.length, active: active.length };
    }, [watchSecondaryFiles, removedSecondaryUploads]);

    // compute final price for preview: use discount percentage only
    const watchedPrice = watch('price') as number | undefined;
    const watchedDiscount = watch('descontoPercentagem') as number | undefined;
    const finalPrice = computeFinalPrice(watchedPrice ?? 0, watchedDiscount);

    const createFormData = (items: FieldValues) => {
        const formData = new FormData();
        for (const key in items) {
            const value = items[key];
            if (value !== undefined && value !== null) {
                // These are handled explicitly to avoid duplicates and to support filtering removed uploads
                if (key === 'file' || key === 'secondaryFiles') continue;
                // handle arrays (tags) and files specially
                if (Array.isArray(value)) {
                    value.forEach(v => formData.append(key, v));
                } else {
                    formData.append(key, value);
                }
            }
        }
        return formData;
    };

    const onSubmit = async (data: CreateProductSchema) => {
        try {
            setVariantsError(null);

            // For the cascading selector, we submit the whole selected path (ancestors + leaf)
            // so existing filtering by parent category continues to work.
            const finalCategoryIds: number[] = (selectedCategoryPathRef.current ?? []).map(c => c.id);

            // ensure campaignIds are included (selectedCampaigns holds campaign objects)
            const existingCampaignIds = (selectedCampaigns ?? []).map(c => (c as { id: number }).id);
            const finalCampaignIds: number[] = [...existingCampaignIds];

            const usingVariants = (variantsDraft?.length ?? 0) > 0 || ((product?.variants?.length ?? 0) > 0);
            const variantPayload = (variantsDraft ?? []).map(v => {
                const color = (v.color ?? '').trim();
                return {
                    id: v.id,
                    key: v.key,
                    color,
                    quantityInStock: Math.max(0, Number(v.quantityInStock) || 0),
                    priceOverride: v.priceOverride ?? null,
                    descriptionOverride: (v.descriptionOverride ?? '').trim() || null
                };
            });

            if ((variantsDraft?.length ?? 0) > 0) {
                const baseColor = (data.cor ?? '').trim();
                if (!baseColor) {
                    setError('cor', { type: 'manual', message: 'Indica a cor principal do produto.' });
                    return;
                }

                if (variantPayload.some(v => !v.color)) {
                    setVariantsError('Cada variação precisa de uma cor.');
                    return;
                }

                const normalizedBase = baseColor.toLowerCase();
                const normalizedVariants = variantPayload.map(v => v.color.toLowerCase());
                const unique = new Set(normalizedVariants);
                if (unique.size !== normalizedVariants.length) {
                    setVariantsError('As cores das variações devem ser únicas.');
                    return;
                }

                if (normalizedVariants.includes(normalizedBase)) {
                    setVariantsError('A cor principal não deve ser repetida nas variações.');
                    return;
                }
            }

            // build submission payload ensuring categoryIds/campaignIds contain the created/existing ids
            const submissionData = {
                ...data,
                categoryIds: finalCategoryIds,
                campaignIds: finalCampaignIds,
                // quantityInStock stays independent even if variants exist
            } as unknown as FieldValues;

            // custom properties JSON
            const cleanProps = (propertiesDraft ?? [])
                .map(p => ({
                    categoryId: p.categoryId ?? null,
                    name: (p.name ?? '').trim(),
                    value: (p.value ?? '').trim(),
                }))
                .filter(p => p.name.length > 0);
            // Always include (even empty) so updates can clear.
            submissionData.propertiesJson = JSON.stringify(cleanProps);
            const formData = createFormData(submissionData);

            // ensure explicit sentinel keys are present when user cleared selections so the server
            // can detect an explicit update without model binding attempting to parse an empty string
            if (finalCampaignIds.length === 0) formData.append('campaignIds_present', '1');
            if (finalCategoryIds.length === 0) formData.append('categoryIds_present', '1');

            if (watchFile) {
                formData.append("file", watchFile);
            }

            // handle secondary files (multiple)
            const secondaryFiles = data.secondaryFiles as SecondaryPreviewFile[] | undefined;
            if (secondaryFiles && secondaryFiles.length) {
                const removed = new Set(removedSecondaryUploads);
                secondaryFiles
                    .filter((f) => !removed.has(secondaryFileKey(f)))
                    .forEach((f) => formData.append('secondaryFiles', f));
            }

            // include any removed secondary images when updating
            if (removedSecondaryImages && removedSecondaryImages.length) {
                removedSecondaryImages.forEach(u => formData.append('removedSecondaryImages', u));
            }

            // map discount percentage to backend expected key
            if (data.descontoPercentagem !== undefined) {
                formData.append('discountPercentage', String(data.descontoPercentagem));
            }

            if (usingVariants) {
                formData.append('variantsJson', JSON.stringify(variantPayload));
                for (const v of (variantsDraft ?? [])) {
                    if (v.file) {
                        formData.append('variantFiles', v.file);
                        formData.append('variantFileKeys', v.key);
                    }
                }
            }

            if (product) {
                await updateProduct({ id: product.id, data: formData }).unwrap();
            } else {
                await createProduct(formData).unwrap();
            }

            setEditMode(false);
            setSelectedProduct(null);
            refetch();
            } catch (error) {
            console.log(error);
            handleApiError<CreateProductSchema>(error, setError, [
                "description",
                "file",
                "name",
                "pictureUrl",
                "price",
                "quantityInStock",
                "genero",
                "anoPublicacao"
            ]);
        }
    };

    return (
        <Box component={Paper} sx={{ p: 4, maxWidth: 900, width: '100%', mx: "auto" }}>
            <PageTitle title="Detalhes do Produto" variant="h4" />
            <Typography variant="h6" sx={{ mb: 4 }}>
                {product ? 'Editar Produto' : 'Criar Novo Produto'}
            </Typography>

            <form onSubmit={handleSubmit(onSubmit)}>
                <Grid container spacing={3}>
                    <Grid item xs={12}>
                        <AppTextInput control={control} name="name" label="Nome do Produto" />
                    </Grid>

                    {/* Categories (up to 4 levels) */}
                    <Grid item xs={12}>
                        <Box display='flex' flexDirection='column' gap={2}>
                            {[0, 1, 2, 3].map((level) => {
                                const parentId = level === 0 ? null : (selectedCategoryPathRef.current[level - 1]?.id ?? null);
                                const visible = level === 0 || !!selectedCategoryPathRef.current[level - 1];
                                if (!visible) return null;

                                const opts = optionsForParent(parentId);
                                const value = selectedCategoryPath[level] ?? null;
                                const label = level === 0
                                    ? 'Categoria'
                                    : level === 1
                                        ? 'Subcategoria'
                                        : `Subcategoria (nível ${level + 1})`;

                                const breadcrumb = breadcrumbForLevel(level);

                                return (
                                    <Box key={level}>
                                        {breadcrumb && (
                                            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.5 }}>
                                                {breadcrumb}
                                            </Typography>
                                        )}

                                        <Box sx={{ display: 'flex', gap: 1, alignItems: 'flex-start' }}>
                                            <Box sx={{ flex: 1 }}>
                                                <Autocomplete<Category, false, false, true>
                                                    freeSolo
                                                    options={opts}
                                                    value={value}
                                                    inputValue={categoryInputs[level]}
                                                    getOptionLabel={(o) => typeof o === 'string' ? o : (o.name ?? '')}
                                                    onInputChange={(_e, v) => {
                                                        setCategoryInputs(prev => {
                                                            const next = prev.map((p, i) => (i === level ? v : p));
                                                            categoryInputsRef.current = next;
                                                            return next;
                                                        });
                                                    }}
                                                    onChange={async (_event, v) => {
                                                        try {
                                                            if (typeof v === 'string') {
                                                                await commitTypedCategoryAtLevel(level);
                                                                return;
                                                            }

                                                            if (!v) {
                                                                setPathAtLevel(level, null);
                                                                return;
                                                            }

                                                            setPathAtLevel(level, v);
                                                            setCategoryInputs(prev => {
                                                                const next = prev.map((p, i) => (i === level ? '' : p));
                                                                categoryInputsRef.current = next;
                                                                return next;
                                                            });
                                                        } catch (err) {
                                                            console.error('Failed to set category', err);
                                                            setError('categoryIds', { type: 'manual', message: 'Falha ao criar/definir categoria. Verifica permissões.' });
                                                        }
                                                    }}
                                                    renderInput={(params) => (
                                                        <TextField
                                                            {...params}
                                                            label={label}
                                                            placeholder='Seleciona ou escreve e carrega Enter'
                                                            onBlur={async () => {
                                                                try {
                                                                    await commitTypedCategoryAtLevel(level);
                                                                } catch (err) {
                                                                    console.error('Failed to create category', err);
                                                                    setError('categoryIds', { type: 'manual', message: 'Falha ao criar categoria. Verifica permissões.' });
                                                                }
                                                            }}
                                                            onKeyDown={async (e) => {
                                                                if (e.key !== 'Enter') return;
                                                                e.preventDefault();
                                                                try {
                                                                    await commitTypedCategoryAtLevel(level);
                                                                } catch (err) {
                                                                    console.error('Failed to create category', err);
                                                                    setError('categoryIds', { type: 'manual', message: 'Falha ao criar categoria. Verifica permissões.' });
                                                                }
                                                            }}
                                                        />
                                                    )}
                                                />
                                            </Box>

                                            <IconButton
                                                aria-label="add-property"
                                                sx={{ mt: 0.5 }}
                                                size="small"
                                                disabled={!selectedCategoryPath[level]}
                                                onClick={() => {
                                                    const cat = selectedCategoryPath[level];
                                                    if (!cat) return;
                                                    setPropertiesDraft(prev => ([
                                                        ...prev,
                                                        { key: newPropertyKey(), categoryId: cat.id, name: '', value: '' }
                                                    ]));
                                                }}
                                            >
                                                <AddIcon fontSize="small" />
                                            </IconButton>
                                        </Box>
                                    </Box>
                                );
                            })}
                        </Box>
                    </Grid>

                    {/* Custom properties */}
                    <Grid item xs={12}>
                        <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 2, p: 2 }}>
                            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 2 }}>
                                <Typography variant="subtitle1" fontWeight={700}>
                                    Propriedades (opcional)
                                </Typography>
                                <Button
                                    variant="outlined"
                                    size="small"
                                    onClick={() => {
                                        const leaf = selectedCategoryPathRef.current[selectedCategoryPathRef.current.length - 1];
                                        setPropertiesDraft(prev => ([
                                            ...prev,
                                            { key: newPropertyKey(), categoryId: leaf?.id ?? null, name: '', value: '' }
                                        ]));
                                    }}
                                >
                                    Adicionar
                                </Button>
                            </Box>

                            {(propertiesDraft ?? []).length === 0 ? (
                                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                                    Usa o botão "+" nas categorias ou "Adicionar" para incluir propriedades como Sabor, Tamanho, Compatibilidade, etc.
                                </Typography>
                            ) : (
                                <Box sx={{ mt: 2, display: 'flex', flexDirection: 'column', gap: 2 }}>
                                    {propertiesDraft.map((p) => {
                                        const crumb = breadcrumbForCategoryId(p.categoryId);
                                        return (
                                            <Box key={p.key} sx={{ borderTop: 1, borderColor: 'divider', pt: 2 }}>
                                                {crumb && (
                                                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
                                                        {crumb}
                                                    </Typography>
                                                )}
                                                <Grid container spacing={2} alignItems="center">
                                                    <Grid item xs={12} md={4}>
                                                        <TextField
                                                            fullWidth
                                                            label="Propriedade"
                                                            value={p.name}
                                                            onChange={(e) => {
                                                                const value = e.target.value;
                                                                setPropertiesDraft(prev => prev.map(x => x.key === p.key ? { ...x, name: value } : x));
                                                            }}
                                                        />
                                                    </Grid>
                                                    <Grid item xs={12} md={7}>
                                                        <TextField
                                                            fullWidth
                                                            label="Valor"
                                                            value={p.value}
                                                            onChange={(e) => {
                                                                const value = e.target.value;
                                                                setPropertiesDraft(prev => prev.map(x => x.key === p.key ? { ...x, value } : x));
                                                            }}
                                                        />
                                                    </Grid>
                                                    <Grid item xs={12} md={1} sx={{ display: 'flex', justifyContent: 'flex-end' }}>
                                                        <IconButton
                                                            aria-label="remove-property"
                                                            color="error"
                                                            onClick={() => setPropertiesDraft(prev => prev.filter(x => x.key !== p.key))}
                                                        >
                                                            <DeleteIcon />
                                                        </IconButton>
                                                    </Grid>
                                                </Grid>
                                            </Box>
                                        );
                                    })}
                                </Box>
                            )}
                        </Box>
                    </Grid>

                    {/* Campaigns multi-select */}
                    <Grid item xs={12}>
                        <Autocomplete
                            multiple
                            options={(campaigns ?? []) as Campaign[]}
                            getOptionLabel={(option: Campaign) => option.name}
                            value={selectedCampaigns}
                            onChange={(_, value: Campaign[] | null) => {
                                const vals = (value ?? []) as Campaign[];
                                setSelectedCampaigns(vals);
                                setValue('campaignIds', vals.map(v => v.id));
                            }}
                            renderTags={(value: Campaign[], getTagProps) =>
                                value.map((option, index) => (
                                    <Chip label={option.name} {...getTagProps({ index })} key={option.id} />
                                ))
                            }
                            renderInput={(params) => (
                                <TextField {...params} label="Campanhas" placeholder="Select campaigns" />
                            )}
                        />
                    </Grid>


                    <Grid item xs={12} md={6}>
                        <AppTextInput
                            type="number"
                            control={control}
                            name="price"
                            label="Preço"
                            helperText="Em cêntimos (ex: 1299 = €12,99)"
                        />
                    </Grid>
                    <Grid item xs={12} md={6}>
                        <AppTextInput
                            type="number"
                            control={control}
                            name="descontoPercentagem"
                            label="Desconto (%)"
                        />
                    </Grid>

                    <Grid item xs={12} md={6}>
                        <Typography sx={{ mt: 2 }}>
                            {watchedDiscount && watchedDiscount > 0 ? (
                                <>
                                    <span style={{ textDecoration: 'line-through', marginRight: 8 }}>{currencyFormat(watchedPrice ?? 0)}</span>
                                    <span style={{ color: 'crimson', fontWeight: 700 }}>{currencyFormat(finalPrice ?? 0)}</span>
                                </>
                            ) : (
                                <span>{currencyFormat(finalPrice ?? 0)}</span>
                            )}
                        </Typography>
                    </Grid>
                    <Grid item xs={12} md={6}>
                        <AppTextInput
                            type="number"
                            control={control}
                            name="quantityInStock"
                            label="Quantidade em Stock"
                            helperText={(variantsDraft?.length ?? 0) > 0 ? 'Independente do stock por cor' : undefined}
                        />
                    </Grid>

                    <Grid item xs={12}>
                        <AppTextInput
                            control={control}
                            multiline
                            rows={4}
                            name="description"
                            label="Descrição"
                        />
                    </Grid>                           

                    <Grid item xs={12} sx={{ display: 'flex', flexDirection: { xs: 'column', sm: 'row' }, justifyContent: 'space-between', alignItems: 'center', gap: 2 }}>
                        <Box sx={{ flex: 1, width: '100%' }}>
                            <Typography variant="subtitle2" sx={{ mb: 1 }}>
                                Imagem principal
                            </Typography>
                            <AppDropzone name="file" control={control} />
                            <Box sx={{ mt: 2 }}>
                                <Typography variant="subtitle2" sx={{ mb: 1 }}>
                                    Imagens secundárias (opcional)
                                </Typography>
                                <Button component="label" variant="outlined" size="small">
                                    Selecionar imagens
                                    <input
                                        type="file"
                                        hidden
                                        multiple
                                        accept="image/*"
                                        onChange={(e) => {
                                            const files = e.target.files;
                                            if (files && files.length) {
                                                const existing = (watchSecondaryFiles ?? []) as SecondaryPreviewFile[];
                                                const existingByKey = new Map(existing.map(f => [secondaryFileKey(f), f] as const));
                                                const incoming = Array.from(files).map((f) => {
                                                    const key = secondaryFileKey(f);
                                                    if (existingByKey.has(key)) return existingByKey.get(key)!;
                                                    return Object.assign(f, { preview: URL.createObjectURL(f) }) as SecondaryPreviewFile;
                                                });
                                                const merged = [...existing, ...incoming].reduce<SecondaryPreviewFile[]>((acc, f) => {
                                                    const key = secondaryFileKey(f);
                                                    if (!acc.some(x => secondaryFileKey(x) === key)) acc.push(f);
                                                    return acc;
                                                }, []);

                                                setValue('secondaryFiles', merged as unknown as CreateProductSchema['secondaryFiles'], {
                                                    shouldDirty: true,
                                                    shouldTouch: true,
                                                });
                                                // allow selecting the same file again later
                                                (e.target as HTMLInputElement).value = '';
                                            }
                                        }}
                                    />
                                </Button>

                                {secondaryCounts.total > 0 && (
                                    <Typography variant="body2" sx={{ mt: 1, color: 'text.secondary' }}>
                                        Selecionadas: {secondaryCounts.total} (ativas: {secondaryCounts.active})
                                    </Typography>
                                )}

                                {watchSecondaryFiles && watchSecondaryFiles.length > 0 && (
                                    <Box sx={{ mt: 1, display: 'flex', gap: 1, flexWrap: 'wrap' }}>
                                        {watchSecondaryFiles.map((f) => {
                                            const key = secondaryFileKey(f);
                                            const removed = removedSecondaryUploads.includes(key);
                                            return (
                                                <Box key={key} sx={{ position: 'relative' }}>
                                                    <img
                                                        src={f.preview}
                                                        alt={f.name}
                                                        style={{
                                                            width: 120,
                                                            height: 80,
                                                            objectFit: 'cover',
                                                            borderRadius: 4,
                                                            opacity: removed ? 0.4 : 1,
                                                        }}
                                                    />
                                                    <Button
                                                        size="small"
                                                        color={removed ? 'inherit' : 'error'}
                                                        onClick={() => {
                                                            setRemovedSecondaryUploads((prev) => {
                                                                if (prev.includes(key)) return prev.filter((x) => x !== key);
                                                                return [...prev, key];
                                                            });
                                                        }}
                                                        sx={{ position: 'absolute', top: 4, right: 4, minWidth: 0, px: 1 }}
                                                    >
                                                        {removed ? 'Undo' : 'Remove'}
                                                    </Button>
                                                </Box>
                                            );
                                        })}
                                    </Box>
                                )}
                            </Box>
                        </Box>

                        <Box sx={{ mt: { xs: 1, sm: 0 } }}>
                            {watchFile && (watchFile as PreviewFile).preview ? (
                                <img
                                    src={(watchFile as PreviewFile).preview}
                                    alt="preview"
                                    style={{ maxHeight: 200, maxWidth: '100%', width: 'auto', display: 'block' }}
                                />
                            ) : product?.pictureUrl ? (
                                <img
                                    src={product.pictureUrl}
                                    alt="product"
                                    style={{ maxHeight: 200, maxWidth: '100%', width: 'auto', display: 'block' }}
                                />
                            ) : null}
                        </Box>
                    </Grid>

                    {/* Secondary images thumbnails and removal UI */}
                    {product?.secondaryImages && product.secondaryImages.length > 0 && (
                        <Grid item xs={12}>
                            <Typography variant="subtitle1" sx={{ mb: 1 }}>Existing secondary images</Typography>
                            <Box display='flex' gap={1} flexWrap='wrap'>
                                {product.secondaryImages.map((url, idx) => {
                                    const identifier = product.secondaryImagePublicIds?.[idx] ?? url;
                                    const removed = removedSecondaryImages.includes(identifier);
                                    return (
                                        <Box key={identifier} sx={{ position: 'relative' }}>
                                            <img src={url} alt="sec" style={{ width: 120, height: 80, objectFit: 'cover', borderRadius: 4, opacity: removed ? 0.4 : 1 }} />
                                            <Button size="small" color={removed ? 'inherit' : 'error'} onClick={() => {
                                                if (removed) setRemovedSecondaryImages(prev => prev.filter(x => x !== identifier));
                                                else setRemovedSecondaryImages(prev => [...prev, identifier]);
                                            }} sx={{ position: 'absolute', top: 4, right: 4 }}>
                                                {removed ? 'Undo' : 'Remove'}
                                            </Button>
                                        </Box>
                                    )
                                })}
                            </Box>
                        </Grid>
                    )}

                    <Grid item xs={12}>
                        <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 2, p: 2 }}>
                            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 2 }}>
                                <Typography variant="subtitle1" fontWeight={700}>
                                    Variações por cor (opcional)
                                </Typography>
                                <Button
                                    variant="outlined"
                                    size="small"
                                    onClick={() => {
                                        setVariantsError(null);
                                        setVariantsDraft((prev) => [
                                            ...prev,
                                            {
                                                key: newVariantKey(),
                                                color: '',
                                                quantityInStock: 0,
                                                priceOverride: null,
                                                descriptionOverride: null,
                                                file: null,
                                                preview: null,
                                                existingPictureUrl: null
                                            }
                                        ]);
                                    }}
                                >
                                    Adicionar cor
                                </Button>
                            </Box>

                            {variantsError && (
                                <Typography variant="body2" color="error" sx={{ mt: 1 }}>
                                    {variantsError}
                                </Typography>
                            )}

                            {(variantsDraft ?? []).length === 0 ? (
                                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                                    Sem variações. O produto usa apenas o stock e a imagem principais.
                                </Typography>
                            ) : (
                                <Box sx={{ mt: 2, display: 'flex', flexDirection: 'column', gap: 2 }}>
                                    <Grid container spacing={2}>
                                        <Grid item xs={12} md={4}>
                                            <AppTextInput
                                                control={control}
                                                name="cor"
                                                label="Cor principal"
                                                helperText="Esta é a cor do produto principal (sem variação)."
                                            />
                                        </Grid>
                                    </Grid>

                                    {variantsDraft.map((v, idx) => {
                                        const previewUrl = v.preview || v.existingPictureUrl || undefined;

                                        return (
                                            <Box key={v.key} sx={{ borderTop: idx === 0 ? 0 : 1, borderColor: 'divider', pt: idx === 0 ? 0 : 2 }}>
                                                <Grid container spacing={2}>
                                                    <Grid item xs={12} md={3}>
                                                        <TextField
                                                            fullWidth
                                                            label="Cor"
                                                            value={v.color}
                                                            onChange={(e) => {
                                                                const value = e.target.value;
                                                                setVariantsDraft((prev) => prev.map(x => x.key === v.key ? { ...x, color: value } : x));
                                                            }}
                                                        />
                                                    </Grid>
                                                    <Grid item xs={12} md={2}>
                                                        <TextField
                                                            fullWidth
                                                            type="number"
                                                            label="Stock"
                                                            value={v.quantityInStock}
                                                            onChange={(e) => {
                                                                const value = Math.max(0, Number(e.target.value) || 0);
                                                                setVariantsDraft((prev) => prev.map(x => x.key === v.key ? { ...x, quantityInStock: value } : x));
                                                            }}
                                                        />
                                                    </Grid>
                                                    <Grid item xs={12} md={3}>
                                                        <TextField
                                                            fullWidth
                                                            type="number"
                                                            label="Preço (opcional)"
                                                            value={v.priceOverride ?? ''}
                                                            onChange={(e) => {
                                                                const raw = e.target.value;
                                                                const value = raw === '' ? null : Math.max(0, Number(raw) || 0);
                                                                setVariantsDraft((prev) => prev.map(x => x.key === v.key ? { ...x, priceOverride: value } : x));
                                                            }}
                                                            helperText="Em cêntimos (ex: 1299 = €12,99)"
                                                        />
                                                    </Grid>
                                                    <Grid item xs={12} md={4}>
                                                        <TextField
                                                            fullWidth
                                                            label="Descrição (opcional)"
                                                            value={v.descriptionOverride ?? ''}
                                                            onChange={(e) => {
                                                                const value = e.target.value;
                                                                setVariantsDraft((prev) => prev.map(x => x.key === v.key ? { ...x, descriptionOverride: value } : x));
                                                            }}
                                                        />
                                                    </Grid>

                                                    <Grid item xs={12} md={8}>
                                                        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap' }}>
                                                            <Button component="label" variant="outlined" size="small">
                                                                Foto (opcional)
                                                                <input
                                                                    type="file"
                                                                    hidden
                                                                    accept="image/*"
                                                                    onChange={(e) => {
                                                                        const file = e.target.files?.[0];
                                                                        if (!file) return;

                                                                        const newPreview = URL.createObjectURL(file);
                                                                        variantPrevRef.current.push(newPreview);

                                                                        setVariantsDraft((prev) => prev.map(x => {
                                                                            if (x.key !== v.key) return x;
                                                                            if (x.preview) {
                                                                                try { URL.revokeObjectURL(x.preview); } catch { }
                                                                            }
                                                                            return { ...x, file, preview: newPreview };
                                                                        }));

                                                                        (e.target as HTMLInputElement).value = '';
                                                                    }}
                                                                />
                                                            </Button>

                                                            {previewUrl && (
                                                                <img
                                                                    src={previewUrl}
                                                                    alt={v.color || 'variant'}
                                                                    style={{ width: 64, height: 64, objectFit: 'cover', borderRadius: 8 }}
                                                                />
                                                            )}
                                                        </Box>
                                                    </Grid>

                                                    <Grid item xs={12} md={4} sx={{ display: 'flex', justifyContent: 'flex-end', alignItems: 'center' }}>
                                                        <Button
                                                            color="error"
                                                            variant="outlined"
                                                            size="small"
                                                            onClick={() => {
                                                                setVariantsDraft((prev) => {
                                                                    const found = prev.find(x => x.key === v.key);
                                                                    if (found?.preview) {
                                                                        try { URL.revokeObjectURL(found.preview); } catch { }
                                                                    }
                                                                    return prev.filter(x => x.key !== v.key);
                                                                });
                                                            }}
                                                        >
                                                            Remover
                                                        </Button>
                                                    </Grid>
                                                </Grid>
                                            </Box>
                                        );
                                    })}
                                </Box>
                            )}
                        </Box>
                    </Grid>
                </Grid>

                <Box sx={{ mt: 3, display: 'flex', flexDirection: { xs: 'column', sm: 'row' }, justifyContent: 'space-between', gap: 2 }}>
                    <Button onClick={() => setEditMode(false)} variant="contained" color="inherit" sx={{ width: { xs: '100%', sm: 'auto' } }}>
                        Cancelar
                    </Button>

                    <LoadingButton
                        loading={isSubmitting}
                        variant="contained"
                        color="success"
                        type="submit"
                        sx={{ width: { xs: '100%', sm: 'auto' } }}
                    >
                        Enviar
                    </LoadingButton>
                </Box>
            </form>
        </Box>
    );
}
