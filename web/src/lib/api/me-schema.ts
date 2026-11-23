import { z } from "zod";

export const meSchema = z.object({
  id: z.string(),
  email: z.string().nullable(),
  emailConfirmed: z.boolean(),
  externalLogins: z.array(z.string()),
  hasLlmKey: z.boolean(),
  demo: z
    .object({
      limit: z.number(),
      used: z.number(),
      remaining: z.number(),
      resetsAt: z.string(),
      privacyNotice: z.string(),
    })
    .nullish(),
  chunks: z.object({ used: z.number(), limit: z.number() }),
});
export type Me = z.infer<typeof meSchema>;
