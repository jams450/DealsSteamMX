import { createHash } from "node:crypto";

export class RefreshCapacityError extends Error {
  constructor() { super("Refresh temporarily unavailable"); }
}

// Process-local only. Never evict pending work: that would allow duplicate rotation.
export function createRefreshCoordinator(options: {
  ttlMs?: number;
  capacity?: number;
  now?: () => number;
} = {}) {
  const { ttlMs = 5_000, capacity = 256, now = Date.now } = options;
  const entries = new Map<string, { promise: Promise<unknown>; expiresAt: number }>();

  return function coordinate<T>(token: string, rotate: () => Promise<T | null>): Promise<T | null> {
    const key = createHash("sha256").update(token).digest("hex");
    const time = now();
    for (const [digest, entry] of entries) {
      if (entry.expiresAt <= time) entries.delete(digest);
    }
    const existing = entries.get(key);
    if (existing) return existing.promise as Promise<T | null>;
    if (entries.size >= capacity) {
      for (const [digest, entry] of entries) {
        if (entry.expiresAt !== Infinity) {
          entries.delete(digest);
          break;
        }
      }
    }
    // Pending saturation is transient, never an authentication rejection.
    if (entries.size >= capacity) return Promise.reject(new RefreshCapacityError());

    const entry = { promise: Promise.resolve(null) as Promise<T | null>, expiresAt: Infinity };
    entry.promise = Promise.resolve().then(rotate).then(result => {
      if (result === null) entries.delete(key);
      else entry.expiresAt = now() + ttlMs;
      return result;
    }, error => {
      entries.delete(key);
      throw error;
    });
    entries.set(key, entry);
    return entry.promise;
  };
}

export const coordinateRefresh = createRefreshCoordinator();
