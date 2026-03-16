export type Category = {
    id: number;
    name: string;
    slug?: string | null;
    isActive?: boolean;
    description?: string | null;
    parentCategoryId?: number | null;
}
