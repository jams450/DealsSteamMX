"use client";

import { useCallback, useEffect, useState } from "react";
import type { AdminUser, UserCreatePayload, UserUpdatePayload } from "@/lib/contracts/users-admin";
import { createUser, deleteUser, listUsers, patchUserActive, updateUser } from "../_lib/users-api";

export function useUsersAdmin() {
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setUsers(await listUsers());
    } catch (err) {
      setError(err instanceof Error ? err.message : "No se pudieron cargar usuarios");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const create = useCallback(async (payload: UserCreatePayload) => {
    setError(null);
    await createUser(payload);
    await reload();
  }, [reload]);

  const update = useCallback(async (userId: number, payload: UserUpdatePayload) => {
    setError(null);
    await updateUser(userId, payload);
    await reload();
  }, [reload]);

  const toggleActive = useCallback(async (user: AdminUser) => {
    setError(null);
    await patchUserActive(user.userId, !user.active);
    await reload();
  }, [reload]);

  const remove = useCallback(async (userId: number) => {
    setError(null);
    await deleteUser(userId);
    await reload();
  }, [reload]);

  return {
    users,
    loading,
    error,
    setError,
    reload,
    create,
    update,
    toggleActive,
    remove
  };
}
