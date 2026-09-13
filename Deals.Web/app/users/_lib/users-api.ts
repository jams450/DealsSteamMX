import { csrfFetch } from "@/lib/security/csrf-client";
import { normalizeUsers, type AdminUser, type UserCreatePayload, type UserUpdatePayload } from "@/lib/contracts/users-admin";
import { parseApiError } from "@/lib/bff/client-session";

export async function listUsers(): Promise<AdminUser[]> {
  const response = await fetch("/api/bff/users", { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudieron cargar usuarios");
  const payload = await response.json();
  return normalizeUsers(payload);
}

export async function createUser(payload: UserCreatePayload): Promise<void> {
  const response = await csrfFetch("/api/bff/users", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });

  if (!response.ok) throw await parseApiError(response, "No se pudo guardar usuario");
}

export async function updateUser(userId: number, payload: UserUpdatePayload): Promise<void> {
  const response = await csrfFetch(`/api/bff/users/${userId}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });

  if (!response.ok) throw await parseApiError(response, "No se pudo guardar usuario");
}

export async function patchUserActive(userId: number, active: boolean): Promise<void> {
  const response = await csrfFetch(`/api/bff/users/${userId}/active`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ active })
  });

  if (!response.ok) throw await parseApiError(response, "No se pudo actualizar estado");
}

export async function deleteUser(userId: number): Promise<void> {
  const response = await csrfFetch(`/api/bff/users/${userId}`, { method: "DELETE" });
  if (!response.ok) throw await parseApiError(response, "No se pudo borrar usuario");
}
