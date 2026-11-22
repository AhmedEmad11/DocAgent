import type { FieldErrors, FieldValues, Resolver } from "react-hook-form";
import type { z } from "zod";

/**
 * Small zod 4 resolver for React Hook Form (one fewer dependency than @hookform/resolvers).
 * All failing messages of a field are kept in `types`, so a password that breaks several rules lists each of them.
 * Issue messages are i18n keys.
 */
export function zodResolver<T extends FieldValues>(schema: z.ZodType<T, FieldValues>): Resolver<T> {
  return async (values) => {
    const result = await schema.safeParseAsync(values);
    if (result.success) return { values: result.data, errors: {} };

    const errors: Record<string, { type: string; message: string; types: Record<string, string> }> =
      {};
    for (const issue of result.error.issues) {
      const name = issue.path.map(String).join(".") || "root";
      const entry = (errors[name] ??= { type: "validation", message: issue.message, types: {} });
      entry.types[issue.message] = issue.message;
    }
    return { values: {}, errors: errors as unknown as FieldErrors<T> };
  };
}
