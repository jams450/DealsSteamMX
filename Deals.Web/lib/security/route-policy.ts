const publicSessionPaths = new Set([
  "/api/auth/login",
  "/api/auth/refresh",
  "/api/auth/session"
]);

// Add one-off public pages or APIs here. Keep /api/bff routes authenticated.
const publicApplicationPaths = new Set<string>();

// Add a concrete prefix here only when every descendant should be public.
const publicApplicationPrefixes: readonly string[] = [];

export function isPublicRoute(pathname: string): boolean {
  return (
    publicSessionPaths.has(pathname) ||
    publicApplicationPaths.has(pathname) ||
    publicApplicationPrefixes.some((prefix) => pathname.startsWith(`${prefix}/`))
  );
}
