export class MemoryTtlCache<K, V> {
  private readonly store = new Map<K, { value: V; expiresAt: number }>();

  constructor(private readonly defaultTtlMs: number) {}

  get(key: K): V | undefined {
    const entry = this.store.get(key);
    if (!entry) return undefined;
    if (entry.expiresAt <= Date.now()) {
      this.store.delete(key);
      return undefined;
    }
    return entry.value;
  }

  set(key: K, value: V, ttlMs = this.defaultTtlMs): V {
    this.store.set(key, { value, expiresAt: Date.now() + ttlMs });
    return value;
  }

  delete(key: K) {
    this.store.delete(key);
  }

  clear() {
    this.store.clear();
  }

  async getOrLoad(key: K, loader: () => Promise<V>, ttlMs = this.defaultTtlMs): Promise<V> {
    const cached = this.store.get(key);
    if (cached && cached.expiresAt > Date.now()) return cached.value;
    const value = await loader();
    this.store.set(key, { value, expiresAt: Date.now() + ttlMs });
    return value;
  }
}
