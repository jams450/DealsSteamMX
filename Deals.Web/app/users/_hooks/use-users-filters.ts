"use client";

import { useMemo, useState } from "react";
import type { AdminUser } from "@/lib/contracts/users-admin";

export type UsersStatusFilter = "all" | "active" | "inactive";
export type UsersRoleFilter = "all" | "admin" | "user";

export function useUsersFilters(users: AdminUser[]) {
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState<UsersStatusFilter>("all");
  const [role, setRole] = useState<UsersRoleFilter>("all");

  const filteredUsers = useMemo(() => {
    return users.filter((user) => {
      const query = search.trim().toLowerCase();
      if (query && !user.name.toLowerCase().includes(query) && !user.email.toLowerCase().includes(query)) return false;
      if (status === "active" && !user.active) return false;
      if (status === "inactive" && user.active) return false;
      if (role === "admin" && !user.admin) return false;
      if (role === "user" && user.admin) return false;
      return true;
    });
  }, [role, search, status, users]);

  const hasActiveFilters = search.trim().length > 0 || status !== "all" || role !== "all";

  function resetFilters() {
    setSearch("");
    setStatus("all");
    setRole("all");
  }

  return {
    search,
    status,
    role,
    setSearch,
    setStatus,
    setRole,
    filteredUsers,
    hasActiveFilters,
    resetFilters
  };
}
