/** Concurrent callers with the same key share one in-flight call (UI-AUTH-8: concurrent calls share one refresh). */
export function singleFlight<T>(
  store: Map<string, Promise<T>>,
  key: string,
  run: () => Promise<T>,
): Promise<T> {
  const existing = store.get(key);
  if (existing) return existing;
  const promise = run().finally(() => {
    store.delete(key);
  });
  store.set(key, promise);
  return promise;
}
