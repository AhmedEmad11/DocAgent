/** For message keys chosen at run time (error keys from server actions and the error map): the catalogs are checked by the parity script and tests. */
export type DynamicT = (key: string, values?: Record<string, string | number>) => string;
export const dynamicT = (t: unknown): DynamicT => t as DynamicT;
