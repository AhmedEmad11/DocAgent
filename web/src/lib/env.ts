import { z } from "zod";

// Server-side environment schema (UI-SEC-9). Never import this from a client component:
// it reads secrets. Validation is lazy so `next build` does not need them yet (they are used from 9.1).
const serverEnvSchema = z.object({
  DOCAGENT_API_URL: z.url().refine((u) => !u.endsWith("/"), "must not end with a slash"),
  SESSION_SECRET: z.string().min(32, "must be at least 32 characters"),
  SESSION_SECRET_PREVIOUS: z.string().min(32).optional(),
  NEXT_PUBLIC_SITE_URL: z.url(),
  BFF_SHARED_SECRET: z.string().min(16, "must be at least 16 characters"),
});

export type ServerEnv = z.infer<typeof serverEnvSchema>;

export function parseServerEnv(source: Record<string, string | undefined>): ServerEnv {
  const result = serverEnvSchema.safeParse(source);
  if (!result.success) {
    // Names and rules only; never echo values.
    const issues = result.error.issues.map((i) => `${i.path.join(".")}: ${i.message}`);
    throw new Error(`Invalid server environment: ${issues.join("; ")}`);
  }
  return result.data;
}

let cached: ServerEnv | undefined;
export function getServerEnv(): ServerEnv {
  cached ??= parseServerEnv(process.env);
  return cached;
}
