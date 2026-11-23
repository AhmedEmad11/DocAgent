import { meSchema, type Me } from "./me-schema";

/** Validates a `GET /me` body that came back through the proxy (browser side). */
export const meFromBody = (body: unknown): Me => meSchema.parse(body);
