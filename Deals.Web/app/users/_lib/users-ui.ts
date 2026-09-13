import type { AdminUser } from "@/lib/contracts/users-admin";

const badgeBase = "tabler-badge tabler-badge-solid";

export function getUserRoleLabel(user: AdminUser) {
  return user.admin ? "Admin" : "Usuario";
}

export function getUserRoleBadgeClass(user: AdminUser) {
  return `${badgeBase} ${user.admin ? "tabler-badge-primary" : "tabler-badge-muted"}`;
}

export function getUserStatusLabel(user: AdminUser) {
  return user.active ? "Activo" : "Inactivo";
}

export function getUserStatusBadgeClass(user: AdminUser) {
  return `${badgeBase} ${user.active ? "tabler-badge-success" : "tabler-badge-danger"}`;
}
