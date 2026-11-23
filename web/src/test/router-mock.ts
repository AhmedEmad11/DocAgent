import { vi } from "vitest";

/** Shared `next/navigation` stand-in for component tests (installed in vitest.setup.ts). Assert on `refresh`/`push`. */
export const routerMock = {
  refresh: vi.fn(),
  push: vi.fn(),
  replace: vi.fn(),
  back: vi.fn(),
  prefetch: vi.fn(),
};
export const navigationMock = {
  useRouter: () => routerMock,
  usePathname: () => "/en",
  useSearchParams: () => new URLSearchParams(),
  redirect: vi.fn(),
  notFound: vi.fn(),
};
